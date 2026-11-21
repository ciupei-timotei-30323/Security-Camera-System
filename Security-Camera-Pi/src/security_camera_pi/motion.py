"""Low-cost frame-difference motion detection."""

from __future__ import annotations

from dataclasses import dataclass

import cv2
import numpy as np


@dataclass(frozen=True, slots=True)
class MotionResult:
    motion: bool
    max_area: float


class MotionDetector:
    """Running-average background subtractor with a contour-area threshold.

    Frames are reduced to a small grayscale image, blurred, compared against an
    exponentially weighted background, thresholded and dilated. The largest
    external contour area is compared with ``min_area``.
    """

    def __init__(
        self,
        min_area: float,
        *,
        width: int = 320,
        height: int = 180,
        blur_kernel: int = 21,
        difference_threshold: int = 25,
        background_alpha: float = 0.08,
        dilate_iterations: int = 2,
    ) -> None:
        if width <= 0 or height <= 0:
            raise ValueError("motion frame dimensions must be positive")
        if blur_kernel < 3 or blur_kernel % 2 == 0:
            raise ValueError("blur_kernel must be an odd number >= 3")
        if not 0 < background_alpha <= 1:
            raise ValueError("background_alpha must be in (0, 1]")

        self.min_area = float(min_area)
        self.width = width
        self.height = height
        self.blur_kernel = blur_kernel
        self.difference_threshold = int(difference_threshold)
        self.background_alpha = float(background_alpha)
        self.dilate_iterations = int(dilate_iterations)
        self._background: np.ndarray | None = None

    def reset(self) -> None:
        self._background = None

    def process(self, gray_frame: np.ndarray) -> MotionResult:
        if gray_frame.ndim != 2:
            raise ValueError("motion detector expects a grayscale frame")

        small = cv2.resize(
            gray_frame,
            (self.width, self.height),
            interpolation=cv2.INTER_AREA,
        )
        small = cv2.GaussianBlur(
            small,
            (self.blur_kernel, self.blur_kernel),
            0,
        )

        if self._background is None:
            # The first frame establishes the background; it can never be motion.
            self._background = small.astype(np.float32)
            return MotionResult(motion=False, max_area=0.0)

        cv2.accumulateWeighted(small, self._background, self.background_alpha)
        background = cv2.convertScaleAbs(self._background)
        difference = cv2.absdiff(small, background)
        _, thresholded = cv2.threshold(
            difference,
            self.difference_threshold,
            255,
            cv2.THRESH_BINARY,
        )
        thresholded = cv2.dilate(
            thresholded,
            None,
            iterations=self.dilate_iterations,
        )
        contours, _ = cv2.findContours(
            thresholded,
            cv2.RETR_EXTERNAL,
            cv2.CHAIN_APPROX_SIMPLE,
        )
        max_area = max(
            (float(cv2.contourArea(contour)) for contour in contours),
            default=0.0,
        )

        return MotionResult(
            motion=max_area > 0 and max_area >= self.min_area,
            max_area=max_area,
        )
