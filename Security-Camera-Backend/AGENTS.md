Project: Smart Entrance Security & Access Monitoring System. Three tiers:
Raspberry Pi 5 (camera, motion detection) -> home server (ASP.NET Core + PostgreSQL
backend, Python FastAPI cv-service in Docker Compose) -> ESP32 (16x2 LCD + buzzer).
Plus a web dashboard.

Rules:
- Multi-tenant. The tenant comes ONLY from the JWT `system_id` claim or from the
  device API key. Never from a request body, query string or header sent by a client.
- Access to another tenant's resource returns 404, not 403.
- Every new endpoint gets integration tests in the existing Testcontainers suite,
  including a sys-001 vs sys-002 leak test.
- Keep the README endpoint table, compose.yaml and .env.example in sync with the code.
- No raw SQL string building; use EF Core. Hash secrets; never log passwords/keys/JWTs.
- Before coding, reply with a short plan and any ambiguities in the contract. Make
  reasonable assumptions for non-blocking ones and state them.
- Run `dotnet test` (or the relevant test command) before declaring a step done.
- Small, focused commits. No unrelated refactors.