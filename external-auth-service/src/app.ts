import { compare } from "bcryptjs";
import { neon } from "@neondatabase/serverless";
import { Hono } from "hono";
import type { Context } from "hono";

export type ExternalAuthEnv = {
  DATABASE_URL?: string;
  INTERNAL_API_SECRET?: string;
};

type FactoryKitRow = {
  system_id: string;
  admin_username: string;
  password_hash: string;
  pi_api_key: string;
};

type ValidateAdminBody = {
  username?: unknown;
  password?: unknown;
};

const app = new Hono<{ Bindings: ExternalAuthEnv }>();

/**
 * Cloudflare Workers expose bindings/secrets on `c.env`; the Node adapter usually does not.
 * Fall back to `process.env` only in Node, and never import a Node-only module here.
 */
function readEnv(c: Context<{ Bindings: ExternalAuthEnv }>, key: keyof ExternalAuthEnv): string | undefined {
  const bindingValue = c.env?.[key];
  if (typeof bindingValue === "string" && bindingValue.length > 0) {
    return bindingValue;
  }

  if (typeof process !== "undefined") {
    const processValue = process.env[key];
    if (processValue && processValue.length > 0) {
      return processValue;
    }
  }

  return undefined;
}

/** Constant-time comparison so the internal secret is not leaked through response timing. */
function secretsMatch(provided: string | undefined, expected: string | undefined): boolean {
  if (!expected) {
    return true;
  }

  if (!provided) {
    return false;
  }

  const encoder = new TextEncoder();
  const providedBytes = encoder.encode(provided);
  const expectedBytes = encoder.encode(expected);
  const length = Math.max(providedBytes.length, expectedBytes.length);

  let difference = providedBytes.length ^ expectedBytes.length;
  for (let index = 0; index < length; index += 1) {
    difference |= (providedBytes[index] ?? 0) ^ (expectedBytes[index] ?? 0);
  }

  return difference === 0;
}

app.get("/health", (c) => c.json({ status: "ok" }));

app.post("/api/internal/validate-admin", async (c) => {
  const expectedSecret = readEnv(c, "INTERNAL_API_SECRET");
  const providedSecret = c.req.header("X-Internal-Secret");

  if (!secretsMatch(providedSecret, expectedSecret)) {
    return c.json({ error: "Forbidden" }, 403);
  }

  let body: ValidateAdminBody;
  try {
    body = await c.req.json<ValidateAdminBody>();
  } catch {
    return c.json({ error: "Invalid JSON body" }, 400);
  }

  if (typeof body.username !== "string" || typeof body.password !== "string") {
    return c.json({ error: "username and password are required" }, 400);
  }

  const username = body.username.trim();
  if (!username || !body.password) {
    return c.json({ error: "username and password are required" }, 400);
  }

  const databaseUrl = readEnv(c, "DATABASE_URL");
  if (!databaseUrl) {
    console.error("DATABASE_URL is not configured.");
    return c.json({ error: "Authentication service is not configured" }, 500);
  }

  try {
    const sql = neon(databaseUrl);
    const rows = (await sql`
      SELECT system_id, admin_username, password_hash, pi_api_key
      FROM factory_kits
      WHERE admin_username = ${username}
        AND is_active = TRUE
      LIMIT 1
    `) as FactoryKitRow[];

    const kit = rows[0];
    if (!kit) {
      return c.json({ error: "Invalid factory admin credentials" }, 401);
    }

    const passwordMatches = await compare(body.password, kit.password_hash);
    if (!passwordMatches) {
      return c.json({ error: "Invalid factory admin credentials" }, 401);
    }

    return c.json({
      systemId: kit.system_id,
      username: kit.admin_username,
      piApiKey: kit.pi_api_key
    });
  } catch (error) {
    // Never log the request body: it contains the administrator password.
    console.error("Factory admin validation failed.", error instanceof Error ? error.message : error);
    return c.json({ error: "Authentication service unavailable" }, 500);
  }
});

app.notFound((c) => c.json({ error: "Not found" }, 404));

export { app };
export default app;
