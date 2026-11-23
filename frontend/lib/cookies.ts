/**
 * Minimal, dependency-free `Set-Cookie` serialization.
 *
 * Keeping this out of `next/headers` lets the BFF route cores be unit tested with plain
 * `node --test` and keeps cookie flags in exactly one place.
 */

export interface SessionCookieOptions {
  httpOnly: boolean;
  sameSite: "lax" | "strict" | "none";
  secure: boolean;
  path: string;
  maxAge?: number;
  expires?: Date;
}

const SAME_SITE = { lax: "Lax", strict: "Strict", none: "None" } as const;

export function serializeCookie(
  name: string,
  value: string,
  options: SessionCookieOptions,
): string {
  const parts = [`${name}=${value}`];

  parts.push(`Path=${options.path}`);
  parts.push(`SameSite=${SAME_SITE[options.sameSite]}`);

  if (options.httpOnly) {
    parts.push("HttpOnly");
  }

  if (options.secure) {
    parts.push("Secure");
  }

  if (typeof options.maxAge === "number" && Number.isFinite(options.maxAge)) {
    parts.push(`Max-Age=${Math.max(0, Math.floor(options.maxAge))}`);
  }

  if (options.expires && !Number.isNaN(options.expires.getTime())) {
    parts.push(`Expires=${options.expires.toUTCString()}`);
  }

  return parts.join("; ");
}

/**
 * Reads a single cookie value from a raw `Cookie` header. Returns `null` when absent. Values are
 * URI-decoded defensively; sealed tokens are base64url, so decoding is a no-op in practice.
 */
export function readCookie(cookieHeader: string | null | undefined, name: string): string | null {
  if (!cookieHeader) {
    return null;
  }

  for (const part of cookieHeader.split(";")) {
    const separator = part.indexOf("=");

    if (separator === -1) {
      continue;
    }

    if (part.slice(0, separator).trim() !== name) {
      continue;
    }

    const rawValue = part.slice(separator + 1).trim();

    if (!rawValue) {
      return null;
    }

    try {
      return decodeURIComponent(rawValue);
    } catch {
      return rawValue;
    }
  }

  return null;
}
