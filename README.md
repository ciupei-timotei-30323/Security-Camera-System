# Security Camera System

ASP.NET Core backend for the Raspberry Pi security camera. The API is multi-tenant: every
sub-user, entrance log, away-mode setting and Pi API key belongs to exactly one camera system
(`SystemId`, for example `sys-001` or `sys-002`).

## Web dashboard

The Next.js dashboard lives in [`frontend/`](frontend/) and talks to this API through a
same-origin BFF. Route handlers own the JWT in an `httpOnly`, `SameSite=Lax` cookie and
the catch-all `/api/[...path]` proxy streams request/response bodies without buffering, so MJPEG and
`<img src="/api/events/{id}/face">` work while the token never reaches browser JavaScript.

Local development:

```bash
cd frontend
cp .env.example .env.local   # BACKEND_URL=http://localhost:8080
npm install
npm run dev                  # http://localhost:3000
```

`Security-Camera-Backend/compose.yaml` also defines a `dashboard` service on port `3000`
with `BACKEND_URL=http://security-camera-backend:8080`.

## Raspberry Pi capture daemon

The Raspberry Pi 5 daemon lives in [`Security-Camera-Pi/`](Security-Camera-Pi/README.md). It runs
Picamera2 motion detection locally, uploads full-resolution stills to `POST /api/events`, and
pushes a ~640x360 live preview to `POST /api/camera/frame`. Events that cannot reach the backend
are kept in a durable `offline_queue/` and retried oldest-first with exponential backoff; the
queue is bounded by `QUEUE_MAX_MB`. It never listens for inbound connections.

See the daemon README for installation, `.env` settings, the systemd unit and the offline
queue/rejected-file behaviour.

## Multi-tenancy

- **Factory administrators** are validated by an external IAM service (or the local stub in
  development). A successful login registers the administrator's camera system and Pi key locally.
- **Sub-users** live in PostgreSQL and are unique per system. Two systems may both have a user
  named `alice`; logging in without a `systemId` then returns
  `400 SYSTEM_ID_REQUIRED` so the dashboard can ask which system to use.
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
| `Notifications__DashboardBaseUrl` | `http://localhost:5173` | Dashboard base for alert links |
| `Notifications__EmailRetryBaseDelayMilliseconds` | `1000` | First retry delay (doubles per retry) |
| `Smtp__Host` / `Smtp__Port` | `mailpit` / `1025` | SMTP server (compose default) |
| `Smtp__FromAddress` | `alerts@security-camera.local` | Alert sender address |
| `Alarm__DurationSeconds` | `15` | Terminal alarm window after a suspect event |

`appsettings.Development.json`, `compose.yaml` and `.env.example` contain the two sample factory
kits (`cam_admin_1` → `sys-001`, `cam_admin_2` → `sys-002`). The external service seeds the same
defaults into Neon. Never use the sample passwords in production.

### Running the external auth service locally

1. Set `NEON_DB_URL` and a shared `INTERNAL_API_SECRET` in `Security-Camera-Backend/.env` (used by
   Compose), and `DATABASE_URL` + the same `INTERNAL_API_SECRET` in
   `external-auth-service/.env` (used by the seed script and direct Node runs).
2. Create the `factory_kits` table and upsert the sample kits:

   ```bash
   cd external-auth-service
   npm install
   npm run seed
   ```

3. Start the stack:

   ```bash
   cd Security-Camera-Backend
   docker compose up --build
   ```

   The backend waits for the `external-auth-service` healthcheck and calls it at
   `http://external-auth-service:3001`. The service is otherwise internal to the Compose bridge
   network; `127.0.0.1:3001` is published only for local debugging.

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
