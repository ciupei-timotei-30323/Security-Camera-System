import { readCookie } from "./cookies.ts";
import {
  buildUpstreamHeaders,
  resolveBackendUrl,
  BackendNotConfiguredError,
  isRequestOriginAllowed,
  UnknownSystemError,
} from "./edge-router.ts";
import { isRouteAllowed, isStreamingRoute } from "./route-allowlist.ts";
import { getSessionSecret, openSession, SESSION_COOKIE_NAME } from "./session-crypto.ts";
import { sessionClearCookieHeader } from "./session-cookie.ts";

/**
 * The BFF proxy core.
 *
 * Written against the standard `Request`/`Response` API (not `next/headers`) so it can be
 * exercised by unit tests without booting Next.js. `app/api/[...path]/route.ts` is a thin adapter.
 */

/** Response headers passed through to the browser; everything else is dropped. */
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

/** Time allowed for a JSON endpoint to answer (connect + response). */
const JSON_TIMEOUT_MS = 20_000;

/** Time allowed for a streaming endpoint to *start* answering; the body itself is unbounded. */
const STREAM_CONNECT_TIMEOUT_MS = 15_000;

type StreamingRequestInit = RequestInit & { duplex?: "half" };

function jsonResponse(
  body: Record<string, unknown>,
  status: number,
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "Content-Type": "application/problem+json",
      "Cache-Control": "no-store",
      ...headers,
    },
  });
}

function notFoundResponse(): Response {
  return jsonResponse({ title: "Not found", status: 404 }, 404);
}

function forbiddenResponse(detail: string): Response {
  return jsonResponse({ title: "Forbidden", status: 403, detail }, 403);
}

/**
 * Unauthorized response: clears the sealed cookie and, for navigations/image loads, redirects to
 * the login page instead of surfacing raw JSON in the address bar.
 */
export function loginResponse(request: Request, sessionPresent: boolean): Response {
  const accept = request.headers.get("accept") ?? "";
  const destination = request.headers.get("sec-fetch-dest");
  const isNavigationOrImage =
    accept.includes("text/html") || destination === "image" || destination === "document";

  const headers = new Headers({ "Cache-Control": "no-store" });
  headers.append("Set-Cookie", sessionClearCookieHeader(request));

  if (isNavigationOrImage) {
    headers.set("Location", "/login?reason=expired");
    return new Response(null, { status: 307, headers });
  }

  headers.set("Content-Type", "application/problem+json");
  headers.set("Location", "/login?reason=expired");

  return new Response(
    JSON.stringify({
      title: "Unauthorized",
      status: 401,
      detail: sessionPresent
        ? "Your session has expired. Please sign in again."
        : "Please sign in to continue.",
    }),
    { status: 401, headers },
  );
}

/**
 * Aborts after `ms`. `clear()` only cancels the timer (and must never abort, otherwise an
 * already-received streaming body would be torn down).
 */
function createDeadline(ms: number): {
  signal: AbortSignal;
  clear: () => void;
  aborted: () => boolean;
} {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), ms);

  if (typeof timer === "object" && "unref" in timer && typeof timer.unref === "function") {
    timer.unref();
  }

  return {
    signal: controller.signal,
    clear: () => clearTimeout(timer),
    aborted: () => controller.signal.aborted,
  };
}

export async function handleProxy(request: Request, path: string[]): Promise<Response> {
  if (!isRouteAllowed(request.method, path)) {
    // Unknown/undersired routes (including the whole device + ingestion surface) are invisible.
    return notFoundResponse();
  }

  const method = request.method.toUpperCase();
  const isStreaming = isStreamingRoute(path);

  // CSRF: state-changing requests must come from this origin (or ALLOWED_ORIGINS).
  if (!isRequestOriginAllowed(request)) {
    return forbiddenResponse("Cross-origin requests are not allowed.");
  }

  const cookieHeader = request.headers.get("cookie");
  const sealed = readCookie(cookieHeader, SESSION_COOKIE_NAME);
  const session = sealed ? openSession(sealed, getSessionSecret()) : null;

  if (!session) {
    return loginResponse(request, Boolean(sealed));
  }

  let backendUrl: string;

  try {
    backendUrl = resolveBackendUrl(session.systemId);
  } catch (error) {
    if (error instanceof UnknownSystemError) {
      // A sealed systemId that no longer maps to a private backend must never fall back to a
      // different system's upstream.
      return forbiddenResponse("This camera system is not available on this dashboard.");
    }

    if (error instanceof BackendNotConfiguredError) {
      return jsonResponse(
        {
          title: "Dashboard misconfigured",
          status: 500,
          detail: "No private backend is configured. Set EDGE_BACKENDS_JSON or BACKEND_URL.",
        },
        500,
      );
    }

    throw error;
  }

  const encodedPath = path.map((segment) => encodeURIComponent(segment)).join("/");
  let target: URL;

  try {
    target = new URL(`${backendUrl}/api/${encodedPath}`);
  } catch {
    return jsonResponse({ title: "Dashboard misconfigured", status: 500 }, 500);
  }

  target.search = new URL(request.url).search;

  const hasBody = method !== "GET" && method !== "HEAD";
  const deadline = createDeadline(isStreaming ? STREAM_CONNECT_TIMEOUT_MS : JSON_TIMEOUT_MS);

  // Both the deadline and the browser disconnect abort the upstream call.
  const signal = AbortSignal.any([request.signal, deadline.signal]);

  const init: StreamingRequestInit = {
    method,
    headers: buildUpstreamHeaders(request, session.edgeJwt),
    cache: "no-store",
    redirect: "manual",
    signal,
  };

  if (hasBody && request.body) {
    init.body = request.body;
    init.duplex = "half";
  }

  let upstream: Response;

  try {
    upstream = await fetch(target, init);
  } catch {
    deadline.clear();

    if (request.signal.aborted) {
      return new Response(null, { status: 499 });
    }

    if (deadline.aborted()) {
      return jsonResponse(
        {
          title: "Camera backend timed out",
          status: 504,
          detail: "The backend did not respond in time.",
        },
        504,
      );
    }

    return jsonResponse(
      {
        title: "Cannot reach the camera backend",
        status: 502,
        detail: "The backend is unavailable. Check EDGE_BACKENDS_JSON / BACKEND_URL and try again.",
      },
      502,
    );
  }

  // Headers arrived: a streaming body must not be cut off by the connect deadline.
  deadline.clear();

  if (upstream.status === 401) {
    await upstream.body?.cancel().catch(() => undefined);
    return loginResponse(request, true);
  }

  // The incoming request signal is passed to the upstream fetch above. Also cancel the upstream
  // body explicitly when the browser disconnects so streamed MJPEG connections release the
  // backend's concurrent-stream slot immediately.
  if (request.signal.aborted) {
    await upstream.body?.cancel().catch(() => undefined);
    return new Response(null, { status: 499 });
  }

  request.signal.addEventListener(
    "abort",
    () => {
      void upstream.body?.cancel().catch(() => undefined);
    },
    { once: true },
  );

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
