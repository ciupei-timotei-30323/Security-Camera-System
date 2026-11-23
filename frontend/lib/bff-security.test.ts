import assert from "node:assert/strict";
import test, { afterEach, beforeEach } from "node:test";
import { handleProxy } from "./bff-proxy.ts";
import { handleLogin } from "./bff-login.ts";
import { handleLogout } from "./bff-logout.ts";
import { sealSession, SESSION_COOKIE_NAME } from "./session-crypto.ts";
import { sessionCookieOptions } from "./session-cookie.ts";
import { isRouteAllowed } from "./route-allowlist.ts";
import { buildUpstreamHeaders, parseEdgeBackends, resolveBackendUrl } from "./edge-router.ts";

const SECRET = "test-bff-session-secret-at-least-32-bytes-long";
const REAL_FETCH = globalThis.fetch;

interface Captured {
  url: string;
  method: string;
  headers: Headers;
  body: string | null;
}

function captureFetch(responder: () => Response): Captured[] {
  const calls: Captured[] = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = input instanceof Request ? input : new Request(input as string, init);

    calls.push({
      url: request.url,
      method: request.method,
      headers: request.headers,
      body: init?.body ? String(init.body) : null,
    });

    return responder();
  }) as typeof fetch;

  return calls;
}

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function sessionCookie(systemId: string, ttlSeconds = 600): string {
  const exp = Math.floor(Date.now() / 1000) + ttlSeconds;
  const sealed = sealSession({ systemId, edgeJwt: `jwt-for-${systemId}`, exp }, SECRET);

  return `${SESSION_COOKIE_NAME}=${encodeURIComponent(sealed)}`;
}

function proxyRequest(
  path: string,
  init: { method?: string; headers?: Record<string, string>; body?: BodyInit } = {},
): Request {
  return new Request(`https://dashboard.example.com/api/${path}`, {
    method: init.method ?? "GET",
    headers: { host: "dashboard.example.com", ...(init.headers ?? {}) },
    body: init.body,
  });
}

beforeEach(() => {
  process.env.BFF_SESSION_SECRET = SECRET;
  process.env.EDGE_BACKENDS_JSON = JSON.stringify({
    "sys-001": "http://100.64.0.10:8080",
    "sys-002": "http://security-camera-backend-2:8080",
  });
  delete process.env.BACKEND_URL;
  delete process.env.ALLOWED_ORIGINS;
  delete process.env.EXTERNAL_AUTH_URL;
  delete process.env.COOKIE_SECURE;
});

afterEach(() => {
  globalThis.fetch = REAL_FETCH;
});

// --- (a) device / ingestion routes can never be reached through the BFF -----------------------

test("allowlist rejects device and ingestion routes", () => {
  const forbiddenRoutes: Array<[string, string[]]> = [
    ["POST", ["events"]],
    ["POST", ["events", "analyze"]],
    ["POST", ["camera", "frame"]],
    ["GET", ["device", "status"]],
    ["POST", ["device", "register"]],
  ];

  for (const [method, path] of forbiddenRoutes) {
    assert.equal(isRouteAllowed(method, path), false, `${method} /api/${path.join("/")}`);
  }

  assert.equal(isRouteAllowed("GET", ["events"]), true);
  assert.equal(isRouteAllowed("GET", ["events", "12", "face"]), true);
  assert.equal(isRouteAllowed("GET", ["events", "abc"]), false);
});

test("BFF answers 404 (and never proxies) for device routes", async () => {
  const calls = captureFetch(() => jsonResponse(200, { ok: true }));

  const cases: Array<[string, string, BodyInit | undefined]> = [
    ["POST", "events", JSON.stringify({ hello: "world" })],
    ["POST", "events/analyze", JSON.stringify({ hello: "world" })],
    ["POST", "camera/frame", "frame-bytes"],
    ["GET", "device/status", undefined],
    ["POST", "device/status", undefined],
  ];

  for (const [method, path, body] of cases) {
    const response = await handleProxy(
      proxyRequest(path, {
        method,
        headers: {
          "content-type": "application/json",
          authorization: "Bearer client-token",
          "x-api-key": "dev-only-api-key",
          origin: "https://dashboard.example.com",
          cookie: sessionCookie("sys-001"),
        },
        body,
      }),
      path.split("/"),
    );

    assert.equal(response.status, 404, `${method} /api/${path} must be 404`);
  }

  assert.equal(calls.length, 0, "no device request may reach an upstream backend");
});

