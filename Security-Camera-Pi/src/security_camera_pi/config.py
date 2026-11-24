"""Environment-based configuration for the capture daemon."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from urllib.parse import quote, urlsplit
import os

from dotenv import load_dotenv

DEFAULT_MOTION_MIN_AREA = 1500.0
DEFAULT_COOLDOWN_SECONDS = 8.0
DEFAULT_PREVIEW_INTERVAL = 1.0
DEFAULT_QUEUE_MAX_MB = 500.0
DEFAULT_REQUEST_TIMEOUT_SECONDS = 5.0

STREAM_MODE_WEBRTC = "webrtc"
STREAM_MODE_MJPEG = "mjpeg"
STREAM_MODES = (STREAM_MODE_WEBRTC, STREAM_MODE_MJPEG)

DEFAULT_STREAM_WIDTH = 1280
DEFAULT_STREAM_HEIGHT = 720
DEFAULT_STREAM_FPS = 15
DEFAULT_STREAM_BITRATE_KBPS = 2500
DEFAULT_RTSP_PORT = 8554
DEFAULT_RTSP_USER = "pi"


class ConfigError(ValueError):
    """Raised when an environment value is missing or invalid."""


def _bool(name: str, default: bool) -> bool:
    raw = _optional(name)
    if raw is None:
        return default
    lowered = raw.lower()
    if lowered in ("1", "true", "yes", "on"):
        return True
    if lowered in ("0", "false", "no", "off"):
        return False
    raise ConfigError(f"{name} must be a boolean (true/false), got {raw!r}")


def _backend_host(backend_url: str) -> str | None:
    """Best-effort MediaMTX host when ``RTSP_HOST`` is not configured.

    The backend and MediaMTX normally share a host in the reference deployment, so the backend's
    hostname is a sensible default. An explicit ``RTSP_HOST`` always wins.
    """

    try:
        host = urlsplit(backend_url).hostname
    except ValueError:
        return None
    return host or None


def _required(name: str) -> str:
    value = os.getenv(name, "").strip()
    if not value:
        raise ConfigError(f"{name} is required")
    return value


def _optional(name: str) -> str | None:
    """Returns the trimmed value, or None when the variable is unset/empty."""

    value = os.getenv(name)
    if value is None or not value.strip():
        return None
    return value.strip()


def _int(
    name: str,
    default: int,
    *,
    minimum: int | None = None,
) -> int:
    raw = _optional(name)
    if raw is None:
        value = default
    else:
        try:
            value = int(raw)
        except ValueError as exc:
            raise ConfigError(f"{name} must be an integer") from exc

    if minimum is not None and value < minimum:
        raise ConfigError(f"{name} must be >= {minimum}")

    return value


def _float(
    name: str,
    default: float | None,
    *,
    minimum: float | None = None,
    exclusive_minimum: bool = False,
) -> float:
    raw = os.getenv(name)
    if raw is None or not raw.strip():
        if default is None:
            raise ConfigError(f"{name} is required")
        value = default
    else:
        try:
            value = float(raw)
        except ValueError as exc:
            raise ConfigError(f"{name} must be a number") from exc

    if minimum is not None:
        invalid = value <= minimum if exclusive_minimum else value < minimum
        if invalid:
            operator = ">" if exclusive_minimum else ">="
            raise ConfigError(f"{name} must be {operator} {minimum:g}")

    return value


@dataclass(frozen=True, slots=True)
class Config:
    backend_url: str
    api_key: str
    motion_min_area: float = DEFAULT_MOTION_MIN_AREA
    cooldown_seconds: float = DEFAULT_COOLDOWN_SECONDS
    preview_interval: float = DEFAULT_PREVIEW_INTERVAL
    queue_max_mb: float = DEFAULT_QUEUE_MAX_MB
    queue_dir: Path = Path("offline_queue")
    request_timeout_seconds: float = DEFAULT_REQUEST_TIMEOUT_SECONDS

    # --- Live video path -------------------------------------------------------------------
    # ``webrtc`` pushes H.264/RTSP to MediaMTX; ``mjpeg`` keeps the original POST /api/camera/frame
    # loop. The event upload and offline queue are identical in both modes.
    stream_mode: str = STREAM_MODE_WEBRTC
    system_id: str | None = None
    stream_rtsp_url: str | None = None
    rtsp_host: str | None = None
    rtsp_port: int = DEFAULT_RTSP_PORT
    rtsp_user: str = DEFAULT_RTSP_USER
    stream_width: int = DEFAULT_STREAM_WIDTH
    stream_height: int = DEFAULT_STREAM_HEIGHT
    stream_fps: int = DEFAULT_STREAM_FPS
    stream_bitrate_kbps: int = DEFAULT_STREAM_BITRATE_KBPS
    stream_ffmpeg_encoder: str | None = None
    ffmpeg_path: str = "ffmpeg"
    preview_enabled: bool = True

    @property
    def queue_max_bytes(self) -> int:
        return int(self.queue_max_mb * 1024 * 1024)

    @property
    def uses_webrtc(self) -> bool:
        return self.stream_mode == STREAM_MODE_WEBRTC

    @property
    def resolved_rtsp_url(self) -> str | None:
        """RTSP URL for the MediaMTX publisher, or None when WebRTC streaming is off.

        ``STREAM_RTSP_URL`` wins when set (an operator may point at a different host or use a
        ``rtsps://`` URL). Otherwise the URL is assembled from ``RTSP_HOST``/``RTSP_PORT`` and the
        ``SYSTEM_ID``, which doubles as the MediaMTX path so the backend webhook can enforce
        tenant isolation.
        """

        if not self.uses_webrtc:
            return None
        if self.stream_rtsp_url:
            return self.stream_rtsp_url
        if not self.system_id or not self.rtsp_host:
            return None

        user = quote(self.rtsp_user, safe="")
        password = quote(self.api_key, safe="")
        path = quote(self.system_id, safe="")
        return (
            f"rtsp://{user}:{password}@{self.rtsp_host}:{self.rtsp_port}/{path}"
        )

    @classmethod
    def from_env(cls, env_file: str | Path | None = ".env") -> "Config":
        """Load configuration from ``env_file`` plus process environment.

        Real environment variables take precedence over values in the file, which
        makes systemd ``EnvironmentFile=`` and one-off overrides predictable.
        """

        if env_file:
            load_dotenv(env_file, override=False)

        backend_url = _required("BACKEND_URL").rstrip("/")
        if not backend_url.startswith(("http://", "https://")):
            raise ConfigError("BACKEND_URL must start with http:// or https://")

        api_key = _required("API_KEY")
        motion_min_area = _float(
            "MOTION_MIN_AREA",
            DEFAULT_MOTION_MIN_AREA,
            minimum=0,
        )
        cooldown_seconds = _float(
            "COOLDOWN_SECONDS",
            DEFAULT_COOLDOWN_SECONDS,
            minimum=0,
        )
        preview_interval = _float(
            "PREVIEW_INTERVAL",
            DEFAULT_PREVIEW_INTERVAL,
            minimum=0,
            exclusive_minimum=True,
        )
        queue_max_mb = _float(
            "QUEUE_MAX_MB",
            DEFAULT_QUEUE_MAX_MB,
            minimum=0,
            exclusive_minimum=True,
        )

        stream_mode = (_optional("STREAM_MODE") or STREAM_MODE_WEBRTC).lower()
        if stream_mode not in STREAM_MODES:
            raise ConfigError(
                f"STREAM_MODE must be one of {', '.join(STREAM_MODES)}; got {stream_mode!r}"
            )

        system_id = _optional("SYSTEM_ID")
        rtsp_host = _optional("RTSP_HOST") or _backend_host(backend_url)

        if stream_mode == STREAM_MODE_WEBRTC:
            # Fail fast instead of silently running an MJPEG-only daemon when the operator asked
            # for WebRTC: a missing SYSTEM_ID would otherwise publish to the wrong path.
            if not _optional("STREAM_RTSP_URL") and not system_id:
                raise ConfigError(
                    "SYSTEM_ID is required when STREAM_MODE=webrtc (it is the MediaMTX path)"
                )

        return cls(
            backend_url=backend_url,
            api_key=api_key,
            motion_min_area=motion_min_area,
            cooldown_seconds=cooldown_seconds,
            preview_interval=preview_interval,
            queue_max_mb=queue_max_mb,
            stream_mode=stream_mode,
            system_id=system_id,
            stream_rtsp_url=_optional("STREAM_RTSP_URL"),
            rtsp_host=rtsp_host,
            rtsp_port=_int("RTSP_PORT", DEFAULT_RTSP_PORT, minimum=1),
            rtsp_user=_optional("RTSP_USER") or DEFAULT_RTSP_USER,
            stream_width=_int("STREAM_WIDTH", DEFAULT_STREAM_WIDTH, minimum=2),
            stream_height=_int("STREAM_HEIGHT", DEFAULT_STREAM_HEIGHT, minimum=2),
            stream_fps=_int("STREAM_FPS", DEFAULT_STREAM_FPS, minimum=1),
            stream_bitrate_kbps=_int(
                "STREAM_BITRATE_KBPS", DEFAULT_STREAM_BITRATE_KBPS, minimum=1
            ),
            stream_ffmpeg_encoder=_optional("STREAM_FFMPEG_ENCODER"),
            ffmpeg_path=_optional("FFMPEG_PATH") or "ffmpeg",
            preview_enabled=_bool("PREVIEW_ENABLED", True),
        )
