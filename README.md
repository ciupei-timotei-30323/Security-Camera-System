# Security Camera System

ASP.NET Core backend for the Raspberry Pi security camera. The API is multi-tenant: every
sub-user, entrance log, away-mode setting and Pi API key belongs to exactly one camera system
(`SystemId`, for example `sys-001` or `sys-002`).

## Web dashboard

The Next.js dashboard lives in [`frontend/`](frontend/) and is the **only** publicly reachable
service. Every browser request goes to a same-origin BFF, which proxies it to a private backend.
The backend never has a public URL or domain.

The BFF seals the backend JWT (plus the camera system it belongs to) into its own cookie rather than
storing the backend's token:

- `POST /api/auth/login` receives `{ username, password, systemId? }`, logs in upstream, and seals
  `{ systemId, edgeJwt, exp }` with AES-256-GCM into the `HttpOnly`, `SameSite=Lax`, `Path=/`
  cookie `bff_session`. The raw JWT is **never** returned to browser JavaScript.
- The cookie is encrypted with `BFF_SESSION_SECRET` (at least 32 bytes). Production startup fails
  when it is missing or too short.
- `Secure` is set automatically when `NODE_ENV=production` or when the request carries
  `x-forwarded-proto: https`, so Cloudflare Quick Tunnels need no extra flags.
- Each Pi/backend may sign its tokens with its own key, so the BFF never verifies backend JWT
  signatures. It only decrypts its own cookie, routes strictly by the sealed `systemId`, and
  forwards `Authorization: Bearer <edgeJwt>` upstream.
- Expired or tampered cookies are rejected and cleared immediately.

Upstream targets come from `EDGE_BACKENDS_JSON` (a `systemId` → private URL map, e.g.
`{"sys-001":"http://100.64.0.10:8080"}`) with `BACKEND_URL` as the single-backend fallback. A
`systemId` that is not mapped is rejected with `403`; a `systemId` is never read from query
parameters, headers or request bodies to pick a target.

Local development:

```bash
cd frontend
cp .env.example .env.local   # BACKEND_URL=http://localhost:8080
npm install
npm run dev                  # http://localhost:3000
```

The root `compose.yaml` also defines a `dashboard` service on port `3000`
with `BACKEND_URL=http://security-camera-backend:8080` and the backend on the private
`security-camera-internal` network only.

### Network exposure

Only `dashboard` publishes a host port (`3000`). The backend, PostgreSQL, `cv-service` and
`external-auth-service` live on the private Docker network; none of them is reachable from the
host, LAN or internet except through the BFF. `mailpit` is bound to `127.0.0.1` as a
development-only exception. All containers run as non-root with `no-new-privileges`.

`mosquitto` (`1883`, `9001`) and `mediamtx` (`8554`, `8889`, `8189/udp`) are deliberate published
exceptions because the Pi and the ESP32 terminals live outside Docker; put `mqtts://` plus `wss://`
in front of the broker before exposing it beyond a trusted LAN.

#### Reaching the backend from the Raspberry Pi

The backend publishes no host port, so a Pi **cannot** use `http://<lan-ip>:8080`. Instead, the
`tailscale` service joins the tailnet from inside the backend's network namespace
(`network_mode: service:security-camera-backend`), so the backend answers at
`http://<TS_HOSTNAME>:8080` over the mesh with no extra binding and nothing added to the LAN.

```bash
# 1. create a tagged (ideally ephemeral) auth key: Tailscale admin -> Settings -> Keys
# 2. put it in .env
TS_AUTHKEY=tskey-auth-...            # required
TS_HOSTNAME=security-camera-backend  # the tailnet name Pi clients will use
# 3. start the mesh sidecar (it is opt-in via a compose profile)
docker compose --profile tailscale up -d
```

The Pi then uses `BACKEND_URL=http://security-camera-backend:8080` and the ESP32 fallback uses
`FALLBACK_STATUS_URL=http://security-camera-backend:8080/api/device/status`. Remote camera sites
can point `EDGE_BACKENDS_JSON` at the same tailnet name, e.g.
`{"sys-002":"http://edge-sys-002:8080"}`.

