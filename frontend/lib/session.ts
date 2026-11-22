import { cache } from "react";
import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import type { AuthUser } from "@/lib/types";

export const SESSION_COOKIE = "sc_session";

export function getBackendUrl(): string {
  const value = process.env.BACKEND_URL?.trim();

  if (!value) {
    throw new Error("BACKEND_URL is not configured for the dashboard BFF.");
  }

  return value.replace(/\/+$/, "");
}

export async function getSessionToken(): Promise<string | null> {
  const store = await cookies();

  return store.get(SESSION_COOKIE)?.value ?? null;
}

function secureCookiesEnabled(): boolean {
  const configured = process.env.COOKIE_SECURE;

  if (configured !== undefined) {
    return configured.toLowerCase() === "true";
  }

  return process.env.NODE_ENV === "production";
}

export function sessionCookieOptions(expiresAtUtc: string) {
  const expires = new Date(expiresAtUtc);
  const maxAge = Number.isNaN(expires.getTime())
    ? undefined
    : Math.max(0, Math.floor((expires.getTime() - Date.now()) / 1000));

  return {
    httpOnly: true,
    sameSite: "lax" as const,
    secure: secureCookiesEnabled(),
    path: "/",
    expires: Number.isNaN(expires.getTime()) ? undefined : expires,
    maxAge,
  };
}

export const clearedSessionCookieOptions = {
  httpOnly: true,
  sameSite: "lax" as const,
  secure: secureCookiesEnabled(),
  path: "/",
  expires: new Date(0),
  maxAge: 0,
};

export const getProfile = cache(async (): Promise<AuthUser | null> => {
  const token = await getSessionToken();

  if (!token) {
    return null;
  }

  let response: Response;

  try {
    response = await fetch(`${getBackendUrl()}/api/auth/me`, {
      headers: {
        Accept: "application/json",
        Authorization: `Bearer ${token}`,
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

/**
 * Cookie auth is SameSite=Lax, but state-changing BFF calls also reject explicit cross-site
 * origins as defence in depth.
 */
export function isSameOrigin(request: Request): boolean {
  const origin = request.headers.get("origin");

  if (!origin) {
    return true;
  }

  // `request.url` uses the standalone server's bind host (0.0.0.0 in Docker), not the
  // browser-facing host. The Host header is the origin the browser actually requested.
  const requestHost = request.headers.get("host") ?? new URL(request.url).host;

  try {
    return new URL(origin).host === requestHost;
  } catch {
    return false;
  }
}
