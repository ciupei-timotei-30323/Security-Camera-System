"""Picamera2 wrapper with lazy hardware imports."""

from __future__ import annotations

import threading

import cv2
import numpy as np

from .frames import frame_to_bgr, frame_to_jpeg, i420_y_plane

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

    Stream formats:
    - ``main``: 1920x1080 RGB888. Picamera2 stores RGB888 in BGR byte order,
      which is exactly what OpenCV expects for JPEG encoding. Used for stills
      and, downscaled, for the live preview.
    - ``lores``: 640x360 YUV420. Used only for motion detection, where the
      first ``height`` rows are the Y/luma plane.

    The preview and motion loops run in different threads. Picamera2's capture
    API is not safe to drive concurrently, so every capture is protected by one
    lock. Encoding and resizing happen outside the lock.
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
        return i420_y_plane(frame, self._preview_size)

    def capture_preview_jpeg(self, quality: int = PREVIEW_JPEG_QUALITY) -> bytes:
        with self._lock:
            frame = self._picam2.capture_array("main")
        # Validate before resize so a wrong/misordered buffer raises a clear
        # FrameFormatError rather than being resized into another bad frame.
        frame_to_bgr(frame, "BGR", expected_size=self._full_size)
        width, height = self._preview_size
        preview = cv2.resize(frame, (width, height), interpolation=cv2.INTER_AREA)
        return frame_to_jpeg(
            preview,
            "BGR",
            quality,
            expected_size=self._preview_size,
        )

    def capture_stream_frame(self, size: tuple[int, int]) -> np.ndarray:
        """Returns a BGR frame resized to ``size`` for the RTSP/H.264 publisher.

        Uses the same ``main`` RGB888 (BGR-ordered) buffer as the still path and the same lock,
        so the streamer never races the motion or preview loops for the camera.
        """

        width, height = size
        if width <= 0 or height <= 0:
            raise CameraError(f"invalid stream size {size}")

        with self._lock:
            frame = self._picam2.capture_array("main")
        frame_to_bgr(frame, "BGR", expected_size=self._full_size)
        if (width, height) == self._full_size:
            return frame
        return cv2.resize(frame, (width, height), interpolation=cv2.INTER_AREA)

    def capture_still_jpeg(self, quality: int = STILL_JPEG_QUALITY) -> bytes:
        with self._lock:
            frame = self._picam2.capture_array("main")
        return frame_to_jpeg(
            frame,
            "BGR",
            quality,
            expected_size=self._full_size,
        )