test("proxy strips X-Api-Key, X-Internal-Secret and client Authorization", async () => {
  const calls = captureFetch(() => jsonResponse(200, { status: "ok" }));

  const response = await handleProxy(
    proxyRequest("system/status", {
      headers: {
        authorization: "Bearer client-supplied",
        "x-api-key": "dev-only-api-key",
        "x-internal-secret": "dev-only-internal-secret",
        cookie: sessionCookie("sys-001"),
      },
    }),
    ["system", "status"],
  );

  assert.equal(response.status, 200);
  assert.equal(calls.length, 1);

  const upstream = calls[0];
  assert.equal(upstream.url, "http://100.64.0.10:8080/api/system/status");
  assert.equal(upstream.headers.get("authorization"), "Bearer jwt-for-sys-001");
  assert.equal(upstream.headers.get("x-api-key"), null);
  assert.equal(upstream.headers.get("x-internal-secret"), null);
  assert.equal(upstream.headers.get("cookie"), null);
});

test("buildUpstreamHeaders can never forward credential headers", () => {
  const request = proxyRequest("users", {
    headers: {
      authorization: "Bearer nope",
      "x-api-key": "nope",
      "x-internal-secret": "nope",
    },
  });

  const headers = buildUpstreamHeaders(request, "sealed-jwt");

  assert.equal(headers.get("authorization"), "Bearer sealed-jwt");
  assert.equal(headers.get("x-api-key"), null);
  assert.equal(headers.get("x-internal-secret"), null);
});

// --- (b) CSRF: cross-origin state-changing requests are rejected -----------------------------

test("proxy rejects cross-origin POST/PUT/PATCH/DELETE with 403", async () => {
  const calls = captureFetch(() => jsonResponse(200, {}));

  const cases: Array<[string, string]> = [
    ["POST", "users"],
    ["DELETE", "users/3"],
    ["PUT", "away-mode-settings"],
    ["PUT", "settings/away-mode"],
    ["DELETE", "events/5"],
  ];

  for (const [method, path] of cases) {
    const response = await handleProxy(
      proxyRequest(path, {
        method,
        headers: {
          "content-type": "application/json",
          origin: "https://evil.example.net",
          cookie: sessionCookie("sys-001"),
        },
        body: method === "DELETE" ? undefined : "{}",
      }),
      path.split("/"),
    );

    assert.equal(response.status, 403, `${method} /api/${path} must be 403`);
  }

  assert.equal(calls.length, 0);
});

test("proxy rejects state-changing requests without an Origin header", async () => {
  const response = await handleProxy(
    proxyRequest("users", {
      method: "POST",
      headers: { "content-type": "application/json", cookie: sessionCookie("sys-001") },
      body: "{}",
    }),
    ["users"],
  );

  assert.equal(response.status, 403);
});

test("proxy accepts same-origin and allowlisted-origin writes", async () => {
  captureFetch(() => jsonResponse(200, {}));

  const sameOrigin = await handleProxy(
    proxyRequest("users", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        origin: "https://dashboard.example.com",
        cookie: sessionCookie("sys-001"),
      },
      body: "{}",
    }),
    ["users"],
  );

  assert.equal(sameOrigin.status, 200);

  process.env.ALLOWED_ORIGINS = "https://dashboard.example.com, https://other.example.com";

  const forwardedHost = await handleProxy(
    new Request("https://backend.internal/api/users", {
      method: "POST",
      headers: {
        host: "backend.internal",
        "x-forwarded-host": "camera.example.com",
        origin: "https://camera.example.com",
        "content-type": "application/json",
        cookie: sessionCookie("sys-001"),
      },
      body: "{}",
    }),
    ["users"],
  );

  assert.equal(forwardedHost.status, 200);

  const allowlisted = await handleProxy(
    new Request("https://backend.internal/api/users", {
      method: "POST",
      headers: {
        host: "backend.internal",
        origin: "https://other.example.com",
        "content-type": "application/json",
        cookie: sessionCookie("sys-001"),
      },
      body: "{}",
    }),
    ["users"],
  );

  assert.equal(allowlisted.status, 200);
});

test("logout rejects cross-origin but still clears the cookie", async () => {
  const calls = captureFetch(() => jsonResponse(200, {}));

  const response = await handleLogout(
    new Request("https://dashboard.example.com/api/auth/logout", {
      method: "POST",
      headers: {
        host: "dashboard.example.com",
        origin: "https://evil.example.net",
        cookie: sessionCookie("sys-001"),
      },
    }),
  );

  assert.equal(response.status, 403);
  assert.match(response.headers.get("set-cookie") ?? "", /bff_session=;/);
  assert.equal(calls.length, 0);
});

