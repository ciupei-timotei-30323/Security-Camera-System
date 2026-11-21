"""Command-line entry point for the Raspberry Pi capture daemon."""

from __future__ import annotations

import argparse
import logging
import signal
import sys

from .backend import BackendClient
from .camera import Camera
from .config import Config, ConfigError
from .daemon import CaptureDaemon
from .logging_setup import configure_logging
from .queue import OfflineQueue

logger = logging.getLogger(__name__)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="security-camera-pi",
        description="Raspberry Pi 5 motion-capture daemon for the security backend.",
    )
    parser.add_argument(
        "--env-file",
        default=".env",
        help="path to the .env file (default: ./.env)",
    )
    parser.add_argument(
        "--simulate-offline",
        action="store_true",
        help="never contact the backend; exercise capture and offline queueing",
    )
    parser.add_argument(
        "--log-level",
        default="INFO",
        help="logging level (default: INFO)",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)

    try:
        configure_logging(args.log_level)
    except ValueError as exc:
        print(str(exc), file=sys.stderr)
        return 2

    try:
        config = Config.from_env(args.env_file)
    except ConfigError as exc:
        logger.error(
            "configuration_invalid",
            extra={"event": "configuration_invalid", "error": str(exc)},
        )
        return 2

    try:
        camera = Camera()
    except Exception as exc:  # noqa: BLE001 - hardware/library may be absent
        logger.error(
            "camera_initialization_failed",
            extra={
                "event": "camera_initialization_failed",
                "error_type": type(exc).__name__,
                "error": str(exc),
            },
        )
        return 1

    backend = BackendClient(
        config.backend_url,
        config.api_key,
        timeout_seconds=config.request_timeout_seconds,
        simulate_offline=args.simulate_offline,
    )
    queue = OfflineQueue(config.queue_dir, config.queue_max_bytes)
    daemon = CaptureDaemon(config, camera, backend, queue)

    def handle_signal(signum: int, _frame: object) -> None:
        logger.info(
            "shutdown_signal_received",
            extra={
                "event": "shutdown_signal_received",
                "signal": signal.Signals(signum).name,
            },
        )
        daemon.request_stop()

    signal.signal(signal.SIGTERM, handle_signal)
    signal.signal(signal.SIGINT, handle_signal)

    try:
        daemon.run()
    except Exception:  # noqa: BLE001 - top-level crash handler
        logger.exception(
            "capture_daemon_crashed",
            extra={"event": "capture_daemon_crashed"},
        )
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
