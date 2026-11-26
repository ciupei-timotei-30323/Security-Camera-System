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

import collections
import logging
import re
import subprocess
import threading
import time
from dataclasses import replace
from typing import Sequence
from urllib.parse import quote

from .camera import Camera
from .config import Config

logger = logging.getLogger(__name__)

# Restart backoff for the publisher process. Capped so a broker/MediaMTX outage recovers quickly
# once it comes back, but slow enough not to spin the CPU.
MIN_RESTART_DELAY_SECONDS = 1.0
MAX_RESTART_DELAY_SECONDS = 30.0
# How long to wait for ffmpeg to exit after closing stdin before killing it.
STOP_GRACE_SECONDS = 5.0
# How many ffmpeg stderr lines to keep for the failure log (ffmpeg's own explanation of the drop).
STDERR_TAIL_LINES = 20
# Always available: the software encoder every ffmpeg build ships with.
SOFTWARE_ENCODER = "libx264"
# Short wait for the stderr reader to catch up before reporting a failure.
STDERR_SETTLE_SECONDS = 0.2

_ENCODER_LINE_RE = re.compile(r"^[\s]*[A-Z.]{6}[\s]+([^\s=]\S*)", re.MULTILINE)


class StreamConfigurationError(RuntimeError):
    """Raised when the daemon cannot build a publisher command."""


# ffmpeg's own error text is the only place the real cause shows up, so each known signature maps
# to an operator-facing hint. Order matters: the first matching pattern wins.
_DIAGNOSTIC_HINTS = (
    (
        re.compile(r"401|Unauthorized|unauthori[sz]ed|authentication failed", re.IGNORECASE),
        "MediaMTX rejected the publish (401). The backend's MediaMTX auth hook denies the key when "
        "the API key is unknown for that path, or when the path (SYSTEM_ID) and the key belong to "
        "different systems. Check that API_KEY in pi.env equals the backend's key for SYSTEM_ID "
        "and that compose.yaml forwards PiAuth__Systems__<id> to the backend.",
    ),
    (
        re.compile(
            r"Connection refused|Connection timed out|No route to host|Network is unreachable"
        ),
        "MediaMTX is not reachable at RTSP_HOST:RTSP_PORT. On a Pi outside the Docker host this is "
        "usually a wrong RTSP_HOST/LAN address or a closed port 8554.",
    ),
    (
        re.compile(
            r"Unknown encoder|Encoder .* not found|Cannot load .*v4l2|Device or resource busy"
        ),
        "ffmpeg cannot use the configured encoder (STREAM_FFMPEG_ENCODER). Set it to libx264 for "
        "software encoding, or leave it empty.",
    ),
    (
        re.compile(r"No such file or directory"),
        "The ffmpeg binary could not be started or a configured path is missing (FFMPEG_PATH).",
    ),
)


def diagnose_ffmpeg_error(text: str) -> str | None:
    """Best-effort, action-oriented explanation for a failed publisher."""

    for pattern, hint in _DIAGNOSTIC_HINTS:
        if pattern.search(text):
            return hint
    return None


def list_encoders(ffmpeg_path: str, *, runner=subprocess.run) -> set[str] | None:
    """Names of the encoders this ffmpeg build offers, or None when ffmpeg cannot be run.

    ``ffmpeg -h encoder=<name>`` exits 0 even for unknown encoders on most builds, so the
    authoritative list is ``-encoders``.
    """

    try:
        result = runner(
            [ffmpeg_path, "-hide_banner", "-encoders"],
            capture_output=True,
            text=True,
            check=False,
        )
    except OSError:
        return None
    if result.returncode != 0:
        return None
    return set(_ENCODER_LINE_RE.findall(result.stdout or ""))


def redact_secrets(text: str, secrets: Sequence[str]) -> str:
    """Removes credentials from a string before it reaches the log.

    ffmpeg echoes the RTSP URL (which embeds the Pi API key) in its error messages, so both the
    literal secrets and any ``scheme://user:pass@host`` userinfo are stripped.
    """

    for secret in secrets:
        if secret:
            text = text.replace(secret, "***")
    return re.sub(r"(\w+://)[^\s/@]+@", r"\1***@", text)


