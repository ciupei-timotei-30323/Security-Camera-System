"""H.264 RTSP publisher for the MediaMTX WebRTC path.

The daemon already has exactly one Picamera2 owner (``Camera``), so this module does not open a
second camera. It pulls BGR frames from the existing ``Camera`` and pipes them as rawvideo into a
single long-lived ``ffmpeg`` process that encodes H.264 and pushes it to MediaMTX over RTSP.

Why a subprocess instead of ``Picamera2``'s ``H264Encoder``/``FfmpegOutput``:

* The encoder is configured from environment variables without rebuilding the camera pipeline.
* Raspberry Pi 5 hardware encoding is available through ``h264_v4l2m2m``; the plain software
  ``libx264`` path works everywhere else. ``STREAM_FFMPEG_ENCODER`` selects one.
* A dead RTSP socket is just a dead process: the supervisor restarts it with backoff, which is far
  easier to reason about than recovering an in-process encoder.

The event upload path and the offline queue are untouched: this thread only adds video egress.
"""

from __future__ import annotations

import logging
import subprocess
import threading
import time
from typing import Sequence

from .camera import Camera
from .config import Config

logger = logging.getLogger(__name__)

# Restart backoff for the publisher process. Capped so a broker/MediaMTX outage recovers quickly
# once it comes back, but slow enough not to spin the CPU.
MIN_RESTART_DELAY_SECONDS = 1.0
MAX_RESTART_DELAY_SECONDS = 30.0
# How long to wait for ffmpeg to exit after closing stdin before killing it.
STOP_GRACE_SECONDS = 5.0


class StreamConfigurationError(RuntimeError):
    """Raised when the daemon cannot build a publisher command."""


def build_ffmpeg_command(config: Config, rtsp_url: str) -> list[str]:
    """Builds the low-latency H.264 -> RTSP command.

    Flags mirror the low-latency profile requested for the WebRTC path:
    ``-preset ultrafast -tune zerolatency -g 30 -f rtsp -rtsp_transport tcp``.
    """

    width = config.stream_width
    height = config.stream_height
    fps = config.stream_fps
    encoder = config.stream_ffmpeg_encoder or "libx264"

    command = [
        config.ffmpeg_path,
        "-hide_banner",
        "-loglevel",
        "error",
        # Input: raw BGR frames on stdin, exactly as Camera produces them.
        "-f",
        "rawvideo",
        "-pix_fmt",
        "bgr24",
        "-s",
        f"{width}x{height}",
        "-r",
        str(fps),
        "-i",
        "pipe:0",
        # Output: H.264, keyframe every second, CBR-ish target bitrate.
        "-c:v",
        encoder,
        "-pix_fmt",
        "yuv420p",
        "-b:v",
        f"{config.stream_bitrate_kbps}k",
        "-maxrate",
        f"{config.stream_bitrate_kbps}k",
        "-bufsize",
        f"{config.stream_bitrate_kbps * 2}k",
        "-g",
        str(fps),
    ]

    if encoder == "libx264":
        # These two options only exist for libx264; h264_v4l2m2m rejects them.
        command += ["-preset", "ultrafast", "-tune", "zerolatency"]

    command += [
        "-f",
        "rtsp",
        "-rtsp_transport",
        "tcp",
        rtsp_url,
    ]
    return command


