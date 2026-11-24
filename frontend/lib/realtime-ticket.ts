import { readCookie } from "./cookies.ts";
import { isValidSystemId } from "./edge-router.ts";
import { getSessionSecret, openSession, SESSION_COOKIE_NAME } from "./session-crypto.ts";
import type { BffSession } from "./session-crypto.ts";

/**
 * Short-lived realtime credentials handed to the browser.
 *
 * Everything here is derived from the sealed `bff_session` cookie — never from a request query
 * string or header — so a caller cannot ask for another tenant's stream ticket. The `edgeJwt`
 * inside the cookie is already tenant-scoped and is what MediaMTX's `read` auth check validates.
 *
 * Written against the standard `Request`/`Response` API so it can be unit tested with
 * `node --test`; `app/api/realtime/ticket/route.ts` is the thin Next.js adapter.
 */

export interface RealtimeTicket {
  /** Tenant the ticket belongs to. Doubles as the MediaMTX path. */
  systemId: string;
  /** Absolute WHEP endpoint, e.g. `https://cam.example.com/sys-001/whep`. */
  webrtcWhepUrl: string;
  /** Backend-issued JWT; sent as `Authorization: Bearer` to the WHEP endpoint. */
  streamToken: string;
  /** MQTT-over-WebSockets broker URL, or `""` when live updates are not configured. */
  mqttWsUrl: string;
  mqttUsername: string;
  mqttPassword: string;
}

export class RealtimeNotConfiguredError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "RealtimeNotConfiguredError";
  }
}

/**
 * Environment bag accepted by the helpers below. Deliberately narrower than `NodeJS.ProcessEnv`:
 * Next.js augments that type with a required `NODE_ENV`, which makes plain object literals in tests
 * fail to typecheck.
 */
export type RealtimeEnv = Readonly<Record<string, string | undefined>>;

function trimTrailingSlashes(value: string): string {
  return value.trim().replace(/\/+$/, "");
}

/**
 * Public MediaMTX origin. `MEDIAMTX_PUBLIC_URL` is the server-side name; `NEXT_PUBLIC_MEDIAMTX_URL`
 * is accepted as a fallback so a single variable can serve both build-time and runtime consumers.
 */
export function getMediaMtxPublicUrl(env: RealtimeEnv = process.env): string | null {
  const raw = trimTrailingSlashes(env.MEDIAMTX_PUBLIC_URL ?? env.NEXT_PUBLIC_MEDIAMTX_URL ?? "");

  return raw.length > 0 ? raw : null;
}

export function getMqttWsUrl(env: RealtimeEnv = process.env): string | null {
  const raw = trimTrailingSlashes(env.MQTT_PUBLIC_WS_URL ?? "");

  return raw.length > 0 ? raw : null;
}

/**
 * Builds the WHEP URL for a tenant. MediaMTX maps each publisher's path to the same name, so the
 * path segment must be the *verified* `systemId` and nothing else.
 */
export function buildWhepUrl(publicUrl: string, systemId: string): string {
  if (!isValidSystemId(systemId)) {
    throw new RealtimeNotConfiguredError(`Invalid camera system id '${systemId}'.`);
  }

  return `${trimTrailingSlashes(publicUrl)}/${systemId}/whep`;
}

/**
 * Assembles the ticket for a verified session. Throws {@link RealtimeNotConfiguredError} when the
 * public MediaMTX URL is missing — the dashboard then falls back to the MJPEG stream instead of
 * showing a broken WebRTC player.
 */
export function buildRealtimeTicket(
  session: BffSession,
  env: RealtimeEnv = process.env,
): RealtimeTicket {
  const mediaMtxUrl = getMediaMtxPublicUrl(env);

  if (!mediaMtxUrl) {
    throw new RealtimeNotConfiguredError(
      "WebRTC is not configured. Set MEDIAMTX_PUBLIC_URL (or NEXT_PUBLIC_MEDIAMTX_URL).",
    );
  }

  const mqttWsUrl = getMqttWsUrl(env) ?? "";

  return {
    systemId: session.systemId,
    webrtcWhepUrl: buildWhepUrl(mediaMtxUrl, session.systemId),
    streamToken: session.edgeJwt,
    mqttWsUrl,
    // Empty when live updates are not configured; the dashboard then keeps its polling queries.
    mqttUsername: mqttWsUrl ? (env.MQTT_PUBLIC_USER ?? env.MQTT_CLIENT_USER ?? "dashboard").trim() : "",
    mqttPassword: mqttWsUrl ? (env.MQTT_PUBLIC_PASS ?? env.MQTT_CLIENT_PASS ?? "").trim() : "",
  };
}

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

/**
 * `GET /api/realtime/ticket` — resolves the sealed cookie and returns the tenant's realtime
 * credentials. Served by the BFF itself (not proxied), so the backend never sees this route.
 */
export function handleRealtimeTicket(request: Request): Response {
  const cookieHeader = request.headers.get("cookie");
  const sealed = readCookie(cookieHeader, SESSION_COOKIE_NAME);

  let session: BffSession | null = null;

  if (sealed) {
    try {
      session = openSession(sealed, getSessionSecret());
    } catch {
      session = null;
    }
  }

  if (!session) {
    return jsonResponse(
      {
        title: "Unauthorized",
        status: 401,
        detail: "Please sign in to continue.",
      },
      401,
    );
  }

  let ticket: RealtimeTicket;

  try {
    ticket = buildRealtimeTicket(session);
  } catch (error) {
    if (error instanceof RealtimeNotConfiguredError) {
      return jsonResponse(
        {
          title: "Realtime unavailable",
          status: 503,
          detail: error.message,
        },
        503,
      );
    }

    throw error;
  }

  return new Response(JSON.stringify(ticket), {
    status: 200,
    headers: {
      "Content-Type": "application/json",
      // Contains a bearer token and broker credentials: never cache, never store.
      "Cache-Control": "no-store",
    },
  });
}
