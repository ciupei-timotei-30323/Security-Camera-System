import { NextResponse } from "next/server";
import {
  clearedSessionCookieOptions,
  getBackendUrl,
  getSessionToken,
  isSameOrigin,
  SESSION_COOKIE,
} from "@/lib/session";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

export async function POST(request: Request) {
  if (!isSameOrigin(request)) {
    return NextResponse.json(
      { title: "Forbidden", detail: "Cross-origin logout requests are not allowed." },
      { status: 403 },
    );
  }

  const token = await getSessionToken();

  if (token) {
    try {
      // Revoke the jti server-side. A failure here must not leave the browser authenticated,
      // so the cookie is always cleared below.
      await fetch(`${getBackendUrl()}/api/auth/logout`, {
        method: "POST",
        headers: { Accept: "application/json", Authorization: `Bearer ${token}` },
        cache: "no-store",
        signal: AbortSignal.timeout(8000),
      });
    } catch {
      // Backend unreachable or token already expired: clear the local session anyway.
    }
  }

  const response = NextResponse.json({ message: "Logged out." });
  response.cookies.set(SESSION_COOKIE, "", clearedSessionCookieOptions);
  response.headers.set("Cache-Control", "no-store");

  return response;
}
