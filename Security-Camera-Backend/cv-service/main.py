from pathlib import Path
import uuid
import cv2
import numpy as np
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from ultralytics import YOLO

app = FastAPI(title="Vision Analysis Service")

# Load models once at startup
# yolo11n.pt auto-downloads on first run (~6 MB)
person_model = YOLO("yolo11n.pt")

# Download once in Dockerfile:
# https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx
YUNET_PATH = "/app/models/face_detection_yunet_2023mar.onnx"
face_detector = cv2.FaceDetectorYN.create(
    model=YUNET_PATH,
    config="",
    input_size=(320, 320),
    score_threshold=0.75,
    nms_threshold=0.3,
    top_k=5,
)


class AnalyzeRequest(BaseModel):
    image_path: str


class AnalyzeResponse(BaseModel):
    person_detected: bool
    face_detected: bool
    person_confidence: float
    face_confidence: float
    face_crop_path: str | None = None
    annotated_path: str | None = None


def pad_and_crop(img: np.ndarray, x: int, y: int, w: int, h: int, pad_ratio: float = 0.25) -> np.ndarray:
    """Adds margin around the tight face box so the dashboard thumbnail includes the full head."""
    img_h, img_w = img.shape[:2]
    pad_w = int(w * pad_ratio)
    pad_h = int(h * pad_ratio)

    x1 = max(0, x - pad_w)
    y1 = max(0, y - int(pad_h * 1.2))  # Slightly more headroom for hair/forehead
    x2 = min(img_w, x + w + pad_w)
    y2 = min(img_h, y + h + pad_h)
    return img[y1:y2, x1:x2]


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/analyze", response_model=AnalyzeResponse)
def analyze_image(req: AnalyzeRequest):
    img_path = Path(req.image_path)
    if not img_path.exists():
        raise HTTPException(status_code=404, detail="Image file not found")

    frame = cv2.imread(str(img_path))
    if frame is None:
        raise HTTPException(status_code=400, detail="Invalid or corrupted image")

    # Stage 1: Person detection (COCO class 0 = person)
    results = person_model.predict(frame, classes=[0], conf=0.55, verbose=False)[0]
    if len(results.boxes) == 0:
        return AnalyzeResponse(
            person_detected=False,
            face_detected=False,
            person_confidence=0.0,
            face_confidence=0.0,
        )

    # Pick the highest-confidence person detection
    best_box = max(results.boxes, key=lambda b: float(b.conf[0]))
    person_conf = float(best_box.conf[0])
    px1, py1, px2, py2 = map(int, best_box.xyxy[0].tolist())

    annotated = frame.copy()
    cv2.rectangle(annotated, (px1, py1), (px2, py2), (255, 165, 0), 2)

    # Stage 2: Run YuNet face detection inside the person bounding box
    person_roi = frame[py1:py2, px1:px2]
    roi_h, roi_w = person_roi.shape[:2]
    face_crop_path = None
    face_detected = False
    face_conf = 0.0

    if roi_w > 20 and roi_h > 20:
        face_detector.setInputSize((roi_w, roi_h))
        _, faces = face_detector.detect(person_roi)

        if faces is not None and len(faces) > 0:
            # faces columns: [x, y, w, h, ...landmarks..., score]
            best_face = max(faces, key=lambda f: float(f[-1]))
            face_conf = float(best_face[-1])
            fx, fy, fw, fh = map(int, best_face[:4])

            # Map coordinates back to full frame
            abs_fx, abs_fy = px1 + fx, py1 + fy
            face_detected = True

            cv2.rectangle(annotated, (abs_fx, abs_fy), (abs_fx + fw, abs_fy + fh), (0, 255, 0), 2)

            face_crop = pad_and_crop(frame, abs_fx, abs_fy, fw, fh)
            crop_filename = f"face_{img_path.stem}_{uuid.uuid4().hex[:6]}.jpg"
            crop_out = img_path.parent / crop_filename
            cv2.imwrite(str(crop_out), face_crop)
            face_crop_path = str(crop_out)

    annotated_filename = f"annotated_{img_path.name}"
    annotated_out = img_path.parent / annotated_filename
    cv2.imwrite(str(annotated_out), annotated)

    return AnalyzeResponse(
        person_detected=True,
        face_detected=face_detected,
        person_confidence=round(person_conf, 3),
        face_confidence=round(face_conf, 3),
        face_crop_path=face_crop_path,
        annotated_path=str(annotated_out),
    )