Sign in with one of the seeded factory administrators from the `external-auth-service` seed:
`cam_admin_1` / `factory-pass-sys-001` (system `sys-001`) or `cam_admin_2` /
`factory-pass-sys-002` (system `sys-002`). The built-in `admin` account (system `sys-001`) uses
the `Admin__DeviceCode` that was configured when the account was first created; the existing
database may still have an older device code.

## Raspberry Pi capture daemon

The Raspberry Pi 5 daemon lives in [`Security-Camera-Pi/`](Security-Camera-Pi/README.md). It runs
Picamera2 motion detection locally, uploads full-resolution stills to `POST /api/events`, and
pushes a ~640x360 live preview to `POST /api/camera/frame`. Events that cannot reach the backend
are kept in a durable `offline_queue/` and retried oldest-first with exponential backoff; the
queue is bounded by `QUEUE_MAX_MB`. It never listens for inbound connections.

See the daemon README for installation, `.env` settings, the systemd unit and the offline
queue/rejected-file behaviour.

`STREAM_MODE` selects the live video path (`webrtc` by default, `mjpeg` as the fallback):

- `webrtc` — the daemon runs a second `Picamera2` camera configuration that encodes H.264
  (`H264Encoder` + `FfmpegOutput`) and pushes it over RTSP to
  `rtsp://<MQTT/RTSP user>:${PI_API_KEY}@${BACKEND_HOST}:8554/${SYSTEM_ID}` with
  `-preset ultrafast -tune zerolatency -g 30 -f rtsp -rtsp_transport tcp`. The publisher thread
  restarts automatically when the RTSP socket drops; motion detection and `POST /api/events`
  uploads are unchanged.
- `mjpeg` — the original `POST /api/camera/frame` loop.

See [`Security-Camera-Pi/README.md`](Security-Camera-Pi/README.md) for the streaming settings.

## ESP32 alert terminal

The ESP32 firmware lives in [`esp32-alert-terminal/`](esp32-alert-terminal/README.md) (PlatformIO,
Arduino C++). It subscribes over MQTT to `systems/<SYSTEM_ID>/alarm`, renders `line1`/`line2` on a
16x2 I2C LCD, drives an armed LED and pulses a buzzer + red LED while `alarm` is true. Reconnects
use exponential backoff (2 s → 60 s) and a `GET /api/device/status` poll takes over while the
broker is unreachable. See its README for wiring, `include/config.h`, build and flash steps.

## Multi-tenancy

- **Factory administrators** are validated by an external IAM service (or the local stub in
  development). A successful login registers the administrator's camera system and Pi key locally.
- **Sub-users** live in PostgreSQL and are unique per system. When `systemId` is omitted, the
  password is verified against every candidate record before anything else: 0 matches return
  `401`, 1 match logs that user in, and only 2+ matches return `400 SYSTEM_ID_REQUIRED` so the
  dashboard can ask which system to use. A username alone therefore reveals nothing.
- **Login protection:** `POST /api/auth/login` is rate limited per client IP (`Auth:LoginRateLimit`,
  default 5 requests/minute → `429` with `Retry-After`) and additionally locks a
  username/system pair for `Auth:LoginLockout:LockoutSeconds` after `MaxFailures` (default 5
  failures / 300 seconds → `429`, `code: LOGIN_LOCKED`).
- **JWT claims** include `sub`, `name`, `role` (`Admin`/`User`) and `system_id`. Every dashboard
  query derives the tenant from `system_id`; it never trusts a client-supplied system id.
- **Pi requests** authenticate with `X-Api-Key`. The filter resolves the key to a `SystemId` from
  configuration or the `PiDevices` table and passes it to the ingestion pipeline.

### Endpoints

