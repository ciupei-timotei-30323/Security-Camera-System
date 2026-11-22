from __future__ import annotations

import threading

import cv2
import numpy as np
import pytest

from security_camera_pi.camera import Camera
from security_camera_pi.frames import (
    FrameFormatError,
    frame_to_jpeg,
    i420_y_plane,
)

BLUE_BGR = (255, 0, 0)
RED_BGR = (0, 0, 255)


def _colour_test_image(width: int, height: int) -> np.ndarray:
    frame = np.empty((height, width, 3), dtype=np.uint8)
    frame[:, :] = BLUE_BGR
    half_w = width // 4
    half_h = height // 4
    frame[height // 2 - half_h : height // 2 + half_h, width // 2 - half_w : width // 2 + half_w] = RED_BGR
    return frame


def _decode(jpeg: bytes) -> np.ndarray:
    frame = cv2.imdecode(np.frombuffer(jpeg, np.uint8), cv2.IMREAD_UNCHANGED)
    assert frame is not None
    return frame


def _mean_bgr(frame: np.ndarray, y: int, x: int) -> np.ndarray:
    patch = frame[y - 20 : y + 20, x - 20 : x + 20]
    return patch.reshape(-1, 3).mean(axis=0)


def _assert_blue(mean_bgr: np.ndarray) -> None:
    blue, green, red = mean_bgr
    assert blue > 180
    assert green < 80
    assert red < 80


def _assert_red(mean_bgr: np.ndarray) -> None:
    blue, green, red = mean_bgr
    assert red > 180
    assert green < 80
    assert blue < 80


def test_i420_jpeg_has_exact_size_and_colours() -> None:
    width, height = 640, 360
    source = _colour_test_image(width, height)
    i420 = cv2.cvtColor(source, cv2.COLOR_BGR2YUV_I420)
    assert i420.shape == (height * 3 // 2, width)

    decoded = _decode(frame_to_jpeg(i420, "I420", 60, expected_size=(width, height)))

    assert decoded.shape == (height, width, 3)
    _assert_blue(_mean_bgr(decoded, 40, 40))
    _assert_red(_mean_bgr(decoded, height // 2, width // 2))


def test_bgr_rgb888_jpeg_has_exact_size_and_colours() -> None:
    width, height = 640, 360
    source = _colour_test_image(width, height)

    decoded = _decode(frame_to_jpeg(source, "RGB888", 60, expected_size=(width, height)))

    assert decoded.shape == (height, width, 3)
    _assert_blue(_mean_bgr(decoded, 40, 40))
    _assert_red(_mean_bgr(decoded, height // 2, width // 2))


def test_planar_i420_cannot_be_encoded_as_bgr() -> None:
    width, height = 640, 360
    i420 = cv2.cvtColor(_colour_test_image(width, height), cv2.COLOR_BGR2YUV_I420)

    with pytest.raises(FrameFormatError, match="BGR frame must have shape"):
        frame_to_jpeg(i420, "BGR", 60, expected_size=(width, height))


def test_i420_rejects_wrong_expected_size() -> None:
    width, height = 640, 360
    i420 = cv2.cvtColor(_colour_test_image(width, height), cv2.COLOR_BGR2YUV_I420)

    with pytest.raises(FrameFormatError, match="I420 frame must have shape"):
        frame_to_jpeg(i420, "I420", 60, expected_size=(width, height * 2))


def test_motion_y_plane_is_only_the_luma_plane() -> None:
    width, height = 320, 180
    source = _colour_test_image(width, height)
    i420 = cv2.cvtColor(source, cv2.COLOR_BGR2YUV_I420)

    y_plane = i420_y_plane(i420, (width, height))

    assert y_plane.shape == (height, width)
    assert np.array_equal(y_plane, i420[:height, :width])


class _FakePicam2:
    def __init__(self, main: np.ndarray, lores: np.ndarray) -> None:
        self.main = main
        self.lores = lores

    def capture_array(self, stream: str) -> np.ndarray:
        if stream == "main":
            return self.main.copy()
        if stream == "lores":
            return self.lores.copy()
        raise KeyError(stream)


def _camera_with(
    main: np.ndarray,
    lores: np.ndarray,
    *,
    full_size: tuple[int, int],
    preview_size: tuple[int, int],
) -> Camera:
    camera = object.__new__(Camera)
    camera._full_size = full_size
    camera._preview_size = preview_size
    camera._lock = threading.Lock()
    camera._started = True
    camera._picam2 = _FakePicam2(main, lores)
    return camera


def test_camera_preview_still_and_motion_paths() -> None:
    full_size = (640, 360)
    preview_size = (320, 180)
    main = _colour_test_image(*full_size)
    lores = cv2.cvtColor(_colour_test_image(*preview_size), cv2.COLOR_BGR2YUV_I420)
    camera = _camera_with(
        main,
        lores,
        full_size=full_size,
        preview_size=preview_size,
    )

    preview = _decode(camera.capture_preview_jpeg())
    still = _decode(camera.capture_still_jpeg())
    motion_gray = camera.capture_motion_gray()

    assert preview.shape == (preview_size[1], preview_size[0], 3)
    assert still.shape == (full_size[1], full_size[0], 3)
    assert motion_gray.shape == (preview_size[1], preview_size[0])

    _assert_blue(_mean_bgr(preview, 30, 30))
    _assert_red(_mean_bgr(preview, preview_size[1] // 2, preview_size[0] // 2))
    _assert_blue(_mean_bgr(still, 30, 30))
    _assert_red(_mean_bgr(still, full_size[1] // 2, full_size[0] // 2))
