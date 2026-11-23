import { cache } from "react";
import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import type { AuthUser } from "@/lib/types";
import {
  getSessionSecret,
  openSession,
  sealSession,
  SESSION_COOKIE_NAME,
  type BffSession,
} from "@/lib/session-crypto";
import {
  getDefaultBackendUrl,
  getEdgeBackends,
  isRequestOriginAllowed,
  resolveBackendUrl,
} from "@/lib/edge-router";

export { sealSession, openSession, getSessionSecret, SESSION_COOKIE_NAME };
export {
  clearedSessionCookieOptions,
  secureCookiesEnabled,
  sessionClearCookieHeader,
  sessionCookieOptions,
  sessionSetCookieHeader,
} from "@/lib/session-cookie";

/** Name of the sealed BFF session cookie. */
export const SESSION_COOKIE = SESSION_COOKIE_NAME;

export type { BffSession };

/**
 * Fail-fast startup check. Called from `instrumentation.ts` so a production deployment without a
 * strong `BFF_SESSION_SECRET` refuses to serve traffic instead of silently issuing weak cookies.
 */
export function assertSessionSecretConfigured(): void {
  getSessionSecret();
}

/**
 * Reads and unseals the session cookie. Returns `null` for a missing, expired, tampered or
 * undecryptable cookie — callers treat that as "not signed in".
 */
export async function getSession(): Promise<BffSession | null> {
  let token: string | undefined;

  try {
    const store = await cookies();
    token = store.get(SESSION_COOKIE)?.value;
  } catch {
    return null;
  }

  if (!token) {
    return null;
  }

  try {
    return openSession(token, getSessionSecret());
  } catch {
    // Missing/weak secret must not crash every page render; the proxy answers 401 and the
    // operator sees the startup validation error in the logs.
    return null;
  }
}

/** Convenience accessor for the upstream bearer token of the current session. */
export async function getSessionToken(): Promise<string | null> {
  const session = await getSession();

  return session?.edgeJwt ?? null;
}

/** Convenience accessor for the verified `systemId` of the current session. */
export async function getSessionSystemId(): Promise<string | null> {
  const session = await getSession();

  return session?.systemId ?? null;
}

function backendUrlForProfile(session: BffSession): string | null {
  try {
    return resolveBackendUrl(session.systemId);
  } catch {
    return null;
  }
}

/**
 * Loads the signed-in account from *its own* camera system's backend. The BFF never validates the
 * backend JWT signature; it only forwards it and trusts the sealed `systemId` for routing.
 */
export const getProfile = cache(async (): Promise<AuthUser | null> => {
  const session = await getSession();

  if (!session) {
    return null;
  }

  const backendUrl = backendUrlForProfile(session);

  if (!backendUrl) {
    throw new Error(
      `No private backend is configured for camera system '${session.systemId}'. ` +
        "Check EDGE_BACKENDS_JSON / BACKEND_URL.",
    );
  }

  let response: Response;

  try {
    response = await fetch(`${backendUrl}/api/auth/me`, {
      headers: {
        Accept: "application/json",
        Authorization: `Bearer ${session.edgeJwt}`,
      },
      cache: "no-store",
      signal: AbortSignal.timeout(8000),
    });
  } catch {
    throw new Error("The camera backend is unreachable. Check BACKEND_URL and try again.");
  }

  if (response.status === 401) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`Could not load the signed-in account (HTTP ${response.status}).`);
  }

  return (await response.json()) as AuthUser;
});

export async function requireProfile(): Promise<AuthUser> {
  const profile = await getProfile();

  if (!profile) {
    redirect("/login");
  }

  return profile;
}

export async function requireAdmin(): Promise<AuthUser> {
  const profile = await requireProfile();

  if (profile.role !== "Admin") {
    redirect("/dashboard");
  }

  return profile;
}

/** True when at least one private backend target is configured. */
export function hasBackendConfigured(): boolean {
  return getDefaultBackendUrl() !== null || Object.keys(getEdgeBackends()).length > 0;
}

/**
 * CSRF defence in depth: cookie auth is `SameSite=Lax`, and every state-changing BFF request must
 * additionally carry an `Origin` matching the request host (or `ALLOWED_ORIGINS`).
 */
export function isSameOrigin(request: Request): boolean {
  return isRequestOriginAllowed(request);
}
