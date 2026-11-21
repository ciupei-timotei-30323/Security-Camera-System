from __future__ import annotations

from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import threading
import time
from typing import Iterator

import pytest

from security_camera_pi.backend import BackendClient
from security_camera_pi.queue import (
    OfflineQueue,
    ProcessOutcome,
    QueueProcessor,
    next_backoff,
)


class _MockHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_POST(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)

        request = {
            "path": self.path,
            "headers": {key.lower(): value for key, value in self.headers.items()},
            "body": body,
        }
        with self.server.lock:  # type: ignore[attr-defined]
            self.server.requests.append(request)  # type: ignore[attr-defined]
            if self.server.responses:  # type: ignore[attr-defined]
                status = self.server.responses.pop(0)  # type: ignore[attr-defined]
            else:
                status = self.server.default_status  # type: ignore[attr-defined]

        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", "2")
        self.end_headers()
        self.wfile.write(b"{}")

    def log_message(self, _format: str, *args: object) -> None:
        # Keep pytest output clean.
        return


@contextmanager
def mock_server(
    *responses: int,
    default_status: int = 201,
) -> Iterator[tuple[ThreadingHTTPServer, str]]:
    server = ThreadingHTTPServer(("127.0.0.1", 0), _MockHandler)
    server.requests = []  # type: ignore[attr-defined]
    server.responses = list(responses)  # type: ignore[attr-defined]
    server.default_status = default_status  # type: ignore[attr-defined]
    server.lock = threading.Lock()  # type: ignore[attr-defined]

    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()

    try:
        yield server, f"http://127.0.0.1:{server.server_port}"
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


def _backend(base_url: str) -> BackendClient:
    return BackendClient(base_url, "test-api-key", timeout_seconds=2.0)


def _queue(tmp_path, max_mb: int = 10) -> OfflineQueue:
    return OfflineQueue(tmp_path / "offline_queue", max_mb * 1024 * 1024)


def _enqueue(queue: OfflineQueue, event_id: str, captured_at: str) -> None:
    queue.enqueue(
        f"jpeg-{event_id}".encode(),
        event_id,
        captured_at,
    )


def test_enqueue_writes_jpeg_and_sidecar(tmp_path) -> None:
    queue = _queue(tmp_path)
    entry = queue.enqueue(
        b"jpeg-bytes",
        "client-1",
        "2026-01-02T03:04:05.678Z",
    )

    assert entry.jpg_path.read_bytes() == b"jpeg-bytes"
    assert entry.jpg_path.name == "20260102T030405678000Z_client-1.jpg"

    sidecar = json.loads(entry.meta_path.read_text(encoding="utf-8"))
    assert sidecar["clientEventId"] == "client-1"
    assert sidecar["capturedAt"] == "2026-01-02T03:04:05.678Z"
    assert sidecar["isOfflineSync"] is True
    assert sidecar["image"] == entry.jpg_path.name
    assert sidecar["enqueuedAt"].endswith("Z")

    assert queue.entries() == [entry]


@pytest.mark.parametrize("status", [200, 201])
def test_successful_upload_deletes_queue_entry(tmp_path, status: int) -> None:
    with mock_server(status) as (server, base_url):
        queue = _queue(tmp_path)
        _enqueue(queue, "client-1", "2026-01-02T03:04:05.678Z")
        processor = QueueProcessor(queue, _backend(base_url), threading.Event())

        assert processor.process_one() is ProcessOutcome.SENT
        assert queue.entries() == []
        assert len(server.requests) == 1  # type: ignore[attr-defined]


def test_5xx_keeps_entry_and_retry_sends_original_fields(tmp_path) -> None:
    with mock_server(500, 201) as (server, base_url):
        queue = _queue(tmp_path)
        _enqueue(queue, "client-5xx", "2026-02-03T04:05:06.789Z")
        processor = QueueProcessor(queue, _backend(base_url), threading.Event())

        assert processor.process_one() is ProcessOutcome.RETRY
        assert len(queue.entries()) == 1

        assert processor.process_one() is ProcessOutcome.SENT
        assert queue.entries() == []

        requests = server.requests  # type: ignore[attr-defined]
        assert len(requests) == 2
        for request in requests:
            assert request["path"] == "/api/events"
            assert request["headers"]["x-api-key"] == "test-api-key"
            assert request["headers"]["content-type"].startswith(
                "multipart/form-data; boundary="
            )
            body = request["body"]
            assert b'name="image"; filename="event.jpg"' in body
            assert b'name="clientEventId"\r\n\r\nclient-5xx' in body
            assert b'name="capturedAt"\r\n\r\n2026-02-03T04:05:06.789Z' in body
            assert b'name="isOfflineSync"\r\n\r\ntrue' in body


