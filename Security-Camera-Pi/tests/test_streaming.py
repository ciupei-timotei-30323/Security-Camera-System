from __future__ import annotations

import io
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

    def __init__(self, stdin: _FakeStdin) -> None:
        self.stdin = stdin
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
