"""Picamera2 wrapper with lazy hardware imports."""

from __future__ import annotations

import threading

import cv2
import numpy as np

FULL_WIDTH = 1920
FULL_HEIGHT = 1080
PREVIEW_WIDTH = 640
PREVIEW_HEIGHT = 360
STILL_JPEG_QUALITY = 90
PREVIEW_JPEG_QUALITY = 60


class CameraError(RuntimeError):
    """Raised when the camera cannot produce a frame."""


class Camera:
    """Owns a Picamera2 instance and serializes access to it.

    The preview and motion loops run in different threads. Picamera2's capture
    API is not safe to drive concurrently, so every capture is protected by one
    lock. Encoding happens outside the lock to keep the critical section short.
    """

    def __init__(
        self,
        *,
        full_size: tuple[int, int] = (FULL_WIDTH, FULL_HEIGHT),
        preview_size: tuple[int, int] = (PREVIEW_WIDTH, PREVIEW_HEIGHT),
    ) -> None:
        self._full_size = full_size
        self._preview_size = preview_size
        self._lock = threading.Lock()
        self._started = False

        # Imported here so config/queue tests can run on a development machine
        # without Raspberry Pi camera libraries installed.
        from picamera2 import Picamera2

        self._picam2 = Picamera2()
        config = self._picam2.create_video_configuration(
            main={"size": self._full_size, "format": "RGB888"},
            lores={"size": self._preview_size, "format": "YUV420"},
            buffer_count=4,
        )
        self._picam2.configure(config)

    def start(self) -> None:
        if self._started:
            return
        self._picam2.start()
        self._started = True

    def close(self) -> None:
        if not self._started:
            return
        self._started = False
        try:
            self._picam2.stop()
        finally:
            self._picam2.close()

    def capture_motion_gray(self) -> np.ndarray:
        with self._lock:
            frame = self._picam2.capture_array("lores")
        return self._lores_to_gray(frame)

    def capture_preview_jpeg(self, quality: int = PREVIEW_JPEG_QUALITY) -> bytes:
        with self._lock:
            frame = self._picam2.capture_array("lores")
        bgr = self._lores_to_bgr(frame)
        return _encode_jpeg(bgr, quality)

    def capture_still_jpeg(self, quality: int = STILL_JPEG_QUALITY) -> bytes:
        with self._lock:
            frame = self._picam2.capture_array("main")
        bgr = _main_to_bgr(frame)
        return _encode_jpeg(bgr, quality)

    def _lores_to_gray(self, frame: np.ndarray) -> np.ndarray:
        height, width = self._preview_size
        if frame.ndim == 2 and frame.shape[0] == height * 3 // 2:
            return cv2.cvtColor(frame, cv2.COLOR_YUV2GRAY_I420)
        if frame.ndim == 2:
            return frame
        if frame.ndim == 3 and frame.shape[2] == 4:
            return cv2.cvtColor(frame, cv2.COLOR_BGRA2GRAY)
        if frame.ndim == 3 and frame.shape[2] == 3:
            return cv2.cvtColor(frame, cv2.COLOR_RGB2GRAY)
        raise CameraError(f"unsupported lores frame shape: {frame.shape}")

    def _lores_to_bgr(self, frame: np.ndarray) -> np.ndarray:
        height, width = self._preview_size
        if frame.ndim == 2 and frame.shape[0] == height * 3 // 2:
            return cv2.cvtColor(frame, cv2.COLOR_YUV2BGR_I420)
        if frame.ndim == 2:
            return cv2.cvtColor(frame, cv2.COLOR_GRAY2BGR)
        if frame.ndim == 3 and frame.shape[2] == 4:
            return cv2.cvtColor(frame, cv2.COLOR_BGRA2BGR)
        if frame.ndim == 3 and frame.shape[2] == 3:
            return cv2.cvtColor(frame, cv2.COLOR_RGB2BGR)
        raise CameraError(f"unsupported lores frame shape: {frame.shape}")


def _main_to_bgr(frame: np.ndarray) -> np.ndarray:
    if frame.ndim == 2:
        return cv2.cvtColor(frame, cv2.COLOR_GRAY2BGR)
    if frame.ndim == 3 and frame.shape[2] == 4:
        return cv2.cvtColor(frame, cv2.COLOR_BGRA2BGR)
    if frame.ndim == 3 and frame.shape[2] == 3:
        return cv2.cvtColor(frame, cv2.COLOR_RGB2BGR)
    raise CameraError(f"unsupported main frame shape: {frame.shape}")


def _encode_jpeg(bgr: np.ndarray, quality: int) -> bytes:
    ok, encoded = cv2.imencode(
        ".jpg",
        bgr,
        [int(cv2.IMWRITE_JPEG_QUALITY), int(quality)],
    )
    if not ok:
        raise CameraError("JPEG encoding failed")
    return encoded.tobytes()