class _StderrCollector(threading.Thread):
    """Keeps the last few ffmpeg stderr lines so a dropped publisher can be explained."""

    def __init__(self, stream, secrets: Sequence[str]) -> None:
        super().__init__(name="rtsp-stderr", daemon=True)
        self._stream = stream
        self._secrets = list(secrets)
        self.lines: collections.deque[str] = collections.deque(maxlen=STDERR_TAIL_LINES)

    def run(self) -> None:
        try:
            for raw in iter(self._stream.readline, b""):
                line = raw.decode("utf-8", "replace").strip()
                if line:
                    self.lines.append(redact_secrets(line, self._secrets))
        except (OSError, ValueError):  # pragma: no cover - pipe closed under us
            return

    def tail(self) -> str:
        return " | ".join(self.lines)


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
        runner=subprocess.run,
    ) -> None:
        super().__init__(name="rtsp-streamer", daemon=True)
        self.camera = camera
        self.config = config
        self.stop_event = stop_event
        self._rtsp_url = rtsp_url if rtsp_url is not None else config.resolved_rtsp_url
        self._command = list(command) if command is not None else None
        self._popen = popen
        self._runner = runner
        self._encoder_warning_logged = False
        # Repeated failures produce identical explanations; only log the full detail once.
        self._last_failure: tuple[str, str] | None = None

    def _secret_values(self) -> list[str]:
        """Strings that must never reach the log: the Pi API key and the URL password."""

        values = [self.config.api_key, self._rtsp_url or ""]
        # ffmpeg quotes the URL, sometimes percent-encoded, so add both forms.
        if self.config.api_key:
            values.append(quote(self.config.api_key, safe=""))
        return values

    def _fallback_encoder_if_missing(self) -> None:
        """Switches to libx264 when the configured encoder is not in this ffmpeg build.

        ``h264_v4l2m2m`` is only present on Raspberry Pi OS images built with the V4L2 m2m
        encoder; elsewhere every frame write fails the moment ffmpeg dies, which surfaces as an
        unexplained ``BrokenPipeError``. Software H.264 keeps the feed alive instead.
        """

        encoder = self.config.stream_ffmpeg_encoder
        if not encoder or encoder == SOFTWARE_ENCODER:
            return

        available = list_encoders(self.config.ffmpeg_path, runner=self._runner)
        if available is None:
            if not self._encoder_warning_logged:
                self._encoder_warning_logged = True
                logger.warning(
                    "ffmpeg_encoders_unavailable",
                    extra={
                        "event": "ffmpeg_encoders_unavailable",
                        "ffmpeg_path": self.config.ffmpeg_path,
                        "hint": "`ffmpeg -encoders` could not be executed; check FFMPEG_PATH. "
                        "Keeping STREAM_FFMPEG_ENCODER as configured.",
                    },
                )
            return
        if encoder in available:
            return

        self._encoder_warning_logged = True
        logger.warning(
            "ffmpeg_encoder_unavailable",
            extra={
                "event": "ffmpeg_encoder_unavailable",
                "encoder": encoder,
                "fallback": SOFTWARE_ENCODER,
                "hint": f"STREAM_FFMPEG_ENCODER={encoder} is not offered by this ffmpeg build. "
                f"Falling back to {SOFTWARE_ENCODER} (software encoding, higher CPU). Set "
                "STREAM_FFMPEG_ENCODER=libx264 to silence this, or install a build with the "
                "V4L2 hardware encoder.",
            },
        )
        self.config = replace(self.config, stream_ffmpeg_encoder=SOFTWARE_ENCODER)

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
            self._fallback_encoder_if_missing()
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
                "encoder": self.config.stream_ffmpeg_encoder or SOFTWARE_ENCODER,
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
            # Piped, not DEVNULL: ffmpeg's stderr is the only explanation for a dropped
            # publisher (401 from MediaMTX, missing encoder, unreachable host, ...).
            stderr=subprocess.PIPE,
        )
        stdin = process.stdin
        if stdin is None:  # pragma: no cover - Popen(stdin=PIPE) always provides this
            raise StreamConfigurationError("ffmpeg process has no stdin pipe")

        collector: _StderrCollector | None = None
        if process.stderr is not None:
            collector = _StderrCollector(process.stderr, self._secret_values())
            collector.start()

        def explain(reason: str) -> None:
            """Logs ffmpeg's own words plus an operator-facing hint."""

            # Give the reader thread a moment to drain what ffmpeg wrote before exiting, and let
            # the exit code settle so the log reports the real one instead of None.
            if collector is not None:
                collector.join(STDERR_SETTLE_SECONDS)
            if process.poll() is None:
                try:
                    process.wait(timeout=STDERR_SETTLE_SECONDS)
                except subprocess.TimeoutExpired:
                    pass
            tail = collector.tail() if collector is not None else ""
            details: dict[str, object] = {"returncode": process.poll()}
            if tail:
                details["ffmpeg_stderr"] = tail
                hint = diagnose_ffmpeg_error(tail)
                if hint:
                    details["hint"] = hint
            else:
                details["ffmpeg_stderr"] = "<no output>"
                details["hint"] = (
                    "ffmpeg exited without a message. Run it by hand on the Pi to reproduce: "
                    f"`{self.config.ffmpeg_path} -hide_banner -loglevel error ...` with the same "
                    "STREAM_* and RTSP_* values as pi.env."
                )
            signature = (reason, tail)
            repeat = signature == self._last_failure
            self._last_failure = signature
            if not repeat:
                logger.warning(reason, extra={"event": reason, **details})
            else:
                # Keep the restart history visible without repeating ffmpeg's whole message.
                logger.debug(
                    reason + "_repeat",
                    extra={"event": reason + "_repeat", "returncode": details["returncode"]},
                )

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
                    explain("rtsp_publisher_pipe_closed")
                    logger.debug(
                        "rtsp_publisher_pipe_error",
                        extra={
                            "event": "rtsp_publisher_pipe_error",
                            "error_type": type(exc).__name__,
                        },
                    )
                    return

                if process.poll() is not None:
                    explain("rtsp_publisher_exited")
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

    # The stderr pipe is pumped by _StderrCollector; close it so restart loops do not leak fds.
    if process.stderr is not None and not process.stderr.closed:
        try:
            process.stderr.close()
        except OSError:  # pragma: no cover
            pass


def _redact_rtsp_url(url: str) -> str:
    """Drops the credentials from an RTSP URL so logs never contain the Pi API key."""

    scheme, separator, rest = url.partition("://")
    if not separator:
        return url
    _, at, host_part = rest.rpartition("@")
    if at:
        return f"{scheme}://{host_part}"
    return url
