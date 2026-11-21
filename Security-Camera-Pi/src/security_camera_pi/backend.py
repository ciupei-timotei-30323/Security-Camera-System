"""HTTP client for the backend ingestion and live-preview endpoints."""

from __future__ import annotations

import requests

EVENT_SUCCESS_STATUSES = frozenset({200, 201})
PREVIEW_SUCCESS_STATUSES = frozenset(range(200, 300))


class BackendError(RuntimeError):
    """Base class for backend upload failures."""

    def __init__(
        self,
        message: str,
        *,
        status_code: int | None = None,
    ) -> None:
        super().__init__(message)
        self.status_code = status_code


class TransientBackendError(BackendError):
    """Network error, timeout, 429 or 5xx. The request may be retried later."""


class PermanentBackendError(BackendError):
    """A 4xx response other than 429. Retrying the same request cannot succeed."""


class BackendClient:
    def __init__(
        self,
        base_url: str,
        api_key: str,
        *,
        timeout_seconds: float = 5.0,
        simulate_offline: bool = False,
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self._api_key = api_key
        self.timeout_seconds = timeout_seconds
        self.simulate_offline = simulate_offline

    @property
    def events_url(self) -> str:
        return f"{self.base_url}/api/events"

    @property
    def preview_url(self) -> str:
        return f"{self.base_url}/api/camera/frame"

    def post_event(
        self,
        image: bytes,
        client_event_id: str,
        captured_at: str,
        *,
        is_offline_sync: bool,
    ) -> None:
        if self.simulate_offline:
            raise TransientBackendError("simulated offline mode")

        try:
            response = requests.post(
                self.events_url,
                headers={"X-Api-Key": self._api_key},
                files={"image": ("event.jpg", image, "image/jpeg")},
                data={
                    "clientEventId": client_event_id,
                    "capturedAt": captured_at,
                    "isOfflineSync": "true" if is_offline_sync else "false",
                },
                timeout=self.timeout_seconds,
            )
        except requests.RequestException as exc:
            raise TransientBackendError(
                f"event upload failed: {type(exc).__name__}"
            ) from exc

        self._check_response(
            response,
            success_statuses=EVENT_SUCCESS_STATUSES,
            operation="event upload",
        )

    def post_preview(self, jpeg: bytes) -> None:
        if self.simulate_offline:
            raise TransientBackendError("simulated offline mode")

        try:
            response = requests.post(
                self.preview_url,
                headers={
                    "X-Api-Key": self._api_key,
                    "Content-Type": "image/jpeg",
                },
                data=jpeg,
                timeout=self.timeout_seconds,
            )
        except requests.RequestException as exc:
            raise TransientBackendError(
                f"preview upload failed: {type(exc).__name__}"
            ) from exc

        self._check_response(
            response,
            success_statuses=PREVIEW_SUCCESS_STATUSES,
            operation="preview upload",
        )

    @staticmethod
    def _check_response(
        response: requests.Response,
        *,
        success_statuses: frozenset[int],
        operation: str,
    ) -> None:
        status = response.status_code
        if status in success_statuses:
            return

        if status == 429 or status >= 500:
            raise TransientBackendError(
                f"{operation} returned HTTP {status}",
                status_code=status,
            )

        if 400 <= status < 500:
            raise PermanentBackendError(
                f"{operation} returned HTTP {status}",
                status_code=status,
            )

        raise TransientBackendError(
            f"{operation} returned unexpected HTTP {status}",
            status_code=status,
        )
