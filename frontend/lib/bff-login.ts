import {
  getClientIp,
  getDefaultBackendUrl,
  getEdgeBackends,
  isValidSystemId,
  resolveBackendUrl,
  isRequestOriginAllowed,
  UnknownSystemError,
  BackendNotConfiguredError,
} from "./edge-router.ts";
import { sessionSetCookieHeader } from "./session-cookie.ts";
import { getSessionSecret, sealSession } from "./session-crypto.ts";
import type { AuthUser, ProblemDetails } from "./types.ts";
import { isRouteAllowed } from "./route-allowlist.ts";

/**
 * `POST /api/auth/login` — leak-free login.
 *
 * The raw backend JWT never reaches browser JavaScript: it is sealed into the `bff_session`
 * cookie together with the verified `systemId` and its expiry.
 *
 * Dependency-free (no `next/*`) so it is unit testable with `node --test`.
 */

const LOGIN_TIMEOUT_MS = 15_000;

interface LoginSuccessBody {
  token: string;
  expiresAtUtc: string;
  user: AuthUser;
}

type Backend = { systemId: string; url: string };

type Authenticated = { systemId: string; login: LoginSuccessBody };

function problem(status: number, title: string, detail?: string, code?: string): Response {
  const body: Record<string, unknown> = { title, status };

  if (detail) {
    body.detail = detail;
  }

  if (code) {
    body.code = code;
  }

  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/problem+json", "Cache-Control": "no-store" },
  });
}

function forbidden(detail: string): Response {
  return problem(403, "Forbidden", detail);
}

function unauthorized(): Response {
  // Identical message for unknown users and bad passwords: no account enumeration.
  return problem(401, "Unauthorized", "Invalid username or password.");
}

async function postLogin(
  backendUrl: string,
  credentials: { username: string; password: string; systemId?: string },
  clientIp: string | null = null,
): Promise<Response> {
  const headers: Record<string, string> = {
    "Content-Type": "application/json",
    Accept: "application/json",
  };

  if (clientIp) {
    headers["X-Forwarded-For"] = clientIp;
  }

  return fetch(`${backendUrl}/api/auth/login`, {
    method: "POST",
    headers,
    body: JSON.stringify({
      username: credentials.username,
      password: credentials.password,
      systemId: credentials.systemId,
    }),
    cache: "no-store",
    signal: AbortSignal.timeout(LOGIN_TIMEOUT_MS),
  });
}

async function parseLogin(upstream: Response): Promise<LoginSuccessBody | null> {
  if (!upstream.ok) {
    return null;
  }

  try {
    const parsed = (await upstream.json()) as Partial<LoginSuccessBody>;

    if (typeof parsed.token !== "string" || !parsed.token) {
      return null;
    }

    if (typeof parsed.expiresAtUtc !== "string" || !parsed.expiresAtUtc) {
      return null;
    }

    if (!parsed.user || typeof parsed.user !== "object") {
      return null;
    }

    return parsed as LoginSuccessBody;
  } catch {
    return null;
  }
}

/** Re-emits a backend error without leaking anything it did not already say. */
async function relayBackendError(upstream: Response): Promise<Response> {
  const status = upstream.status >= 400 && upstream.status < 500 ? upstream.status : 502;

  let parsed: ProblemDetails | null = null;

  try {
    parsed = (await upstream.json()) as ProblemDetails;
  } catch {
    parsed = null;
  }

  if (!parsed || typeof parsed !== "object") {
    return status === 401 ? unauthorized() : problem(status, "Login failed");
  }

  // `SYSTEM_ID_REQUIRED` (and friends) drive the login form's extra field, so keep the code.
  return problem(
    status,
    parsed.title || (status === 401 ? "Unauthorized" : "Login failed"),
    parsed.detail,
    parsed.code,
  );
}

function expiresAtUnixSeconds(expiresAtUtc: string): number | null {
  const parsed = new Date(expiresAtUtc).getTime();

  return Number.isNaN(parsed) ? null : Math.floor(parsed / 1000);
}