| Method | Route | Auth | Scope |
|---|---|---|---|
| `POST` | `/api/auth/login` | anonymous | external admin first, then local sub-user |
| `GET` | `/api/auth/me` | JWT | returns `id`, `username`, `role`, `systemId` |
| `POST` | `/api/auth/logout` | JWT | revokes the JWT `jti` |
| `POST` | `/api/users` | JWT `Admin` | creates a sub-user in the admin's system |
| `GET` | `/api/users` | JWT `Admin` | lists only the admin's system users |
| `GET` | `/api/users/{id}` | JWT `Admin` | one sub-user from the admin's system |
| `DELETE` | `/api/users/{id}` | JWT `Admin` | deletes a sub-user (not yourself or the factory admin) |
| `GET` | `/api/events` | JWT | paged entrance logs (`page`, `pageSize`, `suspectOnly`, `from`, `to`) |
| `GET` | `/api/events/{id}` | JWT | one entrance log; cross-tenant ids return 404 |
| `DELETE` | `/api/events/{id}` | JWT `Admin` | deletes the log and its image files |
| `GET` | `/api/events/{id}/face` | JWT | tenant-scoped face crop (`Cache-Control: private`) |
| `GET` | `/api/events/{id}/annotated` | JWT | tenant-scoped annotated frame (raw-capture fallback) |
| `POST` | `/api/events` | Pi `X-Api-Key` | ingests a frame for the key's system |
| `POST` | `/api/events/analyze` | Pi `X-Api-Key` | CV analysis for the key's system |
| `POST` | `/api/camera/frame` | Pi `X-Api-Key` | stores the latest live JPEG (max 200 KB, 5 req/s per key) |
| `GET` | `/api/camera/latest` | JWT | latest JPEG for the caller's system, `204` if absent/stale |
| `GET` | `/api/camera/stream` | JWT | MJPEG stream; max 5 concurrent streams per system |
| `GET` | `/api/system/status` | JWT | camera/terminal online state plus backend and CV health |
| `GET` | `/api/device/status` | terminal `X-Api-Key` | tiny ARMED/DISARMED/SUSPECT payload for the ESP32 |
| `POST` | `/api/internal/mediamtx-auth` | MediaMTX (internal) | stream `publish`/`read` authorization |
| `GET` | `/api/realtime/ticket` | BFF only | short-lived WHEP URL + stream token + MQTT credentials |
| `GET`/`PUT` | `/api/away-mode-settings` | JWT | per-system arm/disarm state and schedule |
| `GET`/`PUT` | `/api/settings/away-mode` | JWT | alias for the same setting |

`GET /api/events` returns `{ "items": [...], "total": 0, "page": 1, "pageSize": 20 }`,
newest first. `pageSize` defaults to 20 and is capped at 100. `from`/`to` are ISO-8601 timestamps
and filter on the event `timestamp` (inclusive).

`POST /api/events` accepts the multipart fields `image`, `capturedAt`, `isOfflineSync` and the
optional `clientEventId` GUID. `capturedAt` is only used as the event timestamp when
`isOfflineSync=true`; otherwise the server's current UTC time is used. `clientEventId` is unique
per system in practice: retrying the same id returns `200 OK` with the existing event instead of
creating a duplicate.

Capture images are no longer exposed as static files under `/captures/**`. Use the JWT-protected
`/api/events/{id}/face` and `/api/events/{id}/annotated` endpoints; both derive the tenant from
the JWT and return 404 for another system's event.

### Live preview and device status

The Pi sends the current preview frame as a raw JPEG body:

```bash
curl -X POST http://localhost:8080/api/camera/frame \
  -H 'X-Api-Key: dev-only-api-key-sys-002' \
  -H 'Content-Type: image/jpeg' \
  --data-binary @frame.jpg
```

Only the newest frame per system is retained in memory (200 KB max, 5 requests/second per API
key). `GET /api/camera/latest` returns it as `image/jpeg`, or `204 No Content` when the system has
no frame or the frame is older than 10 seconds. `GET /api/camera/stream` serves the same frames as
`multipart/x-mixed-replace` MJPEG and stops when the client disconnects; each system may hold at
most five streams, after which the API returns `429`.