// --- session cookie policy -------------------------------------------------------------------

test("session cookie is HttpOnly, SameSite=Lax, Path=/ and Secure only over https", () => {
  const plain = sessionCookieOptions("2030-01-01T00:00:00Z", new Request("http://lan.local/"));
  assert.equal(plain.httpOnly, true);
  assert.equal(plain.sameSite, "lax");
  assert.equal(plain.path, "/");
  assert.equal(plain.secure, false);

  const tunnel = sessionCookieOptions(
    "2030-01-01T00:00:00Z",
    new Request("http://localhost:3000/", { headers: { "x-forwarded-proto": "https" } }),
  );
  assert.equal(tunnel.secure, true);

  const previous = process.env.NODE_ENV;
  // `NODE_ENV` is read at call time, so a temporary cast is enough for this assertion.
  (process.env as Record<string, string | undefined>).NODE_ENV = "production";

  try {
    assert.equal(sessionCookieOptions().secure, true);
  } finally {
    (process.env as Record<string, string | undefined>).NODE_ENV = previous;
  }
});

test("proxy clears the cookie and answers 401 when the sealed cookie is missing or tampered", async () => {
  const missing = await handleProxy(
    proxyRequest("system/status", { headers: {} }),
    ["system", "status"],
  );

  assert.equal(missing.status, 401);
  assert.match(missing.headers.get("set-cookie") ?? "", /bff_session=;/);

  const tampered = await handleProxy(
    proxyRequest("system/status", {
      headers: { cookie: `${SESSION_COOKIE_NAME}=v1.aaaa.bbbb.cccc` },
    }),
    ["system", "status"],
  );

  assert.equal(tampered.status, 401);
});

test("expired sealed cookies are rejected", async () => {
  const expired = await handleProxy(
    proxyRequest("system/status", { headers: { cookie: sessionCookie("sys-001", -60) } }),
    ["system", "status"],
  );

  assert.equal(expired.status, 401);
});

test("proxy routes strictly by the sealed systemId and 403s unmapped systems", async () => {
  const calls = captureFetch(() => jsonResponse(200, { ok: true }));

  const response = await handleProxy(
    proxyRequest("system/status?systemId=sys-002", {
      headers: { cookie: sessionCookie("sys-001") },
    }),
    ["system", "status"],
  );

  assert.equal(response.status, 200);
  // A query-string `systemId` must never influence the upstream target.
  assert.equal(calls[0].url, "http://100.64.0.10:8080/api/system/status?systemId=sys-002");

  const unmapped = await handleProxy(
    proxyRequest("system/status", { headers: { cookie: sessionCookie("sys-999") } }),
    ["system", "status"],
  );

  assert.equal(unmapped.status, 403);
});

test("MJPEG streams keep their content type and are marked non-buffered", async () => {
  captureFetch(
    () =>
      new Response("--frame\r\n", {
        status: 200,
        headers: { "Content-Type": "multipart/x-mixed-replace; boundary=frame" },
      }),
  );

  const response = await handleProxy(
    proxyRequest("camera/stream", { headers: { cookie: sessionCookie("sys-001") } }),
    ["camera", "stream"],
  );

  assert.equal(response.status, 200);
  assert.match(response.headers.get("content-type") ?? "", /multipart\/x-mixed-replace/);
  assert.equal(response.headers.get("cache-control"), "no-store, no-transform");
  assert.equal(response.headers.get("x-accel-buffering"), "no");
});

test("upstream 401 is turned into a cleared session", async () => {
  captureFetch(() => jsonResponse(401, { title: "Unauthorized" }));

  const response = await handleProxy(
    proxyRequest("auth/me", { headers: { cookie: sessionCookie("sys-001") } }),
    ["auth", "me"],
  );

  assert.equal(response.status, 401);
  assert.match(response.headers.get("set-cookie") ?? "", /bff_session=;/);
});

// --- edge resolution -------------------------------------------------------------------------

test("EDGE_BACKENDS_JSON is parsed defensively", () => {
  assert.deepEqual(parseEdgeBackends('{"sys-001":"http://10.0.0.1:8080/"}'), {
    "sys-001": "http://10.0.0.1:8080",
  });
  assert.deepEqual(parseEdgeBackends("not json"), {});
  assert.deepEqual(parseEdgeBackends('{"bad id":"http://10.0.0.1"}'), {});
  assert.deepEqual(parseEdgeBackends('{"sys-001":"file:///etc/passwd"}'), {});
});

