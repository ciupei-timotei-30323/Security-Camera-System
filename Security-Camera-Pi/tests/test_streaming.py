from __future__ import annotations

import contextlib
import io
import logging
import threading

import cv2
import numpy as np
import pytest

from security_camera_pi.camera import Camera
from security_camera_pi.config import Config, ConfigError
from security_camera_pi.streaming import (
    RtspStreamer,
    StreamConfigurationError,
    build_ffmpeg_command,
)


class _ListHandler(logging.Handler):
    def __init__(self, sink: list[logging.LogRecord]) -> None:
        super().__init__()
        self.sink = sink

    def emit(self, record: logging.LogRecord) -> None:
        self.sink.append(record)


@contextlib.contextmanager
def _capture_logs(name: str):
    """Collects LogRecords; `caplog` is broken in this environment's pytest build."""
    records: list[logging.LogRecord] = []
    logger = logging.getLogger(name)
    handler = _ListHandler(records)
    previous_level = logger.level
    logger.addHandler(handler)
    logger.setLevel(logging.DEBUG)
    try:
        yield records
    finally:
        logger.removeHandler(handler)
        logger.setLevel(previous_level)


def _config(**overrides: object) -> Config:
    base: dict[str, object] = {
        "backend_url": "http://backend:8080",
        "api_key": "camera-key",
        "system_id": "sys-001",
        "stream_mode": "webrtc",
        "rtsp_host": "mediamtx",
        "rtsp_port": 8554,
        "rtsp_user": "pi",
        "stream_width": 64,
        "stream_height": 48,
        "stream_fps": 5,
        "stream_bitrate_kbps": 400,
    }
    base.update(overrides)
    return Config(**base)  # type: ignore[arg-type]


def _config_from_env(monkeypatch: pytest.MonkeyPatch, **env: str) -> Config:
    monkeypatch.setenv("BACKEND_URL", "http://backend:8080")
    monkeypatch.setenv("API_KEY", "camera-key")
    for key, value in env.items():
        monkeypatch.setenv(key, value)
    return Config.from_env(env_file=None)


def test_resolved_rtsp_url_embeds_credentials_and_system_path() -> None:
    config = _config()

    assert config.uses_webrtc is True
    assert config.resolved_rtsp_url == "rtsp://pi:camera-key@mediamtx:8554/sys-001"


def test_stream_rtsp_url_override_wins() -> None:
    config = _config(stream_rtsp_url="rtsps://pi:key@cam.example.com:8322/sys-001")

    assert config.resolved_rtsp_url == "rtsps://pi:key@cam.example.com:8322/sys-001"


def test_mjpeg_mode_has_no_rtsp_url() -> None:
    config = _config(stream_mode="mjpeg")

    assert config.uses_webrtc is False
    assert config.resolved_rtsp_url is None


def test_rtsp_url_is_none_without_host_or_system_id() -> None:
    assert _config(rtsp_host=None).resolved_rtsp_url is None
    assert _config(system_id=None).resolved_rtsp_url is None