Every successful Pi API-key request also refreshes `PiDevices.LastSeenAt` (at most one database
write per device every 5 seconds). `GET /api/system/status` returns the caller's own system only:

```json
{
  "camera": { "online": true, "lastSeen": "2026-11-21T12:34:56+00:00" },
  "alertTerminal": { "online": false, "lastSeen": null },
  "backend": "ok",
  "cvService": "ok"
}
```

A device is online when it was seen in the last 15 seconds. Device kinds are `Camera` and
`AlertTerminal`. `cvService` is the cached result of the internal `GET /health` call (`ok` or
`down`; cached for 5 seconds).

### Outbound alerts and the alert terminal

`POST /api/events` persists the event and only enqueues outbound work; it never waits for SMTP.
A `BackgroundService` drains a bounded `System.Threading.Channels` queue and runs every
`INotifier`. Notifier failures are logged and cannot fail ingestion.

`EmailNotifier` sends when the event is suspect and the system's away-mode `alertEmail` is set:

- The dashboard link is `{Notifications:DashboardBaseUrl}/events/{id}`.
- Subject/body contain the event time, event id and `SystemId`.
- The face crop is attached when present; otherwise the annotated frame is used, with the raw
  capture as a final fallback.
- `Notifications:EmailCooldownSeconds` (default `60`) is enforced per system. Events suppressed
  inside the window are counted and summarized in the next e-mail.
- SMTP delivery gets one initial attempt plus three retries with exponential backoff
  (`Notifications:EmailRetryBaseDelayMilliseconds`, default `1000`).

Mailpit is included in `compose.yaml`: SMTP is on `localhost:1025` and the web inbox is at
<http://localhost:8025>. Set `Smtp__Host`, `Smtp__Port`, `Smtp__UseStartTls`,
`Smtp__Username`, `Smtp__Password`, `Smtp__FromAddress` and `Smtp__FromName` for real SMTP.
Without `Smtp__Host`, suspect events are still ingested; the e-mail delivery failure is logged
after the retry attempts.

Alert terminals authenticate with a key configured in `PiAuth:AlertTerminalSystems` (the same
shape as `PiAuth:Systems`). Camera keys are rejected by the terminal endpoint. A suspect event
sets an in-memory alarm window of `Alarm:DurationSeconds` (default `15`) for its system only:

```bash
curl -s http://localhost:8080/api/device/status \
  -H 'X-Api-Key: dev-alert-terminal-key-sys-001'
```

```json
{
  "armed": true,
  "alarm": true,
  "alarmUntil": "2026-11-21T12:34:56+00:00",
  "line1": "SUSPECT DETECTED",
  "line2": "                ",
  "serverTime": "2026-11-21T12:34:41+00:00"
}
```

`line1`/`line2` are padded/truncated to 16 characters for the LCD. `armed` reflects the system's
away mode; `alarm` is true only while the alarm window is open.

### Event-driven alerts over MQTT

The same document is published to Mosquitto so the terminal and the dashboard no longer poll:

| Topic | Retain | QoS | Payload |
|---|---|---|---|
| `systems/{systemId}/alarm` | yes | 1 | exactly the `GET /api/device/status` document above |
| `systems/{systemId}/events` | no | 1 | `{ "eventId", "timestamp", "isSuspect", "hasFace" }` |

`MqttNotifier` runs inside the existing `System.Threading.Channels` notification pipeline, so an
MQTT outage can never fail ingestion. A suspect event publishes the retained alarm document,
then a delayed task publishes the cleared document once `Alarm:DurationSeconds` expires. A
`PUT /api/settings/away-mode` publishes the new `armed` state immediately. Every ingested event
publishes a non-retained summary so open dashboards refresh without polling. `GET
/api/device/status` and `GET /api/camera/stream` keep working unchanged for A/B latency testing.

Configuration (`Mqtt__*`): `Enabled` (default `true`), `Host` (default `mosquitto`), `Port`
(default `1883`), `Username`, `Password`, `ClientId` (default `security-camera-backend`),
`TopicPrefix` (default `systems`), `PublishTimeoutSeconds` (default `5`). Publishing is skipped
when `Host` is empty or `Enabled=false`.

