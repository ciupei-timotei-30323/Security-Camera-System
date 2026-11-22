# External factory-admin auth service

Small Hono service that validates factory camera-kit administrator credentials against Neon
PostgreSQL. The same app runs in two places:

- **Node.js / Docker Compose** via `src/node.ts` on `0.0.0.0:3001`.
- **Cloudflare Workers** via `src/index.ts` (the Hono app's default export).

`src/app.ts` contains all routes and reads `DATABASE_URL` / `INTERNAL_API_SECRET` from
`c.env`, falling back to `process.env` in Node. The Neon serverless driver and `bcryptjs` are
used so there are no native Node addons in the Worker bundle.

## Endpoints

| Method | Route | Auth | Response |
|---|---|---|---|
| `GET` | `/health` | none | `200 { "status": "ok" }` |
| `POST` | `/api/internal/validate-admin` | `X-Internal-Secret` when configured | `200` kit data, `401` bad credentials, `403` bad secret, `400` malformed body |

Successful validation response:

```json
{
  "systemId": "sys-001",
  "username": "cam_admin_1",
  "piApiKey": "dev-pi-key-sys-001"
}
```

The ASP.NET Core backend accepts both these camelCase names and the legacy snake_case names.

## Local setup

```bash
cd external-auth-service
cp .env.example .env
# edit .env: DATABASE_URL and INTERNAL_API_SECRET
npm install
npm run seed
npm run dev
```

The seed script creates `factory_kits` from `schema.sql` and upserts `sys-001` / `cam_admin_1`
and `sys-002` / `cam_admin_2`. Default passwords and Pi keys match the backend development stub:

- `cam_admin_1` / `factory-pass-sys-001` / `dev-pi-key-sys-001`
- `cam_admin_2` / `factory-pass-sys-002` / `dev-pi-key-sys-002`

Override them with `SEED_CAM_ADMIN_1_PASSWORD`, `SEED_CAM_ADMIN_2_PASSWORD`,
`SEED_PI_API_KEY_SYS_001` and `SEED_PI_API_KEY_SYS_002` if needed.

Docker Compose builds this directory with the multi-stage `Dockerfile` and runs the compiled
`dist/src/node.js`. The root compose stack is `Security-Camera-Backend/compose.yaml` and reads
`NEON_DB_URL` + `INTERNAL_API_SECRET` from `Security-Camera-Backend/.env`.

## Manual Cloudflare Workers deployment

Do this only when you are ready; `wrangler.toml` intentionally contains no secrets.

```bash
cd external-auth-service
npx wrangler login
npx wrangler secret put DATABASE_URL
npx wrangler secret put INTERNAL_API_SECRET
npx wrangler deploy
```

After deploying, point `ExternalAuth__BaseUrl` at the Worker URL and set
`ExternalAuth__InternalSecret` to the same `INTERNAL_API_SECRET` used by the Worker. The Worker
does not need a `nodejs_compat` flag for these dependencies.
