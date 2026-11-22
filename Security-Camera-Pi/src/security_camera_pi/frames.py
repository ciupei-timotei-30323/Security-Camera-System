"""Frame format validation and JPEG encoding shared by all producers."""

from __future__ import annotations

from typing import Literal

import cv2
import numpy as np

FrameFormat = Literal["I420", "YUV420", "BGR", "RGB888"]
Size = tuple[int, int]


class FrameFormatError(RuntimeError):
    """Raised when a frame does not match its declared format/shape."""


def frame_to_bgr(
    frame: np.ndarray,
    fmt: FrameFormat,
    expected_size: Size | None = None,
) -> np.ndarray:
    """Return a BGR uint8 frame, validating the source layout.

    ``expected_size`` is ``(width, height)``. ``RGB888`` is accepted as an
    alias for Picamera2's BGR-ordered RGB888 buffers.
    """

    normalized = fmt.upper()
    if normalized in ("I420", "YUV420"):
        height, width = _expected_height_width(expected_size, normalized)
        _validate_i420(frame, height, width)
        bgr = cv2.cvtColor(frame, cv2.COLOR_YUV2BGR_I420)
    elif normalized in ("BGR", "RGB888"):
        height, width = _expected_height_width(expected_size, normalized)
        _validate_bgr(frame, height, width, normalized)
        bgr = frame
    else:
        raise FrameFormatError(f"unsupported frame format: {fmt!r}")

    if bgr.dtype != np.uint8 or bgr.ndim != 3 or bgr.shape[2] != 3:
        raise FrameFormatError(f"converted frame is not a uint8 BGR image: {bgr.shape}")
    if expected_size is not None and _size_of(bgr) != expected_size:
        raise FrameFormatError(
            f"converted frame size {_size_of(bgr)} does not match expected {expected_size}"
        )

    return bgr


def frame_to_jpeg(
    frame: np.ndarray,
    fmt: FrameFormat,
    quality: int,
    *,
    expected_size: Size | None = None,
) -> bytes:
    """Convert a validated frame to a BGR JPEG.

    This is the only path that should call ``cv2.imencode``. In particular, a
    planar I420 buffer must declare ``fmt="I420"``; passing it to this helper
    as ``BGR``/``RGB888`` raises ``FrameFormatError`` instead of silently
    encoding the Y plane as a grayscale image.
    """

    if not 1 <= int(quality) <= 100:
        raise FrameFormatError(f"JPEG quality must be between 1 and 100, got {quality}")

    bgr = frame_to_bgr(frame, fmt, expected_size)
    ok, encoded = cv2.imencode(
        ".jpg",
        bgr,
        [int(cv2.IMWRITE_JPEG_QUALITY), int(quality)],
    )
    if not ok:
        raise FrameFormatError("JPEG encoding failed")
    return encoded.tobytes()


def i420_y_plane(frame: np.ndarray, expected_size: Size) -> np.ndarray:
    """Return the luma plane of an I420 buffer for motion detection."""

    width, height = expected_size
    _validate_i420(frame, height, width)
    return frame[:height, :width]


def _expected_height_width(size: Size | None, fmt: str) -> tuple[int, int]:
    if size is None:
        raise FrameFormatError(
            f"{fmt} frames require expected size=(width, height) so shapes can be validated"
        )

    width, height = size
    if width <= 0 or height <= 0:
        raise FrameFormatError(f"invalid expected size {size}; width and height must be positive")
    if fmt in ("I420", "YUV420") and (width % 2 or height % 2):
        raise FrameFormatError(f"{fmt} requires even dimensions, got {size}")
    return height, width


def _validate_i420(frame: np.ndarray, height: int, width: int) -> None:
    expected_shape = (height * 3 // 2, width)
    if frame.dtype != np.uint8:
        raise FrameFormatError(f"I420 frame dtype must be uint8, got {frame.dtype}")
    if frame.ndim != 2 or frame.shape != expected_shape:
        raise FrameFormatError(
            f"I420 frame must have shape {expected_shape} for {width}x{height}, got {frame.shape}"
        )


def _validate_bgr(frame: np.ndarray, height: int, width: int, fmt: str) -> None:
    expected_shape = (height, width, 3)
    if frame.dtype != np.uint8:
        raise FrameFormatError(f"{fmt} frame dtype must be uint8, got {frame.dtype}")
    if frame.ndim != 3 or frame.shape != expected_shape:
        raise FrameFormatError(
            f"{fmt} frame must have shape {expected_shape} (BGR-ordered), got {frame.shape}"
        )


def _size_of(frame: np.ndarray) -> Size:
    return (int(frame.shape[1]), int(frame.shape[0]))