test("resolveBackendUrl falls back to BACKEND_URL only when no map entry exists", () => {
  process.env.BACKEND_URL = "http://security-camera-backend:8080";

  assert.equal(resolveBackendUrl("sys-001"), "http://100.64.0.10:8080");
  assert.equal(resolveBackendUrl("default"), "http://security-camera-backend:8080");

  delete process.env.EDGE_BACKENDS_JSON;

  assert.equal(resolveBackendUrl("anything"), "http://security-camera-backend:8080");
  assert.throws(() => resolveBackendUrl("bad id!"), /Invalid camera system id/);
});

// --- (c) login never leaks SYSTEM_ID_REQUIRED before checking passwords -----------------------

function loginRequest(body: unknown): Request {
  return new Request("https://dashboard.example.com/api/auth/login", {
    method: "POST",
    headers: {
      host: "dashboard.example.com",
      origin: "https://dashboard.example.com",
      "content-type": "application/json",
    },
    body: JSON.stringify(body),
  });
}

test("login with a systemId seals the JWT into the cookie and never returns it", async () => {
  const calls = captureFetch(() =>
    jsonResponse(200, {
      token: "backend-jwt-1",
      expiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
      user: { id: 1, username: "admin", role: "Admin", systemId: "sys-001" },
    }),
  );

  const response = await handleLogin(
    loginRequest({ username: "admin", password: "secret", systemId: "sys-001" }),
  );

  assert.equal(response.status, 200);
  assert.equal(calls[0].url, "http://100.64.0.10:8080/api/auth/login");

  const raw = await response.text();
  assert.equal(raw.includes("backend-jwt-1"), false, "the raw JWT must never reach JS");

  const setCookie = response.headers.get("set-cookie") ?? "";
  assert.match(setCookie, /bff_session=v1\./);
  assert.match(setCookie, /HttpOnly/i);
  assert.match(setCookie, /SameSite=lax/i);
  assert.match(setCookie, /Path=\//);
});

test("login without systemId returns 401 when no backend authenticates", async () => {
  captureFetch(() => jsonResponse(401, { title: "Unauthorized" }));

  const response = await handleLogin(loginRequest({ username: "admin", password: "wrong" }));

  assert.equal(response.status, 401);
});

test("login without systemId seals the session when exactly one system authenticates", async () => {
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    const url = input instanceof Request ? input.url : String(input);

    if (url.startsWith("http://100.64.0.10:8080")) {
      return jsonResponse(401, { title: "Unauthorized" });
    }

    return jsonResponse(200, {
      token: "jwt-sys-002",
      expiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
      user: { id: 2, username: "admin", role: "Admin", systemId: "sys-002" },
    });
  }) as typeof fetch;

  const response = await handleLogin(loginRequest({ username: "admin", password: "secret" }));

  assert.equal(response.status, 200);
  assert.match(response.headers.get("set-cookie") ?? "", /bff_session=v1\./);
});

test("login without systemId only asks for one when 2+ passwords match", async () => {
  // Systems where the username exists but the password is wrong must not trigger
  // SYSTEM_ID_REQUIRED — they answer 401 like any unknown user.
  captureFetch(() => jsonResponse(401, { title: "Unauthorized" }));

  const noneMatch = await handleLogin(loginRequest({ username: "admin", password: "wrong" }));

  assert.equal(noneMatch.status, 401);
  assert.equal(
    ((await noneMatch.clone().json()) as { code?: string }).code,
    undefined,
    "a plain 401 must not carry SYSTEM_ID_REQUIRED",
  );

  captureFetch(() =>
    jsonResponse(200, {
      token: "jwt",
      expiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
      user: { id: 1, username: "admin", role: "Admin", systemId: "sys-001" },
    }),
  );

  const manyMatch = await handleLogin(loginRequest({ username: "admin", password: "shared" }));

  assert.equal(manyMatch.status, 400);
  assert.equal(((await manyMatch.json()) as { code?: string }).code, "SYSTEM_ID_REQUIRED");
});

test("login rejects invalid systemId and cross-origin attempts before touching a backend", async () => {
  const calls = captureFetch(() => jsonResponse(200, {}));

  const invalid = await handleLogin(
    loginRequest({ username: "admin", password: "x", systemId: "../etc/passwd" }),
  );

  assert.equal(invalid.status, 400);

  const crossOrigin = await handleLogin(
    new Request("https://dashboard.example.com/api/auth/login", {
      method: "POST",
      headers: {
        host: "dashboard.example.com",
        origin: "https://evil.example.net",
        "content-type": "application/json",
      },
      body: JSON.stringify({ username: "admin", password: "x" }),
    }),
  );

  assert.equal(crossOrigin.status, 403);
  assert.equal(calls.length, 0);
});