function sealedSessionResponse(
  request: Request,
  systemId: string,
  login: LoginSuccessBody,
): Response {
  if (!isValidSystemId(systemId)) {
    return problem(502, "Unexpected backend response", "The backend returned an invalid system id.");
  }

  const exp = expiresAtUnixSeconds(login.expiresAtUtc);

  if (exp === null) {
    return problem(502, "Unexpected backend response", "The backend returned an invalid expiry.");
  }

  const sealed = sealSession({ systemId, edgeJwt: login.token, exp }, getSessionSecret());

  const headers = new Headers({
    "Content-Type": "application/json",
    "Cache-Control": "no-store",
  });
  headers.append("Set-Cookie", sessionSetCookieHeader(sealed, request, login.expiresAtUtc));

  // Only the identity + expiry go back to JS; the JWT stays inside the sealed cookie.
  return new Response(JSON.stringify({ user: login.user, expiresAtUtc: login.expiresAtUtc }), {
    status: 200,
    headers,
  });
}

/** Single-backend demo/`BACKEND_URL` mode, or the full `EDGE_BACKENDS_JSON` map. */
function allBackends(): Backend[] {
  const entries = Object.entries(getEdgeBackends()).map(([systemId, url]) => ({ systemId, url }));

  if (entries.length > 0) {
    return entries;
  }

  const fallback = getDefaultBackendUrl();

  if (!fallback) {
    return [];
  }

  return [{ systemId: process.env.BACKEND_DEFAULT_SYSTEM_ID?.trim() || "default", url: fallback }];
}

/**
 * Validates factory-admin credentials against the external factory IAM service. Only a `200`
 * carrying a `systemId` counts as success.
 */
async function validateExternalAdmin(
  username: string,
  password: string,
  clientIp: string | null,
): Promise<{ systemId: string } | null> {
  const base = process.env.EXTERNAL_AUTH_URL?.trim().replace(/\/+$/, "");

  if (!base) {
    return null;
  }

  const internalSecret = process.env.INTERNAL_API_SECRET?.trim();

  if (!internalSecret) {
    // Without the shared secret the call can only be rejected; skip it rather than leak a probe.
    return null;
  }

  try {
    const validateHeaders: Record<string, string> = {
      "Content-Type": "application/json",
      Accept: "application/json",
      "X-Internal-Secret": internalSecret,
    };

    if (clientIp) {
      validateHeaders["X-Forwarded-For"] = clientIp;
    }

    const response = await fetch(`${base}/api/internal/validate-admin`, {
      method: "POST",
      headers: validateHeaders,
      body: JSON.stringify({ username, password }),
      cache: "no-store",
      signal: AbortSignal.timeout(LOGIN_TIMEOUT_MS),
    });

    if (!response.ok) {
      return null;
    }

    const body = (await response.json()) as { systemId?: string; system_id?: string };
    const systemId = body.systemId ?? body.system_id;

    return isValidSystemId(systemId) ? { systemId } : null;
  } catch {
    return null;
  }
}

/**
 * Queries several backends concurrently with the same credentials.
 *
 * Only valid passwords can produce a success, so the outcome leaks nothing about which systems
 * merely *contain* the username.
 */
async function authenticateAcross(
  backends: Backend[],
  username: string,
  password: string,
  clientIp: string | null,
) {
  const attempts = await Promise.allSettled(
    backends.map(async (backend): Promise<Authenticated> => ({
      systemId: backend.systemId,
      login: (await parseLogin(await postLogin(backend.url, { username, password }, clientIp)))!,
    })),
  );

  const successes: Authenticated[] = [];

  for (const attempt of attempts) {
    if (attempt.status === "fulfilled" && attempt.value.login) {
      successes.push(attempt.value);
    }
  }

  return successes;
}

