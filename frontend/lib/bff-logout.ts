import { readCookie } from "./cookies.ts";
import { isRequestOriginAllowed, resolveBackendUrl } from "./edge-router.ts";
import { isRouteAllowed } from "./route-allowlist.ts";
import { getSessionSecret, openSession, SESSION_COOKIE_NAME } from "./session-crypto.ts";
import { sessionClearCookieHeader } from "./session-cookie.ts";

/**
 * `POST /api/auth/logout` — revokes the backend `jti` and always clears the sealed cookie.
 *
 * Dependency-free (no `next/*`) so it is unit testable with `node --test`.
 */

const LOGOUT_TIMEOUT_MS = 8_000;

export async function handleLogout(request: Request): Promise<Response> {
  if (!isRouteAllowed("POST", ["auth", "logout"])) {
    return new Response(JSON.stringify({ title: "Not found", status: 404 }), {
      status: 404,
      headers: { "Content-Type": "application/problem+json" },
    });
  }

  const headers = new Headers({
    "Content-Type": "application/json",
    "Cache-Control": "no-store",
  });

  // Always clear the cookie, whatever the upstream says — a cross-origin logout must not leave a
  // usable session behind even though it is rejected with 403.
  headers.append("Set-Cookie", sessionClearCookieHeader(request));

  if (!isRequestOriginAllowed(request)) {
    return new Response(
      JSON.stringify({
        title: "Forbidden",
        status: 403,
        detail: "Cross-origin logout requests are not allowed.",
      }),
      { status: 403, headers },
    );
  }

  const sealed = readCookie(request.headers.get("cookie"), SESSION_COOKIE_NAME);
  const session = sealed ? openSession(sealed, getSessionSecret()) : null;

  if (session) {
    try {
      // Revoke the jti server-side. A failure here must not leave the browser authenticated, so
      // the cookie is cleared unconditionally below.
      await fetch(`${resolveBackendUrl(session.systemId)}/api/auth/logout`, {
        method: "POST",
        headers: { Accept: "application/json", Authorization: `Bearer ${session.edgeJwt}` },
        cache: "no-store",
        signal: AbortSignal.timeout(LOGOUT_TIMEOUT_MS),
      });
    } catch {
      // Backend unreachable, unmapped system, or token already expired: clear the session anyway.
    }
  }

  return new Response(JSON.stringify({ message: "Logged out." }), { status: 200, headers });
}
