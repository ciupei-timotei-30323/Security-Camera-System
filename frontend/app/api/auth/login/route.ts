import { NextResponse } from "next/server";
import {
  getBackendUrl,
  isSameOrigin,
  SESSION_COOKIE,
  sessionCookieOptions,
} from "@/lib/session";
import type { LoginResult } from "@/lib/types";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

export async function POST(request: Request) {
  if (!isSameOrigin(request)) {
    return NextResponse.json(
      { title: "Forbidden", detail: "Cross-origin login requests are not allowed." },
      { status: 403 },
    );
  }

  let credentials: unknown;

  try {
    credentials = await request.json();
  } catch {
    return NextResponse.json(
      { title: "Invalid request", detail: "The login body must be valid JSON." },
      { status: 400 },
    );
  }

  let upstream: Response;

  try {
    upstream = await fetch(`${getBackendUrl()}/api/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify(credentials),
      cache: "no-store",
      signal: AbortSignal.timeout(15000),
    });
  } catch {
    return NextResponse.json(
      {
        title: "Cannot reach the camera backend",
        detail: "The backend is unavailable. Check BACKEND_URL and try again.",
      },
      { status: 502 },
    );
  }

  const text = await upstream.text();

  if (!upstream.ok) {
    return new NextResponse(text || null, {
      status: upstream.status,
      headers: {
        "Content-Type": upstream.headers.get("content-type") ?? "application/json",
        "Cache-Control": "no-store",
      },
    });
  }

  let login: { token?: string; expiresAtUtc?: string; user?: LoginResult["user"] };

  try {
    login = JSON.parse(text) as typeof login;
  } catch {
    return NextResponse.json(
      { title: "Unexpected backend response", detail: "The login response was not valid JSON." },
      { status: 502 },
    );
  }

  if (!login.token || !login.expiresAtUtc || !login.user) {
    return NextResponse.json(
      { title: "Unexpected backend response", detail: "The login response was incomplete." },
      { status: 502 },
    );
  }

  const response = NextResponse.json({
    user: login.user,
    expiresAtUtc: login.expiresAtUtc,
  } satisfies LoginResult);

  response.cookies.set(
    SESSION_COOKIE,
    login.token,
    sessionCookieOptions(login.expiresAtUtc),
  );
  response.headers.set("Cache-Control", "no-store");

  return response;
}