test("logout revokes the backend jti and always clears the cookie", async () => {
  const calls = captureFetch(() => jsonResponse(204, null));

  const response = await handleLogout(
    new Request("https://dashboard.example.com/api/auth/logout", {
      method: "POST",
      headers: {
        host: "dashboard.example.com",
        origin: "https://dashboard.example.com",
        cookie: sessionCookie("sys-001"),
      },
    }),
  );

  assert.equal(response.status, 200);
  assert.equal(calls[0].url, "http://100.64.0.10:8080/api/auth/logout");
  assert.equal(calls[0].method, "POST");
  assert.equal(calls[0].headers.get("authorization"), "Bearer jwt-for-sys-001");
  assert.match(response.headers.get("set-cookie") ?? "", /bff_session=;/);
});

test("logout clears the cookie even when the backend is unreachable", async () => {
  globalThis.fetch = (async () => {
    throw new TypeError("fetch failed");
  }) as typeof fetch;

  const response = await handleLogout(
    new Request("https://dashboard.example.com/api/auth/logout", {
      method: "POST",
      headers: {
        host: "dashboard.example.com",
        origin: "https://dashboard.example.com",
        cookie: sessionCookie("sys-001"),
      },
    }),
  );

  assert.equal(response.status, 200);
  assert.match(response.headers.get("set-cookie") ?? "", /bff_session=;/);
});

// --- (d) production refuses to start without a strong session secret ---------------------------

test("startup validation fails without a strong BFF_SESSION_SECRET", async () => {
  const { assertSessionSecretConfigured } = await import("./session.ts").catch(() => ({
    assertSessionSecretConfigured: undefined,
  }));

  if (!assertSessionSecretConfigured) {
    // `session.ts` pulls in `next/headers`; assert on the dependency-free core instead.
    const { getSessionSecret } = await import("./session-crypto.ts");

    delete process.env.BFF_SESSION_SECRET;
    assert.throws(() => getSessionSecret(), /BFF_SESSION_SECRET is not configured/);

    process.env.BFF_SESSION_SECRET = "too-short";
    assert.throws(() => getSessionSecret(), /at least 32 bytes/);

    process.env.BFF_SESSION_SECRET = SECRET;
    assert.equal(getSessionSecret(), SECRET);

    return;
  }

  delete process.env.BFF_SESSION_SECRET;
  assert.throws(() => assertSessionSecretConfigured(), /BFF_SESSION_SECRET is not configured/);
});

// --- (e) client-address forwarding is opt-in ---------------------------------------------------

test("client IP is only forwarded when a trusted proxy is configured", async () => {
  const { getClientIp } = await import("./edge-router.ts");

  delete process.env.TRUST_PROXY_HEADERS;

  const request = new Request("https://dashboard.example.com/api/auth/login", {
    method: "POST",
    headers: {
      host: "dashboard.example.com",
      "x-forwarded-for": "203.0.113.7, 10.0.0.1",
      "cf-connecting-ip": "203.0.113.9",
    },
  });

  // Default posture: a client-supplied header must not reach the limiter's partition key.
  assert.equal(getClientIp(request), null);

  process.env.TRUST_PROXY_HEADERS = "true";

  try {
    // Cloudflare's dedicated header wins when present.
    assert.equal(getClientIp(request), "203.0.113.9");

    const forwardedOnly = new Request("https://dashboard.example.com/api/auth/login", {
      method: "POST",
      headers: { host: "dashboard.example.com", "x-forwarded-for": "203.0.113.7, 10.0.0.1" },
    });

    assert.equal(getClientIp(forwardedOnly), "203.0.113.7");

    const noHeaders = new Request("https://dashboard.example.com/api/auth/login", {
      method: "POST",
      headers: { host: "dashboard.example.com" },
    });

    assert.equal(getClientIp(noHeaders), null);

    const calls = captureFetch(() => jsonResponse(200, { ok: true }));
    const headers = buildUpstreamHeaders(
      new Request("https://dashboard.example.com/api/system/status", {
        method: "GET",
        headers: { host: "dashboard.example.com", "x-forwarded-for": "203.0.113.7" },
      }),
      "jwt-for-sys-001",
    );

    assert.equal(headers.get("x-forwarded-for"), "203.0.113.7");
    assert.equal(calls.length, 0);
  } finally {
    delete process.env.TRUST_PROXY_HEADERS;
  }
});
