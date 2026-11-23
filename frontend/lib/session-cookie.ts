import { serializeCookie, type SessionCookieOptions } from "./cookies.ts";
import { SESSION_COOKIE_NAME } from "./session-crypto.ts";

/**
 * Cookie flag policy for the sealed BFF session.
 *
 * Dependency-free (no `next/*`) so the route cores can be unit tested with `node --test`.
 */

function forwardedProto(request?: Request): string | null {
  return request?.headers.get("x-forwarded-proto")?.split(",")[0]?.trim().toLowerCase() ?? null;
}

/**
 * `Secure` is enabled automatically in production or whenever the browser-facing connection was
 * HTTPS (a Cloudflare Quick Tunnel sets `x-forwarded-proto: https`). `COOKIE_SECURE` remains an
 * explicit escape hatch for plain-HTTP LAN deployments.
 */
export function secureCookiesEnabled(request?: Request): boolean {
  const configured = process.env.COOKIE_SECURE;

  if (configured !== undefined && configured.trim() !== "") {
    return configured.trim().toLowerCase() === "true";
  }

  return process.env.NODE_ENV === "production" || forwardedProto(request) === "https";
}

export function sessionCookieOptions(
  expiresAtUtc?: string,
  request?: Request,
): SessionCookieOptions {
  const expires = expiresAtUtc ? new Date(expiresAtUtc) : undefined;
  const validExpires = expires && !Number.isNaN(expires.getTime()) ? expires : undefined;

  return {
    httpOnly: true,
    sameSite: "lax",
    secure: secureCookiesEnabled(request),
    path: "/",
    expires: validExpires,
    maxAge: validExpires
      ? Math.max(0, Math.floor((validExpires.getTime() - Date.now()) / 1000))
      : undefined,
  };
}

export function clearedSessionCookieOptions(request?: Request): SessionCookieOptions {
  return {
    httpOnly: true,
    sameSite: "lax",
    secure: secureCookiesEnabled(request),
    path: "/",
    expires: new Date(0),
    maxAge: 0,
  };
}

export function sessionSetCookieHeader(
  value: string,
  request?: Request,
  expiresAtUtc?: string,
): string {
  return serializeCookie(
    SESSION_COOKIE_NAME,
    value,
    sessionCookieOptions(expiresAtUtc, request),
  );
}

export function sessionClearCookieHeader(request?: Request): string {
  return serializeCookie(SESSION_COOKIE_NAME, "", clearedSessionCookieOptions(request));
}
