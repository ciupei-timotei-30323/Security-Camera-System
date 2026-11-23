FROM python:3.11-slim

# Non-root runtime user. The uid/gid is pinned to 1654 so it matches the .NET image's APP_UID:
# cv-service and the backend share the `captures` volume and both must be able to write to it.
ARG APP_UID=1654
ARG APP_GID=1654

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1

WORKDIR /app

# OpenCV runtime dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgl1 libglib2.0-0 curl \
    && rm -rf /var/lib/apt/lists/*

# Install CPU-only PyTorch first to keep the image small and fast to build
RUN pip install --no-cache-dir torch torchvision --index-url https://download.pytorch.org/whl/cpu
RUN pip install --no-cache-dir fastapi uvicorn[standard] ultralytics opencv-python-headless

# Pre-download both models during build so startup is instant
RUN mkdir -p /app/models && \
    curl -L -o /app/models/face_detection_yunet_2023mar.onnx \
    https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx && \
    python -c "from ultralytics import YOLO; YOLO('yolo11n.pt')"

COPY main.py .

# Create the unprivileged runtime user and hand it the two paths it needs to write:
#   /app/models  — ultralytics writes config/cache next to the model on first use
#   /data/captures — shared volume; face crops and annotated frames are written here
RUN groupadd --gid ${APP_GID} app \
    && useradd --uid ${APP_UID} --gid ${APP_GID} --no-create-home --shell /usr/sbin/nologin app \
    && mkdir -p /data/captures \
    && chown -R ${APP_UID}:${APP_GID} /app /data/captures

USER app

EXPOSE 8000

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]
