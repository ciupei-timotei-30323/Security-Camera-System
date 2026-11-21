"""Small structured JSON logging setup using the standard library."""

from __future__ import annotations

from datetime import datetime, timezone
import json
import logging

_RESERVED_RECORD_KEYS = set(logging.makeLogRecord({}).__dict__) | {
    "message",
    "asctime",
}


class JsonFormatter(logging.Formatter):
    """Emit one JSON object per log line.

    ``logger.info("message", extra={"event": "motion_detected", "area": 12})``
    produces top-level ``event`` and ``area`` fields. Secrets are never added by
    the daemon, and callers must not put API keys in log extras.
    """

    def format(self, record: logging.LogRecord) -> str:
        payload: dict[str, object] = {
            "timestamp": datetime.fromtimestamp(
                record.created, tz=timezone.utc
            ).isoformat(timespec="milliseconds"),
            "level": record.levelname,
            "logger": record.name,
            "message": record.getMessage(),
        }

        for key, value in record.__dict__.items():
            if key in _RESERVED_RECORD_KEYS or key.startswith("_"):
                continue
            payload[key] = value

        if record.exc_info:
            payload["exception"] = self.formatException(record.exc_info)

        return json.dumps(payload, ensure_ascii=False, default=str)


def configure_logging(level: str = "INFO") -> None:
    numeric_level = getattr(logging, level.upper(), None)
    if not isinstance(numeric_level, int):
        raise ValueError(f"unknown log level: {level}")

    handler = logging.StreamHandler()
    handler.setFormatter(JsonFormatter())

    root = logging.getLogger()
    root.handlers.clear()
    root.addHandler(handler)
    root.setLevel(numeric_level)

    # requests/urllib3 debug output can be noisy and is not needed in production.
    logging.getLogger("urllib3").setLevel(max(numeric_level, logging.WARNING))
