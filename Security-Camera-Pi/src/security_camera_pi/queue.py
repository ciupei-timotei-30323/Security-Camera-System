"""Durable offline event queue and its retry thread."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from enum import Enum
import json
import logging
import os
from pathlib import Path
import threading
import time
import uuid

from .backend import (
    BackendClient,
    PermanentBackendError,
    TransientBackendError,
)

logger = logging.getLogger(__name__)

BASE_BACKOFF_SECONDS = 1.0
MAX_BACKOFF_SECONDS = 60.0
IDLE_WAIT_SECONDS = 1.0


@dataclass(frozen=True, slots=True)
class QueueEntry:
    jpg_path: Path
    meta_path: Path
    client_event_id: str
    captured_at: str

    @property
    def size_bytes(self) -> int:
        total = 0
        for path in (self.jpg_path, self.meta_path):
            try:
                total += path.stat().st_size
            except FileNotFoundError:
                pass
        return total


class ProcessOutcome(Enum):
    EMPTY = "empty"
    SENT = "sent"
    REJECTED = "rejected"
    RETRY = "retry"


def _timestamp_key(value: str, fallback: str) -> tuple[datetime, str]:
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.astimezone(timezone.utc), fallback
    except ValueError:
        return datetime.max.replace(tzinfo=timezone.utc), fallback


def _filename_stamp(captured_at: str) -> str:
    try:
        parsed = datetime.fromisoformat(captured_at.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.astimezone(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    except ValueError:
        # Keep the queue writable even if a caller supplied an odd timestamp.
        return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")


class OfflineQueue:
    """Filesystem-backed queue: one JPEG plus one JSON sidecar per event."""

    def __init__(self, root: str | Path, max_bytes: int) -> None:
        self.root = Path(root)
        self.rejected_dir = self.root / "rejected"
        self.max_bytes = max(0, int(max_bytes))
        self._lock = threading.RLock()
        self._active: set[Path] = set()

        self.root.mkdir(parents=True, exist_ok=True)
        self.rejected_dir.mkdir(parents=True, exist_ok=True)

    def enqueue(
        self,
        image: bytes,
        client_event_id: str,
        captured_at: str,
    ) -> QueueEntry:
        if not image:
            raise ValueError("cannot enqueue an empty image")

        with self._lock:
            base = f"{_filename_stamp(captured_at)}_{client_event_id}"
            jpg_path = self.root / f"{base}.jpg"
            meta_path = self.root / f"{base}.json"

            # UUIDs make collisions practically impossible, but never overwrite
            # an existing queued event if one somehow reuses the same name.
            while jpg_path.exists() or meta_path.exists():
                base = f"{base}-{uuid.uuid4().hex[:6]}"
                jpg_path = self.root / f"{base}.jpg"
                meta_path = self.root / f"{base}.json"

            tmp_jpg = self.root / f".{base}.jpg.tmp"
            tmp_meta = self.root / f".{base}.json.tmp"
            sidecar = {
                "clientEventId": client_event_id,
                "capturedAt": captured_at,
                "isOfflineSync": True,
                "image": jpg_path.name,
                "enqueuedAt": datetime.now(timezone.utc)
                .isoformat(timespec="milliseconds")
                .replace("+00:00", "Z"),
            }

            try:
                tmp_jpg.write_bytes(image)
                tmp_meta.write_text(
                    json.dumps(sidecar, ensure_ascii=False),
                    encoding="utf-8",
                )
                os.replace(tmp_jpg, jpg_path)
                os.replace(tmp_meta, meta_path)
            except BaseException:
                tmp_jpg.unlink(missing_ok=True)
                tmp_meta.unlink(missing_ok=True)
                raise

            entry = QueueEntry(
                jpg_path=jpg_path,
                meta_path=meta_path,
                client_event_id=client_event_id,
                captured_at=captured_at,
            )
            logger.info(
                "offline_event_queued",
                extra={
                    "event": "offline_event_queued",
                    "client_event_id": client_event_id,
                    "captured_at": captured_at,
                    "queue_path": str(jpg_path),
                },
            )
            self._enforce_limit_locked()
            return entry

    def entries(self) -> list[QueueEntry]:
        with self._lock:
            return self._entries_locked()

    def oldest(self) -> QueueEntry | None:
        entries = self.entries()
        return entries[0] if entries else None

    def mark_active(self, entry: QueueEntry) -> None:
        with self._lock:
            self._active.add(entry.jpg_path)

    def unmark_active(self, entry: QueueEntry) -> None:
        with self._lock:
            self._active.discard(entry.jpg_path)

    def remove(self, entry: QueueEntry) -> None:
        with self._lock:
            self._remove_locked(entry)

    def reject(self, entry: QueueEntry) -> Path:
        with self._lock:
            self.rejected_dir.mkdir(parents=True, exist_ok=True)
            base = entry.jpg_path.stem
            target_jpg = self.rejected_dir / f"{base}.jpg"
            target_meta = self.rejected_dir / f"{base}.json"
            while target_jpg.exists() or target_meta.exists():
                base = f"{base}-{uuid.uuid4().hex[:6]}"
                target_jpg = self.rejected_dir / f"{base}.jpg"
                target_meta = self.rejected_dir / f"{base}.json"

            if entry.jpg_path.exists():
                os.replace(entry.jpg_path, target_jpg)
            if entry.meta_path.exists():
                os.replace(entry.meta_path, target_meta)

            logger.error(
                "offline_event_rejected",
                extra={
                    "event": "offline_event_rejected",
                    "client_event_id": entry.client_event_id,
                    "captured_at": entry.captured_at,
                    "rejected_path": str(target_jpg),
                },
            )
            return target_jpg

    def enforce_limit(self) -> None:
        with self._lock:
            self._enforce_limit_locked()

    def total_bytes(self) -> int:
        with self._lock:
            return sum(entry.size_bytes for entry in self._entries_locked())

    def _entries_locked(self) -> list[QueueEntry]:
        entries: list[QueueEntry] = []
        for meta_path in self.root.glob("*.json"):
            if meta_path.name.startswith("."):
                continue
            try:
                sidecar = json.loads(meta_path.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError) as exc:
                logger.warning(
                    "offline_sidecar_unreadable",
                    extra={
                        "event": "offline_sidecar_unreadable",
                        "sidecar": str(meta_path),
                        "error_type": type(exc).__name__,
                    },
                )
                continue

            client_event_id = sidecar.get("clientEventId")
            captured_at = sidecar.get("capturedAt")
            image_name = sidecar.get("image") or meta_path.with_suffix(".jpg").name
            jpg_path = self.root / str(image_name)
            if (
                not isinstance(client_event_id, str)
                or not client_event_id
                or not isinstance(captured_at, str)
                or not captured_at
                or not jpg_path.exists()
            ):
                continue

            entries.append(
                QueueEntry(
                    jpg_path=jpg_path,
                    meta_path=meta_path,
                    client_event_id=client_event_id,
                    captured_at=captured_at,
                )
            )

        entries.sort(
            key=lambda entry: _timestamp_key(entry.captured_at, entry.jpg_path.name)
        )
        return entries

    def _remove_locked(self, entry: QueueEntry) -> None:
        entry.jpg_path.unlink(missing_ok=True)
        entry.meta_path.unlink(missing_ok=True)

    def _enforce_limit_locked(self) -> None:
        entries = self._entries_locked()
        total = sum(entry.size_bytes for entry in entries)

        for entry in entries:
            if total <= self.max_bytes:
                break
            if entry.jpg_path in self._active:
                # Never evict an event that the retry thread is uploading.
                continue
            removed_bytes = entry.size_bytes
            self._remove_locked(entry)
            total -= removed_bytes
            logger.warning(
                "offline_event_evicted",
                extra={
                    "event": "offline_event_evicted",
                    "client_event_id": entry.client_event_id,
                    "captured_at": entry.captured_at,
                    "removed_bytes": removed_bytes,
                    "queue_max_bytes": self.max_bytes,
                },
            )


def next_backoff(
    current: float,
    maximum: float = MAX_BACKOFF_SECONDS,
    minimum: float = BASE_BACKOFF_SECONDS,
) -> float:
    """Double the delay, bounded by ``minimum`` and ``maximum`` seconds."""

    return min(maximum, max(minimum, current * 2))


class QueueProcessor(threading.Thread):
    """Retries the oldest queued event with exponential backoff."""

    def __init__(
        self,
        queue: OfflineQueue,
        backend: BackendClient,
        stop_event: threading.Event,
        *,
        base_delay: float = BASE_BACKOFF_SECONDS,
        max_delay: float = MAX_BACKOFF_SECONDS,
        idle_wait: float = IDLE_WAIT_SECONDS,
    ) -> None:
        super().__init__(name="offline-queue", daemon=True)
        self.queue = queue
        self.backend = backend
        self.stop_event = stop_event
        self.base_delay = base_delay
        self.max_delay = max_delay
        self.idle_wait = idle_wait

    def process_one(self) -> ProcessOutcome:
        entry = self.queue.oldest()
        if entry is None:
            return ProcessOutcome.EMPTY

        self.queue.mark_active(entry)
        try:
            try:
                image = entry.jpg_path.read_bytes()
            except FileNotFoundError:
                # The queue was edited outside the daemon.
                self.queue.remove(entry)
                return ProcessOutcome.EMPTY

            self.backend.post_event(
                image,
                entry.client_event_id,
                entry.captured_at,
                is_offline_sync=True,
            )
        except PermanentBackendError as exc:
            self.queue.reject(entry)
            logger.error(
                "offline_event_permanently_failed",
                extra={
                    "event": "offline_event_permanently_failed",
                    "client_event_id": entry.client_event_id,
                    "status_code": exc.status_code,
                    "error": str(exc),
                },
            )
            return ProcessOutcome.REJECTED
        except TransientBackendError as exc:
            logger.warning(
                "offline_event_retry_scheduled",
                extra={
                    "event": "offline_event_retry_scheduled",
                    "client_event_id": entry.client_event_id,
                    "status_code": exc.status_code,
                    "error": str(exc),
                },
            )
            return ProcessOutcome.RETRY
        except OSError as exc:
            logger.error(
                "offline_event_read_failed",
                extra={
                    "event": "offline_event_read_failed",
                    "client_event_id": entry.client_event_id,
                    "error_type": type(exc).__name__,
                },
            )
            return ProcessOutcome.RETRY
        else:
            self.queue.remove(entry)
            logger.info(
                "offline_event_synced",
                extra={
                    "event": "offline_event_synced",
                    "client_event_id": entry.client_event_id,
                    "captured_at": entry.captured_at,
                },
            )
            return ProcessOutcome.SENT
        finally:
            self.queue.unmark_active(entry)

    def run(self) -> None:
        logger.info(
            "offline_queue_started",
            extra={
                "event": "offline_queue_started",
                "pending": len(self.queue.entries()),
            },
        )
        delay = self.base_delay

        while not self.stop_event.is_set():
            outcome = self.process_one()

            if outcome in (ProcessOutcome.SENT, ProcessOutcome.REJECTED):
                delay = self.base_delay
                continue

            if outcome is ProcessOutcome.EMPTY:
                delay = self.base_delay
                self.stop_event.wait(self.idle_wait)
                continue

            self.stop_event.wait(delay)
            delay = next_backoff(delay, self.max_delay, self.base_delay)

        logger.info(
            "offline_queue_stopped",
            extra={"event": "offline_queue_stopped"},
        )