export async function handleLogin(request: Request): Promise<Response> {
  // Reuse the proxy allowlist so the login route stays in one place.
  if (!isRouteAllowed("POST", ["auth", "login"])) {
    return problem(404, "Not found");
  }

  // Forwarded so the backend's per-client-IP login limiter partitions real callers instead of
  // counting the dashboard container as one client. `null` unless a trusted proxy is configured.
  const clientIp = getClientIp(request);

  if (!isRequestOriginAllowed(request)) {
    return forbidden("Cross-origin login requests are not allowed.");
  }

  let raw: unknown;

  try {
    raw = await request.json();
  } catch {
    return problem(400, "Invalid request", "The login body must be valid JSON.");
  }

  if (typeof raw !== "object" || raw === null || Array.isArray(raw)) {
    return problem(400, "Invalid request", "The login body must be a JSON object.");
  }

  const body = raw as Record<string, unknown>;
  const username = typeof body.username === "string" ? body.username : "";
  const password = typeof body.password === "string" ? body.password : "";
  const requestedSystemId =
    typeof body.systemId === "string" && body.systemId.trim() !== ""
      ? body.systemId.trim()
      : undefined;

  if (requestedSystemId !== undefined && !isValidSystemId(requestedSystemId)) {
    return problem(400, "Invalid request", "systemId must match ^[a-zA-Z0-9_-]{1,64}$.");
  }

  // 1. Explicit systemId: talk only to that system's private backend.
  if (requestedSystemId) {
    let backendUrl: string;

    try {
      backendUrl = resolveBackendUrl(requestedSystemId);
    } catch (error) {
      if (error instanceof UnknownSystemError) {
        return unauthorized();
      }

      return problem(
        500,
        "Dashboard misconfigured",
        "No private backend is configured. Set EDGE_BACKENDS_JSON or BACKEND_URL.",
      );
    }

    let upstream: Response;

    try {
      upstream = await postLogin(
        backendUrl,
        { username, password, systemId: requestedSystemId },
        clientIp,
      );
    } catch {
      return problem(
        502,
        "Cannot reach the camera backend",
        "The backend is unavailable. Check EDGE_BACKENDS_JSON / BACKEND_URL and try again.",
      );
    }

    if (!upstream.ok) {
      return relayBackendError(upstream);
    }

    const login = await parseLogin(upstream);

    return login
      ? sealedSessionResponse(request, requestedSystemId, login)
      : problem(502, "Unexpected backend response", "The login response was incomplete.");
  }

  // 2. No systemId: factory administrators are validated by the external IAM service first.
  const externalAdmin = await validateExternalAdmin(username, password, clientIp);

  if (externalAdmin) {
    let backendUrl: string | null = null;

    try {
      backendUrl = resolveBackendUrl(externalAdmin.systemId);
    } catch (error) {
      if (error instanceof BackendNotConfiguredError) {
        return problem(
          500,
          "Dashboard misconfigured",
          "No private backend is configured. Set EDGE_BACKENDS_JSON or BACKEND_URL.",
        );
      }

      backendUrl = null;
    }

    if (backendUrl) {
      try {
        const upstream = await postLogin(backendUrl, { username, password }, clientIp);
        const login = upstream.ok ? await parseLogin(upstream) : null;

        if (login) {
          return sealedSessionResponse(request, externalAdmin.systemId, login);
        }
      } catch {
        // Fall through: local sub-users are still worth checking.
      }
    }
  }

  const backends = allBackends();

  if (backends.length === 0) {
    return problem(
      500,
      "Dashboard misconfigured",
      "No private backend is configured. Set EDGE_BACKENDS_JSON or BACKEND_URL.",
    );
  }

  // 3. Single backend: forward directly and relay its answer (including SYSTEM_ID_REQUIRED).
  if (backends.length === 1) {
    const only = backends[0];

    let upstream: Response;

    try {
      upstream = await postLogin(only.url, { username, password }, clientIp);
    } catch {
      return problem(
        502,
        "Cannot reach the camera backend",
        "The backend is unavailable. Check BACKEND_URL and try again.",
      );
    }

    if (!upstream.ok) {
      return relayBackendError(upstream);
    }

    const login = await parseLogin(upstream);

    if (!login) {
      return problem(502, "Unexpected backend response", "The login response was incomplete.");
    }

    // Trust the backend's own tenant claim over the routing key in single-backend mode.
    const systemId = isValidSystemId(login.user.systemId) ? login.user.systemId : only.systemId;

    return sealedSessionResponse(request, systemId, login);
  }

  // 4. Multiple systems: 0 -> 401, 1 -> seal, 2+ -> 400 SYSTEM_ID_REQUIRED.
  const successes = await authenticateAcross(backends, username, password, clientIp);

  if (successes.length === 0) {
    return unauthorized();
  }

  if (successes.length > 1) {
    return problem(
      400,
      "Multiple systems have a user with this username; please provide your Camera System ID.",
      undefined,
      "SYSTEM_ID_REQUIRED",
    );
  }

  return sealedSessionResponse(request, successes[0].systemId, successes[0].login);
}