`mosquitto/` holds the broker configuration: `mosquitto.conf` (listeners `1883` MQTT and `9001`
WebSockets, password file, ACL file), `acl` (the `backend` user may read/write `systems/#`; the
`dashboard` user may only read `systems/+/alarm` and `systems/+/events`), and `entrypoint.sh`
(seeds `passwd` from `MQTT_BACKEND_USER/PASS` and `MQTT_CLIENT_USER/PASS` on first start). Change
those passwords **before** the first `docker compose up`, or delete the
`security-camera-mosquitto-data` volume to regenerate them.

### WebRTC video (RTSP → MediaMTX → WHEP)

The Pi publishes hardware-encoded H.264 over RTSP to MediaMTX (`bluenviron/mediamtx`), and the
browser plays it over WebRTC directly — video never traverses Next.js. `mediamtx/mediamtx.yml`
enables RTSP on `:8554`, WebRTC on `:8889` (WHEP signalling) and the ICE UDP mux on `:8189`, and
delegates authorization to the backend:

```yaml
authMethod: http
authHTTPAddress: http://security-camera-backend:8080/api/internal/mediamtx-auth
paths:
  all_others:
```

`POST /api/internal/mediamtx-auth` accepts MediaMTX's `{ user, password, token, ip, action, path,
protocol, id, query }` payload and enforces tenant isolation:

- **`publish`** — the Pi API key arrives in `password`/`token` (or as a `token=` query parameter).
  It must resolve to a **camera** key in `PiDevices` (alert-terminal keys are rejected) whose
  `SystemId` equals the path (`sys-001`, trimmed of any leading `/`). `200 OK` on match, `401`
  otherwise.
- **`read`** — the dashboard JWT arrives as `token` (or `token=` query parameter). Its signature,
  expiry and revoked `jti` are validated and its `system_id` claim must equal the path. `200 OK`
  with `{ "expireAt": "<jwt exp, ISO-8601>" }` on match, `401` otherwise.

### Real-time dashboard credentials

`GET /api/realtime/ticket` is served by the BFF itself and is never proxied upstream. It decrypts
the sealed `bff_session` cookie and returns

```json
{
  "systemId": "sys-001",
  "webrtcWhepUrl": "https://cam.example.com/sys-001/whep",
  "streamToken": "<edgeJwt>",
  "mqttWsUrl": "wss://cam.example.com/mqtt",
  "mqttUsername": "dashboard",
  "mqttPassword": "..."
}
```

The tenant always comes from the cookie, never from a query string or header, so a caller cannot
request another system's stream. The WHEP URL is built from `MEDIAMTX_PUBLIC_URL` (or
`NEXT_PUBLIC_MEDIAMTX_URL`); the broker URL from `MQTT_PUBLIC_WS_URL`. When either is unset the
ticket returns empty values and the dashboard keeps its MJPEG fallback and 3-second polling.

### Away mode

`GET`/`PUT /api/settings/away-mode` (alias `/api/away-mode-settings`) use:

```json
{
  "mode": "scheduled",
  "startTime": "22:00",
  "endTime": "06:00",
  "alertEmail": "alerts@example.com",
  "timeZone": "Europe/Bucharest"
}
```

- `mode` is `off`, `indefinite` or `scheduled`.
- `startTime`/`endTime` use `HH:mm` and support overnight ranges such as `22:00`-`06:00`.
- `timeZone` is an IANA id and defaults to `Europe/Bucharest`.
- `GET` also returns `isArmedNow`, computed at request time, and the legacy `isArmed` boolean.
- `PUT` still accepts the legacy `{ "isArmed": true|false }` shape.

Suspect evaluation uses the **event timestamp** (the offline-sync `capturedAt`, when present),
not the server's current time.

`POST /api/auth/login` accepts:

```json
{ "username": "alice", "password": "secret", "systemId": "sys-001" }
```

`systemId` is optional for factory administrators and for usernames that exist in exactly one
system. If the same username exists in several systems and `systemId` is omitted, the response is:

```json
{
  "title": "Multiple systems have a user with this username; please provide your Camera System ID.",
  "status": 400,
  "code": "SYSTEM_ID_REQUIRED"
}
```

## External factory-admin contract

Set `ExternalAuth:UseStub=false` and `ExternalAuth:BaseUrl` to the real service. The backend calls:

```
POST {ExternalAuth:BaseUrl}/api/internal/validate-admin
Content-Type: application/json
X-Internal-Secret: <ExternalAuth:InternalSecret>