def test_from_env_requires_system_id_in_webrtc_mode(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    with pytest.raises(ConfigError, match="SYSTEM_ID is required"):
        _config_from_env(monkeypatch, STREAM_MODE="webrtc")


def test_from_env_accepts_webrtc_with_explicit_rtsp_url(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    config = _config_from_env(
        monkeypatch,
        STREAM_MODE="webrtc",
        STREAM_RTSP_URL="rtsp://pi:key@mediamtx:8554/sys-001",
    )

    assert config.resolved_rtsp_url == "rtsp://pi:key@mediamtx:8554/sys-001"


def test_from_env_rejects_unknown_stream_mode(monkeypatch: pytest.MonkeyPatch) -> None:
    with pytest.raises(ConfigError, match="STREAM_MODE must be one of"):
        _config_from_env(monkeypatch, STREAM_MODE="hls")


def test_from_env_defaults_rtsp_host_to_backend_host(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    config = _config_from_env(monkeypatch, SYSTEM_ID="sys-001")

    assert config.rtsp_host == "backend"
    assert config.resolved_rtsp_url == "rtsp://pi:camera-key@backend:8554/sys-001"


def test_ffmpeg_command_uses_low_latency_flags() -> None:
    command = build_ffmpeg_command(_config(), "rtsp://pi:key@mediamtx:8554/sys-001")

    assert command[0] == "ffmpeg"
    assert "-f" in command and "rawvideo" in command
    assert "bgr24" in command
    assert "64x48" in command
    assert "yuv420p" in command
    # -g 30 style GOP: one keyframe per second at 5 fps.
    assert command[command.index("-g") + 1] == "5"
    assert command[-5:] == [
        "-f",
        "rtsp",
        "-rtsp_transport",
        "tcp",
        "rtsp://pi:key@mediamtx:8554/sys-001",
    ]
    assert "-preset" in command and "ultrafast" in command
    assert "-tune" in command and "zerolatency" in command


def test_ffmpeg_command_omits_libx264_flags_for_hardware_encoder() -> None:
    command = build_ffmpeg_command(
        _config(stream_ffmpeg_encoder="h264_v4l2m2m"),
        "rtsp://pi:key@mediamtx:8554/sys-001",
    )

    assert "h264_v4l2m2m" in command
    assert "-preset" not in command
    assert "-tune" not in command


def test_streamer_reports_missing_rtsp_url() -> None:
    stop = threading.Event()
    streamer = RtspStreamer(object(), _config(rtsp_host=None), stop)  # type: ignore[arg-type]

    with pytest.raises(StreamConfigurationError, match="SYSTEM_ID"):
        streamer._resolve_command()


class _FakeStdin(io.BytesIO):
    def __init__(self, on_write=None) -> None:
        super().__init__()
        self._on_write = on_write
        self.closed_by_streamer = False

    def write(self, data: bytes) -> int:  # type: ignore[override]
        if self._on_write is not None:
            self._on_write()
        return super().write(data)

    def flush(self) -> None:
        return None

    def close(self) -> None:
        self.closed_by_streamer = True
        super().close()


class _FakeProcess:
    """Minimal Popen stand-in: one frame then a dead pipe."""

    def __init__(self, stdin: _FakeStdin, stderr=None) -> None:
        self.stdin = stdin
        self.stderr = stderr
        self.returncode: int | None = None

    def poll(self) -> int | None:
        return self.returncode

    def wait(self, timeout: float | None = None) -> int:
        self.returncode = 0
        return 0

    def kill(self) -> None:
        self.returncode = -9


class _FakeCamera:
    def __init__(self, frame: np.ndarray) -> None:
        self.frame = frame
        self.calls = 0

    def capture_stream_frame(self, size: tuple[int, int]) -> np.ndarray:
        self.calls += 1
        return self.frame


def test_streamer_writes_frames_and_restarts_on_dead_pipe() -> None:
    frame = np.zeros((48, 64, 3), dtype=np.uint8)
    camera = _FakeCamera(frame)
    stop = threading.Event()
    processes: list[_FakeProcess] = []
    first_process_writes = [0]

    def _popen(*_args: object, **_kwargs: object) -> _FakeProcess:
        def _break_pipe() -> None:
            if len(processes) == 1:
                # The first publisher's second frame hits a closed RTSP socket, like a dropped
                # MediaMTX connection, so the supervisor must restart it.
                first_process_writes[0] += 1
                if first_process_writes[0] >= 2:
                    raise BrokenPipeError("rtsp socket closed")
                return

            # The restarted publisher is asked to stop, so the supervisor exits cleanly.
            stop.set()

        process = _FakeProcess(_FakeStdin(_break_pipe))
        processes.append(process)
        return process

    streamer = RtspStreamer(
        camera,  # type: ignore[arg-type]
        _config(),
        stop,
        command=["ffmpeg", "-f", "rtsp", "rtsp://pi:key@mediamtx:8554/sys-001"],
        popen=_popen,  # type: ignore[arg-type]
    )
    streamer.run()

    assert len(processes) == 2
    assert camera.calls >= 2
    assert all(process.stdin.closed_by_streamer for process in processes)


def test_camera_stream_frame_resizes_main_buffer() -> None:
    full_size = (640, 360)
    source = np.zeros((full_size[1], full_size[0], 3), dtype=np.uint8)
    source[:, :] = (255, 0, 0)
    camera = object.__new__(Camera)
    camera._full_size = full_size
    camera._preview_size = (320, 180)
    camera._lock = threading.Lock()
    camera._started = True

    class _Picam:
        def capture_array(self, stream: str) -> np.ndarray:
            assert stream == "main"
            return source.copy()

    camera._picam2 = _Picam()  # type: ignore[assignment]

    frame = camera.capture_stream_frame((64, 48))

    assert frame.shape == (48, 64, 3)
    assert frame.dtype == np.uint8
    blue, green, red = frame.reshape(-1, 3).mean(axis=0)
    assert blue > 180 and green < 80 and red < 80


def test_camera_stream_frame_returns_main_buffer_when_sizes_match() -> None:
    full_size = (64, 48)
    source = np.zeros((full_size[1], full_size[0], 3), dtype=np.uint8)
    camera = object.__new__(Camera)
    camera._full_size = full_size
    camera._preview_size = (32, 24)
    camera._lock = threading.Lock()
    camera._started = True

    class _Picam:
        def capture_array(self, stream: str) -> np.ndarray:
            return source

    camera._picam2 = _Picam()  # type: ignore[assignment]

    frame = camera.capture_stream_frame(full_size)

    assert frame.shape == (48, 64, 3)


def test_streamer_command_uses_configured_dimensions() -> None:
    config = _config(stream_width=1280, stream_height=720, stream_fps=15)
    command = build_ffmpeg_command(config, "rtsp://pi:key@mediamtx:8554/sys-001")

    assert "1280x720" in command
    assert command[command.index("-r") + 1] == "15"
    assert command[command.index("-g") + 1] == "15"
    # cv2 import keeps the headless codec path exercised in CI (no display needed).
    assert cv2.__version__


def test_diagnose_ffmpeg_error_explains_auth_and_network_failures() -> None:
    from security_camera_pi.streaming import diagnose_ffmpeg_error

    unauthorized = (
        "[rtsp @ 0x1] method DESCRIBE failed: 401 Unauthorized\n"
        "rtsp://pi:***@mediamtx:8554/sys-001: Server returned 401 Unauthorized "
        "(authorization failed)"
    )
    hint = diagnose_ffmpeg_error(unauthorized)
    assert hint is not None
    assert "MediaMTX" in hint and "API_KEY" in hint and "SYSTEM_ID" in hint

    refused = "[tcp @ 0x2] Connection refused\nCould not open connection"
    refused_hint = diagnose_ffmpeg_error(refused)
    assert refused_hint is not None and "RTSP_HOST" in refused_hint

    encoder = "Unknown encoder 'h264_v4l2m2m'"
    encoder_hint = diagnose_ffmpeg_error(encoder)
    assert encoder_hint is not None and "libx264" in encoder_hint

    assert diagnose_ffmpeg_error("something entirely unrelated") is None


def test_redact_secrets_hides_key_and_url_userinfo() -> None:
    from security_camera_pi.streaming import redact_secrets

    text = (
        "rtsp://pi:camera-key@mediamtx:8554/sys-001: 401 Unauthorized "
        "(authorization failed) key=camera-key"
    )

    redacted = redact_secrets(text, ["camera-key"])

    assert "camera-key" not in redacted
    assert "rtsp://***@mediamtx:8554/sys-001" in redacted


def test_list_encoders_parses_ffmpeg_output() -> None:
    from security_camera_pi.streaming import list_encoders

    class _Result:
        returncode = 0
        stdout = (
            "Encoders:\n"
            " V..... = Video\n"
            " ------\n"
            " V....D libx264              libx264 H.264 / AVC (codec h264)\n"
            " V....D h264_v4l2m2m         V4L2 mem2mem H.264 encoder (codec h264)\n"
            " A....D aac                  AAC (Advanced Audio Coding)\n"
        )

    def _runner(*_args: object, **_kwargs: object) -> _Result:
        return _Result()

    assert list_encoders("ffmpeg", runner=_runner) == {"libx264", "h264_v4l2m2m", "aac"}


def test_list_encoders_returns_none_when_ffmpeg_is_missing() -> None:
    from security_camera_pi.streaming import list_encoders

    def _runner(*_args: object, **_kwargs: object) -> object:
        raise FileNotFoundError("ffmpeg")

    assert list_encoders("ffmpeg", runner=_runner) is None


def test_streamer_falls_back_to_libx264_when_encoder_missing() -> None:
    class _Result:
        returncode = 0
        stdout = " V....D libx264   libx264 H.264 / AVC (codec h264)\n"

    def _runner(*_args: object, **_kwargs: object) -> _Result:
        return _Result()

    with _capture_logs("security_camera_pi.streaming") as records:
        streamer = RtspStreamer(
            object(),  # type: ignore[arg-type]
            _config(stream_ffmpeg_encoder="h264_v4l2m2m"),
            threading.Event(),
            runner=_runner,
        )
        streamer._fallback_encoder_if_missing()

    assert streamer.config.stream_ffmpeg_encoder == "libx264"
    # The operator must learn why the configured encoder was ignored.
    warnings = [r for r in records if getattr(r, "event", "") == "ffmpeg_encoder_unavailable"]
    assert warnings and "libx264" in warnings[0].hint

    # A supported encoder is left alone and nothing is logged.
    with _capture_logs("security_camera_pi.streaming") as quiet:
        keep = RtspStreamer(
            object(),  # type: ignore[arg-type]
            _config(stream_ffmpeg_encoder="libx264"),
            threading.Event(),
            runner=_runner,
        )
        keep._fallback_encoder_if_missing()

    assert keep.config.stream_ffmpeg_encoder == "libx264"
    assert quiet == []


def test_streamer_keeps_encoder_when_ffmpeg_probe_fails() -> None:
    def _runner(*_args: object, **_kwargs: object) -> object:
        raise FileNotFoundError("ffmpeg")

    with _capture_logs("security_camera_pi.streaming") as records:
        streamer = RtspStreamer(
            object(),  # type: ignore[arg-type]
            _config(stream_ffmpeg_encoder="h264_v4l2m2m"),
            threading.Event(),
            runner=_runner,
        )
        streamer._fallback_encoder_if_missing()

    assert streamer.config.stream_ffmpeg_encoder == "h264_v4l2m2m"
    assert [r.event for r in records] == ["ffmpeg_encoders_unavailable"]


def test_publisher_logs_ffmpeg_stderr_and_hint_on_dead_pipe() -> None:
    frame = np.zeros((48, 64, 3), dtype=np.uint8)
    stop = threading.Event()

    class _Stderr(io.BytesIO):
        @property
        def closed(self) -> bool:  # keep _terminate's close() harmless
            return False

    def _break() -> None:
        stop.set()
        raise BrokenPipeError("rtsp socket closed")

    class _Process(_FakeProcess):
        def __init__(self) -> None:
            super().__init__(_FakeStdin(_break))
            self.stderr = _Stderr(
                b"[rtsp @ 0x1] method ANNOUNCE failed: 401 Unauthorized\n"
                b"rtsp://pi:camera-key@mediamtx:8554/sys-001: Server returned 401\n"
            )

    with _capture_logs("security_camera_pi.streaming") as records:
        streamer = RtspStreamer(
            _FakeCamera(frame),  # type: ignore[arg-type]
            _config(),
            stop,
            command=["ffmpeg"],
            popen=lambda *_a, **_k: _Process(),  # type: ignore[arg-type]
        )
        streamer._publish_once(["ffmpeg"])

    closed = [r for r in records if getattr(r, "event", "") == "rtsp_publisher_pipe_closed"]
    assert closed, [getattr(r, "event", r.getMessage()) for r in records]
    details = closed[0]
    assert "401 Unauthorized" in details.ffmpeg_stderr
    # Never leak the Pi API key through ffmpeg's stderr echo.
    assert "camera-key" not in details.ffmpeg_stderr
    assert "MediaMTX" in details.hint


def test_repeated_identical_failures_are_logged_once() -> None:
    frame = np.zeros((48, 64, 3), dtype=np.uint8)
    stop = threading.Event()

    class _Stderr(io.BytesIO):
        @property
        def closed(self) -> bool:
            return False

    def _break() -> None:
        # Every write fails: the publisher is already dead, exactly like a 401 at startup.
        raise BrokenPipeError("rtsp socket closed")

    class _Process(_FakeProcess):
        def __init__(self) -> None:
            super().__init__(_FakeStdin(_break))
            self.stderr = _Stderr(b"[tcp @ 0x2] Connection refused\n")

    with _capture_logs("security_camera_pi.streaming") as records:
        streamer = RtspStreamer(
            _FakeCamera(frame),  # type: ignore[arg-type]
            _config(),
            stop,
            command=["ffmpeg"],
            popen=lambda *_a, **_k: _Process(),  # type: ignore[arg-type]
        )
        streamer._publish_once(["ffmpeg"])
        streamer._publish_once(["ffmpeg"])

    reasons = [r.event for r in records if getattr(r, "event", "")]
    assert reasons.count("rtsp_publisher_pipe_closed") == 1
    assert reasons.count("rtsp_publisher_pipe_closed_repeat") == 1
