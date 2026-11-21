"""Environment-based configuration for the capture daemon."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import os

from dotenv import load_dotenv

DEFAULT_MOTION_MIN_AREA = 1500.0
DEFAULT_COOLDOWN_SECONDS = 8.0
DEFAULT_PREVIEW_INTERVAL = 1.0
DEFAULT_QUEUE_MAX_MB = 500.0
DEFAULT_REQUEST_TIMEOUT_SECONDS = 5.0


class ConfigError(ValueError):
    """Raised when an environment value is missing or invalid."""


def _required(name: str) -> str:
    value = os.getenv(name, "").strip()
    if not value:
        raise ConfigError(f"{name} is required")
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

    @property
    def queue_max_bytes(self) -> int:
        return int(self.queue_max_mb * 1024 * 1024)

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

        return cls(
            backend_url=backend_url,
            api_key=api_key,
            motion_min_area=motion_min_area,
            cooldown_seconds=cooldown_seconds,
            preview_interval=preview_interval,
            queue_max_mb=queue_max_mb,
        )