{ "username": "cam_admin_1", "password": "..." }
```

The local Neon-backed implementation lives in [`external-auth-service/`](external-auth-service/).
It exposes `GET /health` and the validation endpoint above; the validation endpoint returns
`403` when `INTERNAL_API_SECRET` is configured and the header is missing or wrong.

Expected responses:

- `200 OK` — credentials are valid:

  ```json
  {
    "systemId": "sys-001",
    "username": "cam_admin_1",
    "piApiKey": "optional-camera-kit-api-key"
  }
  ```

  `systemId`, `username` and `piApiKey` camelCase spellings are accepted; the backend also
  accepts the legacy `system_id` and `pi_api_key` snake_case spellings. `piApiKey` is optional;
  when present its SHA-256 hash is stored in `PiDevices` so the camera kit can ingest frames as
  that system.
- `401 Unauthorized` or `404 Not Found` — invalid credentials. The backend then tries the local
  `Users` table (sub-user fallback).
- Any other status — treated as an outage and surfaced as a server error; there is no insecure
  fallback.

### Configuration

| Setting | Compose/default value | Purpose |
|---|---|---|
| `ExternalAuth__UseStub` | `false` in `compose.yaml`, `true` in Development | Uses `StubAccounts` instead of HTTP |
| `ExternalAuth__BaseUrl` | `http://external-auth-service:3001` | External IAM service base URL |
| `ExternalAuth__InternalSecret` | `${INTERNAL_API_SECRET}` | Shared `X-Internal-Secret` header value |
| `NEON_DB_URL` | Neon connection string | Database URL passed to the external service |
| `INTERNAL_API_SECRET` | long random value | Header secret shared by backend and external service |
| `ExternalAuth__StubAccounts__cam_admin_1__SystemId` | `sys-001` | Stub account tenant |
| `ExternalAuth__StubAccounts__cam_admin_1__Password` | `factory-pass-sys-001` | Stub password |
| `ExternalAuth__StubAccounts__cam_admin_1__PiApiKey` | `dev-pi-key-sys-001` | Optional kit key |
| `PiAuth__ApiKey` | `dev-only-api-key` | Legacy single-Pi key (mapped to `sys-001`) |
| `PiAuth__DefaultSystemId` | `sys-001` | Tenant for the legacy key |
| `PiAuth__Systems__sys-002` | `dev-only-api-key-sys-002` | Additional kit key per system |
| `PiAuth__AlertTerminalSystems__sys-001` | `dev-alert-terminal-key-sys-001` | Terminal key for `sys-001` |
| `PiAuth__AlertTerminalSystems__sys-002` | `dev-alert-terminal-key-sys-002` | Terminal key for `sys-002` |
| `Notifications__EmailCooldownSeconds` | `60` | Per-system e-mail cooldown |
| `Notifications__DashboardBaseUrl` | `http://localhost:3000` | Dashboard base for alert links |
| `Notifications__EmailRetryBaseDelayMilliseconds` | `1000` | First retry delay (doubles per retry) |
| `Smtp__Host` / `Smtp__Port` | `mailpit` / `1025` | SMTP server (compose default) |
| `Smtp__FromAddress` | `alerts@security-camera.local` | Alert sender address |
| `Alarm__DurationSeconds` | `15` | Terminal alarm window after a suspect event |
| `Jwt__ExpirationMinutes` | `30` | Dashboard token lifetime (logout revokes the `jti`) |
| `PiAuth__NodeSystemId` | empty | Pins a single-Pi deployment to its own `SystemId` |
| `Cors__AllowedOrigins` | empty | Explicit CORS allow-list; CORS is disabled when empty |
| `Auth__LoginRateLimit__PermitLimit` / `WindowSeconds` | `5` / `60` | Per-IP login requests per window |
| `Auth__LoginLockout__MaxFailures` / `LockoutSeconds` | `5` / `300` | Per username/system lockout |