class RtspStreamer(threading.Thread):
    """Supervises the ffmpeg publisher, restarting it whenever the RTSP socket drops."""

    def __init__(
        self,
        camera: Camera,
        config: Config,
        stop_event: threading.Event,
        *,
        rtsp_url: str | None = None,
        command: Sequence[str] | None = None,
        popen: "type[subprocess.Popen[bytes]]" = subprocess.Popen,
    ) -> None:
        super().__init__(name="rtsp-streamer", daemon=True)
        self.camera = camera
        self.config = config
        self.stop_event = stop_event
        self._rtsp_url = rtsp_url if rtsp_url is not None else config.resolved_rtsp_url
        self._command = list(command) if command is not None else None
        self._popen = popen

    def _resolve_command(self) -> list[str]:
        if self._command is not None:
            return self._command
        if not self._rtsp_url:
            raise StreamConfigurationError(
                "WebRTC streaming requires SYSTEM_ID (or STREAM_RTSP_URL) plus RTSP_HOST"
            )
        return build_ffmpeg_command(self.config, self._rtsp_url)

    def run(self) -> None:
        try:
            command = self._resolve_command()
        except StreamConfigurationError as exc:
            logger.error(
                "rtsp_streamer_misconfigured",
                extra={"event": "rtsp_streamer_misconfigured", "error": str(exc)},
            )
            return

        logger.info(
            "rtsp_streamer_started",
            extra={
                "event": "rtsp_streamer_started",
                "width": self.config.stream_width,
                "height": self.config.stream_height,
                "fps": self.config.stream_fps,
                "bitrate_kbps": self.config.stream_bitrate_kbps,
                "encoder": self.config.stream_ffmpeg_encoder or "libx264",
                # The URL embeds the Pi API key, so only the host/path are logged.
                "rtsp_target": _redact_rtsp_url(self._rtsp_url or ""),
            },
        )

        delay = MIN_RESTART_DELAY_SECONDS
        while not self.stop_event.is_set():
            started = time.monotonic()
            try:
                self._publish_once(command)
            except StreamConfigurationError as exc:
                logger.error(
                    "rtsp_streamer_misconfigured",
                    extra={"event": "rtsp_streamer_misconfigured", "error": str(exc)},
                )
                return
            except Exception as exc:  # noqa: BLE001 - the supervisor must survive
                logger.warning(
                    "rtsp_streamer_crashed",
                    extra={
                        "event": "rtsp_streamer_crashed",
                        "error_type": type(exc).__name__,
                        "error": str(exc),
                    },
                )

            if self.stop_event.is_set():
                break

            # A publisher that stayed up for a while is not in a crash loop; reset the backoff.
            if time.monotonic() - started > MAX_RESTART_DELAY_SECONDS:
                delay = MIN_RESTART_DELAY_SECONDS

            logger.warning(
                "rtsp_streamer_restarting",
                extra={
                    "event": "rtsp_streamer_restarting",
                    "delay_seconds": delay,
                },
            )
            self.stop_event.wait(delay)
            delay = min(delay * 2, MAX_RESTART_DELAY_SECONDS)

        logger.info("rtsp_streamer_stopped", extra={"event": "rtsp_streamer_stopped"})

    def _publish_once(self, command: list[str]) -> None:
        """Runs one ffmpeg process until it exits or the daemon stops."""

        process = self._popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        stdin = process.stdin
        if stdin is None:  # pragma: no cover - Popen(stdin=PIPE) always provides this
            raise StreamConfigurationError("ffmpeg process has no stdin pipe")

        frame_interval = 1.0 / self.config.stream_fps
        next_frame = time.monotonic()

        try:
            while not self.stop_event.is_set():
                now = time.monotonic()
                if now < next_frame:
                    self.stop_event.wait(min(next_frame - now, frame_interval))
                    continue
                next_frame = max(next_frame + frame_interval, now)

                try:
                    frame = self.camera.capture_stream_frame(
                        (self.config.stream_width, self.config.stream_height)
                    )
                except Exception as exc:  # noqa: BLE001 - camera hiccup: skip the frame
                    logger.debug(
                        "stream_frame_capture_failed",
                        extra={
                            "event": "stream_frame_capture_failed",
                            "error_type": type(exc).__name__,
                        },
                    )
                    self.stop_event.wait(0.05)
                    continue

                try:
                    stdin.write(frame.tobytes())
                    stdin.flush()
                except (BrokenPipeError, OSError) as exc:
                    # ffmpeg died (RTSP dropped, MediaMTX refused the key, ...). Return so the
                    # supervisor restarts with backoff.
                    logger.warning(
                        "rtsp_publisher_pipe_closed",
                        extra={
                            "event": "rtsp_publisher_pipe_closed",
                            "error_type": type(exc).__name__,
                        },
                    )
                    return

                if process.poll() is not None:
                    logger.warning(
                        "rtsp_publisher_exited",
                        extra={
                            "event": "rtsp_publisher_exited",
                            "returncode": process.returncode,
                        },
                    )
                    return
        finally:
            _terminate(process)


def _terminate(process: "subprocess.Popen[bytes]") -> None:
    """Closes stdin and waits for ffmpeg, escalating to kill if it hangs."""

    try:
        if process.stdin is not None and not process.stdin.closed:
            process.stdin.close()
    except OSError:
        pass

    try:
        process.wait(timeout=STOP_GRACE_SECONDS)
    except subprocess.TimeoutExpired:
        process.kill()
        try:
            process.wait(timeout=STOP_GRACE_SECONDS)
        except subprocess.TimeoutExpired:  # pragma: no cover - kill should always land
            logger.warning(
                "rtsp_publisher_kill_failed",
                extra={"event": "rtsp_publisher_kill_failed"},
            )


def _redact_rtsp_url(url: str) -> str:
    """Drops the credentials from an RTSP URL so logs never contain the Pi API key."""

    scheme, separator, rest = url.partition("://")
    if not separator:
        return url
    _, at, host_part = rest.rpartition("@")
    if at:
        return f"{scheme}://{host_part}"
    return url
