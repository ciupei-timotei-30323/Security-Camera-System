"""Capture daemon orchestration."""

from __future__ import annotations

from datetime import datetime, timezone
import logging
import threading
import time
import uuid

from .backend import (
    BackendClient,
    PermanentBackendError,
    TransientBackendError,
)
from .camera import Camera
from .config import Config
from .motion import MotionDetector
from .preview import PreviewPusher
from .queue import OfflineQueue, QueueProcessor
from .streaming import RtspStreamer

logger = logging.getLogger(__name__)

MOTION_INTERVAL_SECONDS = 0.2


def utc_now_iso() -> str:
    return (
        datetime.now(timezone.utc)
        .isoformat(timespec="milliseconds")
        .replace("+00:00", "Z")
    )


class CaptureDaemon:
    def __init__(
        self,
        config: Config,
        camera: Camera,
        backend: BackendClient,
        queue: OfflineQueue,
        *,
        motion_interval: float = MOTION_INTERVAL_SECONDS,
    ) -> None:
        self.config = config
        self.camera = camera
        self.backend = backend
        self.queue = queue
        self.motion_interval = motion_interval
        self.stop_event = threading.Event()
        self.detector = MotionDetector(config.motion_min_area)
        # ``webrtc`` mode adds the H.264/RTSP publisher on top of the existing daemon. The JPEG
        # preview keeps running (``PREVIEW_ENABLED``) so the dashboard's MJPEG fallback still has
        # frames to show when WebRTC is unavailable; ``mjpeg`` mode is the original behaviour.
        self.preview_pusher = PreviewPusher(
            camera,
            backend,
            config.preview_interval,
            self.stop_event,
        )
        self.queue_processor = QueueProcessor(
            queue,
            backend,
            self.stop_event,
        )
        self.streamer: RtspStreamer | None = None
        if config.uses_webrtc and config.resolved_rtsp_url:
            self.streamer = RtspStreamer(camera, config, self.stop_event)
        self._shutdown_lock = threading.Lock()
        self._shutdown_done = False

    def request_stop(self) -> None:
        self.stop_event.set()

    def run(self) -> None:
        self.queue.enforce_limit()
        logger.info(
            "capture_daemon_starting",
            extra={
                "event": "capture_daemon_starting",
                "backend_url": self.config.backend_url,
                "motion_min_area": self.config.motion_min_area,
                "cooldown_seconds": self.config.cooldown_seconds,
                "preview_interval": self.config.preview_interval,
                "queue_max_mb": self.config.queue_max_mb,
                "simulate_offline": self.backend.simulate_offline,
                "stream_mode": self.config.stream_mode,
                "rtsp_streaming": self.streamer is not None,
            },
        )

        if self.config.uses_webrtc and self.streamer is None:
            logger.warning(
                "rtsp_streaming_disabled",
                extra={
                    "event": "rtsp_streaming_disabled",
                    "reason": "STREAM_MODE=webrtc but no RTSP URL could be resolved",
                },
            )

        self.camera.start()
        try:
            self.queue_processor.start()
            if self.streamer is not None:
                self.streamer.start()
            if self.config.preview_enabled:
                self.preview_pusher.start()
            logger.info(
                "capture_daemon_started",
                extra={"event": "capture_daemon_started"},
            )
            self._motion_loop()
        finally:
            self.shutdown()

    def shutdown(self) -> None:
        with self._shutdown_lock:
            if self._shutdown_done:
                return
            self._shutdown_done = True
            self.stop_event.set()

            logger.info(
                "capture_daemon_stopping",
                extra={"event": "capture_daemon_stopping"},
            )

            threads = [self.preview_pusher, self.queue_processor]
            if self.streamer is not None:
                threads.append(self.streamer)
            for thread in threads:
                if thread.ident is not None:
                    thread.join(timeout=5.0)
                    if thread.is_alive():
                        logger.warning(
                            "thread_did_not_stop",
                            extra={
                                "event": "thread_did_not_stop",
                                "thread": thread.name,
                            },
                        )

            try:
                self.camera.close()
            except Exception as exc:  # noqa: BLE001 - shutdown must finish
                logger.warning(
                    "camera_close_failed",
                    extra={
                        "event": "camera_close_failed",
                        "error_type": type(exc).__name__,
                    },
                )

            logger.info(
                "capture_daemon_stopped",
                extra={"event": "capture_daemon_stopped"},
            )

    def _motion_loop(self) -> None:
        next_capture = time.monotonic()
        cooldown_until = 0.0

        while not self.stop_event.is_set():
            now = time.monotonic()
            if now < next_capture:
                self.stop_event.wait(min(next_capture - now, 0.1))
                continue

            next_capture = time.monotonic() + self.motion_interval

            try:
                gray = self.camera.capture_motion_gray()
            except Exception as exc:  # noqa: BLE001 - retry on transient camera errors
                logger.warning(
                    "motion_frame_capture_failed",
                    extra={
                        "event": "motion_frame_capture_failed",
                        "error_type": type(exc).__name__,
                    },
                )
                self.stop_event.wait(1.0)
                continue

            result = self.detector.process(gray)
            now = time.monotonic()

            if result.motion and now >= cooldown_until:
                cooldown_until = now + self.config.cooldown_seconds
                logger.info(
                    "motion_detected",
                    extra={
                        "event": "motion_detected",
                        "max_contour_area": round(result.max_area, 1),
                        "cooldown_seconds": self.config.cooldown_seconds,
                    },
                )
                self._capture_and_send()

    def _capture_and_send(self) -> None:
        client_event_id = str(uuid.uuid4())
        captured_at = utc_now_iso()

        try:
            image = self.camera.capture_still_jpeg()
        except Exception as exc:  # noqa: BLE001 - keep the motion loop alive
            logger.error(
                "still_capture_failed",
                extra={
                    "event": "still_capture_failed",
                    "client_event_id": client_event_id,
                    "error_type": type(exc).__name__,
                },
            )
            return

        try:
            self.backend.post_event(
                image,
                client_event_id,
                captured_at,
                is_offline_sync=False,
            )
        except TransientBackendError as exc:
            logger.warning(
                "event_upload_offline",
                extra={
                    "event": "event_upload_offline",
                    "client_event_id": client_event_id,
                    "captured_at": captured_at,
                    "status_code": exc.status_code,
                    "error": str(exc),
                },
            )
            self._queue_event(image, client_event_id, captured_at)
        except PermanentBackendError as exc:
            # A malformed/unauthorized request would fail identically from the
            # queue, so keep the event out of the retry loop.
            logger.error(
                "event_upload_rejected",
                extra={
                    "event": "event_upload_rejected",
                    "client_event_id": client_event_id,
                    "captured_at": captured_at,
                    "status_code": exc.status_code,
                    "error": str(exc),
                },
            )
        except Exception as exc:  # noqa: BLE001 - never lose a captured still
            logger.error(
                "event_upload_failed",
                extra={
                    "event": "event_upload_failed",
                    "client_event_id": client_event_id,
                    "captured_at": captured_at,
                    "error_type": type(exc).__name__,
                },
            )
            self._queue_event(image, client_event_id, captured_at)
        else:
            logger.info(
                "event_uploaded",
                extra={
                    "event": "event_uploaded",
                    "client_event_id": client_event_id,
                    "captured_at": captured_at,
                },
            )

    def _queue_event(
        self,
        image: bytes,
        client_event_id: str,
        captured_at: str,
    ) -> None:
        try:
            self.queue.enqueue(image, client_event_id, captured_at)
        except Exception as exc:  # noqa: BLE001 - log and keep daemon running
            logger.error(
                "offline_queue_write_failed",
                extra={
                    "event": "offline_queue_write_failed",
                    "client_event_id": client_event_id,
                    "captured_at": captured_at,
                    "error_type": type(exc).__name__,
                },
            )
