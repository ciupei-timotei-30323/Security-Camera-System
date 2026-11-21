# Security Camera Pi capture daemon

Raspberry Pi 5 camera daemon for the security camera backend. It watches the camera locally,
uploads motion stills to `POST /api/events`, and pushes a low-resolution live preview to
`POST /api/camera/frame`. The Pi never listens for inbound connections.

The daemon is deliberately stateless apart from `offline_queue/`:

```
Picamera2
  ├─ lores frames ──> grayscale motion detector ──> full-res JPEG ──> POST /api/events
  │                                                   └─ transient failure ──> offline queue
  └─ lores frames ──> 640x360 JPEG q60 ──> POST /api/camera/frame (best effort)
```

## Requirements

- Raspberry Pi 5 with a supported Camera Module and Raspberry Pi OS 64-bit (Bookworm or newer).
- Python 3.11+.
- A reachable backend and a camera API key (`PiAuth__Systems__<systemId>`/`PiDevices`).
- No cloud SDK, face recognition or inbound listener is used.

## Install

```bash
sudo apt update
sudo apt install -y python3-picamera2 python3-opencv python3-venv libcamera-apps
git clone <repository-url>
cd Security-Camera-Pi

# --system-site-packages makes the apt-installed picamera2 module visible.
python3 -m venv --system-site-packages .venv
source .venv/bin/activate
python3 -m pip install --upgrade pip
python3 -m pip install -e .
```

Verify the camera before starting the daemon:

```bash
libcamera-hello --timeout 5000
python3 -c 'from picamera2 import Picamera2; print(Picamera2.global_camera_info())'
```

## Configuration

Copy `.env.example` to `.env` and edit it:

| Variable | Default | Meaning |
|---|---|---|
| `BACKEND_URL` | required | Backend origin, for example `http://192.168.1.10:8080`; no trailing `/api` |
| `API_KEY` | required | Camera kit key, sent as `X-Api-Key`; never logged |
| `MOTION_MIN_AREA` | `1500` | Largest contour area in the internal 320x180 motion image that counts as motion |
| `COOLDOWN_SECONDS` | `8` | Motion is ignored for this long after a trigger |
| `PREVIEW_INTERVAL` | `1` | Seconds between live-preview uploads |
| `QUEUE_MAX_MB` | `500` | Maximum on-disk size of `offline_queue/`, including sidecars |

`MOTION_MIN_AREA` is field-tunable: increase it to ignore leaves/small pets, decrease it for
distant motion. The daemon uses a 1920x1080 full-resolution still and a 640x360 lores stream by
default.

## Run

```bash
source .venv/bin/activate
python -m security_camera_pi
```

Useful options:

```bash
python -m security_camera_pi --env-file /etc/security-camera-pi.env --log-level INFO
python -m security_camera_pi --simulate-offline
```

`--simulate-offline` never contacts the backend. Every motion event is written to the offline
queue, so you can verify capture, sidecar generation, pruning and graceful shutdown without a
server. The preview pusher also fails silently in this mode.

Logs are one JSON object per line on stdout/stderr, suitable for journald. API keys are never
included.

## Backend contract

On motion the daemon:

1. Captures a full-resolution JPEG.
2. Generates a UUID `clientEventId` and a UTC ISO-8601 `capturedAt`.
3. Sends multipart `POST /api/events` with `image`, `clientEventId`, `capturedAt` and
   `isOfflineSync=false`, using a 5-second timeout.

A `200`/`201` means success. Network errors, timeouts, `429` and `5xx` responses are queued.
Other `4xx` responses are permanent and are logged without queueing.

The preview thread sends a raw JPEG body (`Content-Type: image/jpeg`) to
`POST /api/camera/frame`; it does not use multipart. Preview failures are intentionally silent at
normal log levels.

## Offline queue

A transient failure writes two files:

```
offline_queue/20260102T030405678000Z_6f9619ff-8b86-d011-b42d-00c04fc964ff.jpg
offline_queue/20260102T030405678000Z_6f9619ff-8b86-d011-b42d-00c04fc964ff.json
```

The sidecar stores the original `clientEventId`, `capturedAt`, `isOfflineSync: true` and the JPEG
name. The filename uses a filesystem-safe compact UTC timestamp; the original timestamp is kept
verbatim in the sidecar and request.

A background thread retries the oldest event first:

- Exponential backoff starts at 1 second and is capped at 60 seconds.
- `200`/`201` deletes the JPEG and sidecar.
- `429`, `5xx`, network errors and timeouts keep the event queued.
- Other `4xx` responses move both files to `offline_queue/rejected/`.
- When `QUEUE_MAX_MB` is exceeded, the oldest non-uploading entries are deleted.

Queued events are sent with `isOfflineSync=true`, the original `clientEventId` and the original
`capturedAt`, so backend idempotency and away-mode scheduling use the real capture time.

## systemd

Install the application under `/opt/security-camera-pi`, create the virtual environment there, and
put the real configuration in `/etc/security-camera-pi.env`:

```bash
sudo mkdir -p /opt/security-camera-pi
sudo cp -r src pyproject.toml README.md systemd .env.example /opt/security-camera-pi/
sudo python3 -m venv --system-site-packages /opt/security-camera-pi/.venv
sudo /opt/security-camera-pi/.venv/bin/python -m pip install -e /opt/security-camera-pi

sudo cp /opt/security-camera-pi/.env.example /etc/security-camera-pi.env
sudoedit /etc/security-camera-pi.env

sudo cp /opt/security-camera-pi/systemd/security-camera-pi.service \
  /etc/systemd/system/security-camera-pi.service
sudo systemctl daemon-reload
sudo systemctl enable --now security-camera-pi
```

Check status and logs:

```bash
systemctl status security-camera-pi
journalctl -u security-camera-pi -f
```

The service runs as `pi:pi` with the `video` group, restarts on failure and sends `SIGTERM` on
shutdown. The daemon stops its threads, closes the camera and leaves pending queue files intact.

## Tests

The queue suite uses a local mock HTTP server and does not need a camera:

```bash
source .venv/bin/activate
python -m pip install -e '.[test]'
python -m pytest
```

Covered cases include JPEG/sidecar creation, 200/201 deletion, 5xx and 429 retries, permanent 4xx
rejection, oldest-first ordering, size-limit eviction, backoff caps and the real background retry
thread.
