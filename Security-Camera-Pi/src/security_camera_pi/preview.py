"""Background live-preview uploader."""

from __future__ import annotations

import logging
import threading
import time

from .backend import BackendClient
from .camera import Camera

logger = logging.getLogger(__name__)


class PreviewPusher(threading.Thread):
    """Posts a small JPEG to ``/api/camera/frame`` on a fixed cadence.

    Preview is best-effort: failures are deliberately silent at normal log
    levels so a backend outage does not flood the journal. The event/offline
    path carries the durable delivery guarantees.
    """

    def __init__(
        self,
        camera: Camera,
        backend: BackendClient,
        interval_seconds: float,
        stop_event: threading.Event,
    ) -> None:
        super().__init__(name="preview-pusher", daemon=True)
        self.camera = camera
        self.backend = backend
        self.interval_seconds = interval_seconds
        self.stop_event = stop_event

    def run(self) -> None:
        logger.info(
            "preview_pusher_started",
            extra={
                "event": "preview_pusher_started",
                "interval_seconds": self.interval_seconds,
            },
        )

        while not self.stop_event.is_set():
            started = time.monotonic()
            try:
                jpeg = self.camera.capture_preview_jpeg()
                self.backend.post_preview(jpeg)
            except Exception as exc:  # noqa: BLE001 - preview must never kill the daemon
                logger.debug(
                    "preview_push_failed",
                    extra={
                        "event": "preview_push_failed",
                        "error_type": type(exc).__name__,
                    },
                )

            elapsed = time.monotonic() - started
            self.stop_event.wait(max(0.0, self.interval_seconds - elapsed))

        logger.info(
            "preview_pusher_stopped",
            extra={"event": "preview_pusher_stopped"},
        )
