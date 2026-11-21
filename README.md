# Security Camera System

ASP.NET Core backend for the Raspberry Pi security camera. The API is multi-tenant: every
sub-user, entrance log, away-mode setting and Pi API key belongs to exactly one camera system
(`SystemId`, for example `sys-001` or `sys-002`).

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
| `GET` | `/api/events` | JWT | entrance logs for the JWT's system |
| `POST` | `/api/events` | Pi `X-Api-Key` | ingests a frame for the key's system |
| `POST` | `/api/events/analyze` | Pi `X-Api-Key` | CV analysis for the key's system |
| `GET`/`PUT` | `/api/away-mode-settings` | JWT | per-system arm/disarm state |
| `GET`/`PUT` | `/api/settings/away-mode` | JWT | alias for the same setting |

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

{ "username": "cam_admin_1", "password": "..." }
```

Expected responses:

- `200 OK` — credentials are valid:

  ```json
  {
    "system_id": "sys-001",
    "username": "cam_admin_1",
    "pi_api_key": "optional-camera-kit-api-key"
  }
  ```

  `systemId`, `username` and `piApiKey` camelCase spellings are also accepted. `pi_api_key` is
  optional; when present its SHA-256 hash is stored in `PiDevices` so the camera kit can ingest
  frames as that system.
- `401 Unauthorized` or `404 Not Found` — invalid credentials. The backend then tries the local
  `Users` table (sub-user fallback).
- Any other status — treated as an outage and surfaced as a server error; there is no insecure
  fallback.

### Configuration

| Setting | Development value | Purpose |
|---|---|---|
| `ExternalAuth__UseStub` | `true` | Uses `StubAccounts` instead of HTTP |
| `ExternalAuth__BaseUrl` | `http://external-auth:8080` | Future IAM service base URL |
| `ExternalAuth__StubAccounts__cam_admin_1__SystemId` | `sys-001` | Stub account tenant |
| `ExternalAuth__StubAccounts__cam_admin_1__Password` | `factory-pass-sys-001` | Stub password |
| `ExternalAuth__StubAccounts__cam_admin_1__PiApiKey` | `dev-pi-key-sys-001` | Optional kit key |
| `PiAuth__ApiKey` | `dev-only-api-key` | Legacy single-Pi key (mapped to `sys-001`) |
| `PiAuth__DefaultSystemId` | `sys-001` | Tenant for the legacy key |
| `PiAuth__Systems__sys-002` | `dev-only-api-key-sys-002` | Additional kit key per system |

`appsettings.Development.json`, `compose.yaml` and `.env.example` contain the two sample factory
kits (`cam_admin_1` → `sys-001`, `cam_admin_2` → `sys-002`). Never use the sample passwords in
production.

## Testing two camera systems locally

With the development profile (`ExternalAuth:UseStub=true`) both factory admins are available
without the external service.

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
- `CameraSystems`, `PiDevices` (SHA-256 key hashes) and `AwayModeSettings`

Existing rows are migrated to `sys-001`, so the previous single-camera deployment keeps working.

## Tests

```bash
dotnet test
```

The integration suite starts a PostgreSQL 16 Testcontainer, runs the real migrations/seeding,
uses the external-admin stub, covers both tenants, and verifies that sub-user usernames, entrance
logs and away-mode settings cannot leak across `sys-001` and `sys-002`.