Outside the Development (`Development`/`Testing`) environments the backend refuses to start while
any sample secret is still configured — `dev-only-api-key`, `dev-pi-key-*`,
`dev-alert-terminal-key-*`, `factory-pass-*`, a missing or shorter-than-32-byte `Jwt:Secret`, a
missing `ExternalAuth:InternalSecret`, or `ExternalAuth:UseStub=true`. Swagger/OpenAPI is only
mapped in Development, and request bodies are capped by Kestrel (10 MB) with per-endpoint limits
(10 MB for events, 250 KB for camera frames). Uploaded images are validated by magic bytes, not by
content type, before anything is written to disk.

`appsettings.Development.json`, `compose.yaml` and `.env.example` contain the two sample factory
kits (`cam_admin_1` → `sys-001`, `cam_admin_2` → `sys-002`). The external service seeds the same
defaults into Neon. Never use the sample passwords in production.

### Running the external auth service locally

1. Set `NEON_DB_URL` and a shared `INTERNAL_API_SECRET` in the root `.env` (used by Compose), and
   `DATABASE_URL` + the same `INTERNAL_API_SECRET` in `external-auth-service/.env` (used by the
   seed script and direct Node runs).
2. Create the `factory_kits` table and upsert the sample kits:

   ```bash
   cd external-auth-service
   npm install
   npm run seed
   ```

3. Start the stack from the repository root:

   ```bash
   docker compose up --build
   ```

   The backend waits for the `external-auth-service` healthcheck and calls it at
   `http://external-auth-service:3001`. The service is internal to the Compose bridge network and
   publishes no host port; for local debugging outside Compose, point `ExternalAuth__BaseUrl` at a
   locally running instance instead of publishing one.

For manual Cloudflare Workers deployment later, see
[`external-auth-service/README.md`](external-auth-service/README.md). No deployment is performed
by the local setup.

## Testing two camera systems locally

With the Compose stack (`ExternalAuth:UseStub=false`), both factory admins are validated by the
Neon-backed external service using the seeded sample kits. With a direct `dotnet run` in the
Development profile (`ExternalAuth:UseStub=true`), they are validated by the local stub.

1. Log in as the two administrators:

   ```bash
   curl -s http://localhost:8080/api/auth/login \
     -H 'Content-Type: application/json' \
     -d '{"username":"cam_admin_1","password":"factory-pass-sys-001"}'

   curl -s http://localhost:8080/api/auth/login \
     -H 'Content-Type: application/json' \
     -d '{"username":"cam_admin_2","password":"factory-pass-sys-002"}'
   ```

   Each response contains a token and `user.systemId` (`sys-001` / `sys-002`).

2. Create a sub-user with the same username in both systems (replace `<token1>`/`<token2>`):

   ```bash
   curl -s http://localhost:8080/api/users \
     -H "Authorization: Bearer <token1>" -H 'Content-Type: application/json' \
     -d '{"username":"alice","password":"alice-pass-1"}'

   curl -s http://localhost:8080/api/users \
     -H "Authorization: Bearer <token2>" -H 'Content-Type: application/json' \
     -d '{"username":"alice","password":"alice-pass-2"}'
   ```

   Both creations return `201`; the composite unique index is `(SystemId, Username)`.

3. Log in as `alice` without a system id — the API returns
   `400 SYSTEM_ID_REQUIRED`. Add `"systemId":"sys-001"` or `"systemId":"sys-002"` to receive a
   token scoped to the matching user.