def test_429_is_transient_and_keeps_entry(tmp_path) -> None:
    with mock_server(429) as (server, base_url):
        queue = _queue(tmp_path)
        _enqueue(queue, "client-429", "2026-02-03T04:05:06.789Z")
        processor = QueueProcessor(queue, _backend(base_url), threading.Event())

        assert processor.process_one() is ProcessOutcome.RETRY
        assert len(queue.entries()) == 1
        assert len(server.requests) == 1  # type: ignore[attr-defined]


def test_permanent_4xx_moves_entry_to_rejected(tmp_path) -> None:
    with mock_server(400) as (server, base_url):
        queue = _queue(tmp_path)
        _enqueue(queue, "client-400", "2026-02-03T04:05:06.789Z")
        processor = QueueProcessor(queue, _backend(base_url), threading.Event())

        assert processor.process_one() is ProcessOutcome.REJECTED
        assert queue.entries() == []

        rejected = sorted(queue.rejected_dir.glob("*client-400*"))
        assert [path.name for path in rejected] == [
            "20260203T040506789000Z_client-400.jpg",
            "20260203T040506789000Z_client-400.json",
        ]
        assert len(server.requests) == 1  # type: ignore[attr-defined]


def test_oldest_entry_is_sent_first(tmp_path) -> None:
    with mock_server(201, 201) as (server, base_url):
        queue = _queue(tmp_path)
        _enqueue(queue, "newer", "2026-03-01T10:00:00.000Z")
        _enqueue(queue, "older", "2026-03-01T09:00:00.000Z")
        processor = QueueProcessor(queue, _backend(base_url), threading.Event())

        assert processor.process_one() is ProcessOutcome.SENT
        assert processor.process_one() is ProcessOutcome.SENT

        requests = server.requests  # type: ignore[attr-defined]
        assert b"older" in requests[0]["body"]
        assert b"newer" in requests[1]["body"]


def test_enforce_limit_drops_oldest_entries(tmp_path) -> None:
    queue = _queue(tmp_path)
    _enqueue(queue, "oldest", "2026-04-01T09:00:00.000Z")
    _enqueue(queue, "middle", "2026-04-01T10:00:00.000Z")
    _enqueue(queue, "newest", "2026-04-01T11:00:00.000Z")

    entries = queue.entries()
    assert [entry.client_event_id for entry in entries] == [
        "oldest",
        "middle",
        "newest",
    ]

    queue.max_bytes = queue.total_bytes() - entries[0].size_bytes
    queue.enforce_limit()

    assert [entry.client_event_id for entry in queue.entries()] == [
        "middle",
        "newest",
    ]


def test_enqueue_enforces_size_limit(tmp_path) -> None:
    queue = OfflineQueue(tmp_path / "offline_queue", max_bytes=1)
    queue.enqueue(b"larger-than-limit", "client-1", "2026-04-01T09:00:00.000Z")

    assert queue.entries() == []


def test_backoff_doubles_and_caps_at_sixty_seconds() -> None:
    assert next_backoff(1) == 2
    assert next_backoff(2) == 4
    assert next_backoff(30) == 60
    assert next_backoff(60) == 60


def test_background_thread_drains_queue(tmp_path) -> None:
    with mock_server(201) as (server, base_url):
        queue = _queue(tmp_path)
        _enqueue(queue, "threaded", "2026-05-01T09:00:00.000Z")
        stop_event = threading.Event()
        processor = QueueProcessor(
            queue,
            _backend(base_url),
            stop_event,
            base_delay=0.01,
            max_delay=0.02,
            idle_wait=0.01,
        )

        processor.start()
        try:
            deadline = time.monotonic() + 2.0
            while time.monotonic() < deadline and queue.entries():
                time.sleep(0.01)
        finally:
            stop_event.set()
            processor.join(timeout=2.0)

        assert not processor.is_alive()
        assert queue.entries() == []
        assert len(server.requests) == 1  # type: ignore[attr-defined]


def test_background_thread_retries_transient_failure(tmp_path) -> None:
    with mock_server(500, 201) as (server, base_url):
        queue = _queue(tmp_path)
        _enqueue(queue, "retry-thread", "2026-05-01T09:00:00.000Z")
        stop_event = threading.Event()
        processor = QueueProcessor(
            queue,
            _backend(base_url),
            stop_event,
            base_delay=0.01,
            max_delay=0.02,
            idle_wait=0.01,
        )

        processor.start()
        try:
            deadline = time.monotonic() + 2.0
            while time.monotonic() < deadline and queue.entries():
                time.sleep(0.01)
        finally:
            stop_event.set()
            processor.join(timeout=2.0)

        assert not processor.is_alive()
        assert queue.entries() == []
        assert len(server.requests) == 2  # type: ignore[attr-defined]
