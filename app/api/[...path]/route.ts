import { NextResponse } from "next/server";
import {
  clearedSessionCookieOptions,
  getBackendUrl,
  getSessionToken,
  SESSION_COOKIE,
} from "@/lib/session";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

const REQUEST_HEADERS = [
  "accept",
  "accept-language",
  "content-type",
  "range",
  "if-none-match",
  "if-modified-since",
];

const RESPONSE_HEADERS = [
  "content-type",
  "cache-control",
  "content-disposition",
  "etag",
  "last-modified",
  "accept-ranges",
  "content-range",
  "pragma",
  "expires",
];

type RouteContext = { params: Promise<{ path: string[] }> };

type StreamingRequestInit = RequestInit & { duplex?: "half" };

function loginResponse(request: Request, tokenPresent: boolean): Response {
  const accept = request.headers.get("accept") ?? "";
  const destination = request.headers.get("sec-fetch-dest");
  const isNavigationOrImage =
    accept.includes("text/html") || destination === "image" || destination === "document";

  if (isNavigationOrImage) {
    const response = NextResponse.redirect(
      new URL("/login?reason=expired", request.url),
      307,
    );
    response.cookies.set(SESSION_COOKIE, "", clearedSessionCookieOptions);
    return response;
  }

  const response = NextResponse.json(
    {
      title: "Unauthorized",
      status: 401,
      detail: tokenPresent
        ? "Your session has expired. Please sign in again."
        : "Please sign in to continue.",
    },
    {
      status: 401,
      headers: {
        Location: "/login?reason=expired",
        "Cache-Control": "no-store",
      },
    },
  );
  response.cookies.set(SESSION_COOKIE, "", clearedSessionCookieOptions);
  return response;
}

async function proxy(request: Request, context: RouteContext): Promise<Response> {
  const { path } = await context.params;

  if (!path?.length || path.some((segment) => segment === ".." || segment === ".")) {
    return NextResponse.json({ title: "Not found" }, { status: 404 });
  }

  const token = await getSessionToken();

  if (!token) {
    return loginResponse(request, false);
  }

  let target: URL;

  try {
    target = new URL(
      `${getBackendUrl()}/api/${path.map((segment) => encodeURIComponent(segment)).join("/")}`,
    );
  } catch {
    return NextResponse.json(
      {
        title: "Dashboard misconfigured",
        detail: "BACKEND_URL is not set for the dashboard BFF.",
      },
      { status: 500 },
    );
  }

  target.search = new URL(request.url).search;

  const headers = new Headers();

  for (const name of REQUEST_HEADERS) {
    const value = request.headers.get(name);

    if (value) {
      headers.set(name, value);
    }
  }

  headers.set("Authorization", `Bearer ${token}`);

  const method = request.method.toUpperCase();
  const hasBody = method !== "GET" && method !== "HEAD";

  const init: StreamingRequestInit = {
    method,
    headers,
    cache: "no-store",
    redirect: "manual",
    signal: request.signal,
  };

  if (hasBody && request.body) {
    init.body = request.body;
    init.duplex = "half";
  }

  let upstream: Response;

  try {
    upstream = await fetch(target, init);
  } catch {
    if (request.signal.aborted) {
      return new Response(null, { status: 499 });
    }

    return NextResponse.json(
      {
        title: "Cannot reach the camera backend",
        detail: "The backend is unavailable. Check BACKEND_URL and try again.",
      },
      { status: 502 },
    );
  }

  if (upstream.status === 401) {
    return loginResponse(request, true);
  }

  const responseHeaders = new Headers();

  for (const name of RESPONSE_HEADERS) {
    const value = upstream.headers.get(name);

    if (value) {
      responseHeaders.set(name, value);
    }
  }

  const contentType = responseHeaders.get("content-type") ?? "";

  if (contentType.includes("multipart/x-mixed-replace")) {
    // Never let an intermediary buffer the MJPEG stream.
    responseHeaders.set("Cache-Control", "no-store, no-transform");
    responseHeaders.set("X-Accel-Buffering", "no");
  }

  // Content-Encoding/Content-Length are intentionally not forwarded: undici decompresses the
  // body, and the response is streamed chunk-by-chunk.
  return new Response(upstream.body, {
    status: upstream.status,
    statusText: upstream.statusText,
    headers: responseHeaders,
  });
}

export const GET = proxy;
export const POST = proxy;
export const PUT = proxy;
export const PATCH = proxy;
export const DELETE = proxy;
export const HEAD = proxy;
export const OPTIONS = proxy;