4. Verify isolation:

   ```bash
   # Entrance logs only show the caller's system
   curl -s http://localhost:8080/api/events \
     -H "Authorization: Bearer <token1>"

   # Away mode is independent per system
   curl -s -X PUT http://localhost:8080/api/away-mode-settings \
     -H "Authorization: Bearer <token1>" -H 'Content-Type: application/json' \
     -d '{"isArmed":false}'
   ```

   `cam_admin_2` still sees `isArmed: true` for `sys-002`. New frames from the `sys-001` Pi key are
   stored with `SystemId = "sys-001"` and are never returned to `sys-002` users.

5. Pi ingestion uses the key configured for each system:

   ```bash
   curl -s -X POST http://localhost:8080/api/events \
     -H 'X-Api-Key: dev-only-api-key-sys-002' \
     -F 'image=@frame.jpg' -F 'isOfflineSync=false'
   ```

## Database

`dotnet ef database update` (or the automatic startup migration) adds:

- `Users.SystemId` with unique index `(SystemId, Username)`
- `DetectionEvents.SystemId` with index `(SystemId, Timestamp)`
- `DetectionEvents.ClientEventId` with unique index `(SystemId, ClientEventId)` for idempotent Pi retries
- `AwayModeSettings.Mode`, `StartTime`, `EndTime`, `AlertEmail`, `TimeZone` for scheduled away mode
- `PiDevices.DeviceType` (`Camera`/`AlertTerminal`, existing rows default to `Camera`) and
  `PiDevices.LastSeenAt` for online/offline tracking
- Alert-terminal keys are stored in the same `PiDevices` table as camera keys; outbound alerting
  needs no additional tables.
- `CameraSystems`, `PiDevices` (SHA-256 key hashes) and `AwayModeSettings`

Existing rows are migrated to `sys-001`, so the previous single-camera deployment keeps working.
On upgrade, image columns are rewritten to bare file names (served through the new JWT endpoints)
and existing away-mode rows keep their `isArmed` state (`Indefinite` when armed, `Off` otherwise).

## Tests

```bash
dotnet test
```

The suite starts a PostgreSQL 16 Testcontainer, runs the real migrations/seeding, uses the
external-admin stub, covers multiple tenants, and verifies that sub-user usernames, entrance logs,
image endpoints, live frames, device status and away-mode settings cannot leak across systems. It
also covers idempotent event retries, the camera frame size/rate limits, heartbeat throttling,
MJPEG streaming, stream caps, and includes unit tests for the schedule logic (exact start/end,
midnight crossing and DST transitions) and latest-frame expiry. Alerting tests use an injected
fake SMTP sender and cover suspect/non-suspect events, e-mail cooldown summarization, attachments,
terminal-only authentication, alarm expiry, and cross-tenant alarm isolation.

Security-specific coverage:

- `LoginHardeningTests` — a wrong password never reveals that a username exists in several systems
  (plain `401`, no `code`), a single matching password logs in without `systemId`, only a password
  valid in 2+ systems returns `400 SYSTEM_ID_REQUIRED`; per username/system lockout returns `429`
  with `code: LOGIN_LOCKED` and `Retry-After`; image uploads with fake magic bytes or a content
  type that contradicts the bytes are rejected with `400`.
- `ProductionSecretValidationTests` — every development/sample secret (dev API keys, factory
  passwords, short JWT keys, `ExternalAuth:UseStub=true`, stub accounts) is refused outside
  Development, and the real host fails to start with those settings.

The frontend has its own suite for the BFF:

```bash
cd frontend && npm test     # node --experimental-strip-types --test
```

It asserts that device/ingestion routes (`POST /api/events`, `POST /api/camera/frame`,
`/api/device/*`) return `404` through the BFF and never reach an upstream, that `X-Api-Key`,
`X-Internal-Secret` and client `Authorization` headers are stripped, that cross-origin
`POST`/`PUT`/`DELETE` requests get `403` (while still clearing the session on logout), that
`systemId` in a query string cannot retarget the proxy, that the sealed cookie is `HttpOnly`,
`SameSite=Lax`, `Path=/` and `Secure` when appropriate, and that MJPEG responses keep streaming
unbuffered.
