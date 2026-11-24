import assert from "node:assert/strict";
import test, { afterEach, beforeEach } from "node:test";
import {
  buildRealtimeTicket,
  buildWhepUrl,
  getMediaMtxPublicUrl,
  getMqttWsUrl,
  handleRealtimeTicket,
  RealtimeNotConfiguredError,
} from "./realtime-ticket.ts";
import { sealSession, SESSION_COOKIE_NAME } from "./session-crypto.ts";

const SECRET = "test-bff-session-secret-at-least-32-bytes-long";

function sessionCookie(systemId: string, ttlSeconds = 600): string {
  const exp = Math.floor(Date.now() / 1000) + ttlSeconds;
  const sealed = sealSession({ systemId, edgeJwt: `jwt-for-${systemId}`, exp }, SECRET);

  return `${SESSION_COOKIE_NAME}=${encodeURIComponent(sealed)}`;
}

function ticketRequest(cookie?: string): Request {
  return new Request("https://dashboard.example.com/api/realtime/ticket", {
    method: "GET",
    headers: cookie ? { cookie } : {},
  });
}

beforeEach(() => {
  process.env.BFF_SESSION_SECRET = SECRET;
  process.env.MEDIAMTX_PUBLIC_URL = "https://cam.example.com";
  process.env.MQTT_PUBLIC_WS_URL = "wss://cam.example.com/mqtt";
  process.env.MQTT_PUBLIC_USER = "dashboard";
  process.env.MQTT_PUBLIC_PASS = "dashboard-pass";
  delete process.env.NEXT_PUBLIC_MEDIAMTX_URL;
  delete process.env.MQTT_CLIENT_USER;
  delete process.env.MQTT_CLIENT_PASS;
});

afterEach(() => {
  delete process.env.MEDIAMTX_PUBLIC_URL;
  delete process.env.MQTT_PUBLIC_WS_URL;
  delete process.env.MQTT_PUBLIC_USER;
  delete process.env.MQTT_PUBLIC_PASS;
  delete process.env.NEXT_PUBLIC_MEDIAMTX_URL;
  delete process.env.BFF_SESSION_SECRET;
});

test("getMediaMtxPublicUrl prefers MEDIAMTX_PUBLIC_URL and falls back to NEXT_PUBLIC_", () => {
  assert.equal(getMediaMtxPublicUrl({ MEDIAMTX_PUBLIC_URL: "https://cam.example.com/" }), "https://cam.example.com");
  assert.equal(
    getMediaMtxPublicUrl({ NEXT_PUBLIC_MEDIAMTX_URL: "https://public.example.com" }),
    "https://public.example.com",
  );
  assert.equal(getMediaMtxPublicUrl({}), null);
  assert.equal(getMediaMtxPublicUrl({ MEDIAMTX_PUBLIC_URL: "   " }), null);
});

test("getMqttWsUrl returns null when the broker is not configured", () => {
  assert.equal(getMqttWsUrl({ MQTT_PUBLIC_WS_URL: "wss://cam.example.com/mqtt/" }), "wss://cam.example.com/mqtt");
  assert.equal(getMqttWsUrl({}), null);
});

test("buildWhepUrl appends the tenant path and rejects invalid system ids", () => {
  assert.equal(buildWhepUrl("https://cam.example.com/", "sys-001"), "https://cam.example.com/sys-001/whep");
  assert.throws(() => buildWhepUrl("https://cam.example.com", "sys 001"), RealtimeNotConfiguredError);
  assert.throws(() => buildWhepUrl("https://cam.example.com", "../sys-002"), RealtimeNotConfiguredError);
});

test("buildRealtimeTicket derives everything from the session", () => {
  const ticket = buildRealtimeTicket(
    { systemId: "sys-001", edgeJwt: "edge-jwt", exp: Math.floor(Date.now() / 1000) + 600 },
    {
      MEDIAMTX_PUBLIC_URL: "https://cam.example.com",
      MQTT_PUBLIC_WS_URL: "wss://cam.example.com/mqtt",
      MQTT_PUBLIC_USER: "dashboard",
      MQTT_PUBLIC_PASS: "dashboard-pass",
    },
  );

  assert.deepEqual(ticket, {
    systemId: "sys-001",
    webrtcWhepUrl: "https://cam.example.com/sys-001/whep",
    streamToken: "edge-jwt",
    mqttWsUrl: "wss://cam.example.com/mqtt",
    mqttUsername: "dashboard",
    mqttPassword: "dashboard-pass",
  });
});

test("buildRealtimeTicket leaves MQTT credentials empty when no broker is configured", () => {
  const ticket = buildRealtimeTicket(
    { systemId: "sys-001", edgeJwt: "edge-jwt", exp: Math.floor(Date.now() / 1000) + 600 },
    { MEDIAMTX_PUBLIC_URL: "https://cam.example.com" },
  );

  assert.equal(ticket.mqttWsUrl, "");
  assert.equal(ticket.mqttUsername, "");
  assert.equal(ticket.mqttPassword, "");
});

test("buildRealtimeTicket throws when MediaMTX is not configured", () => {
  assert.throws(
    () =>
      buildRealtimeTicket({
        systemId: "sys-001",
        edgeJwt: "edge-jwt",
        exp: Math.floor(Date.now() / 1000) + 600,
      }, {}),
    (error: unknown) => {
      assert.ok(error instanceof RealtimeNotConfiguredError);
      assert.match(error.message, /MEDIAMTX_PUBLIC_URL/);
      return true;
    },
  );
});

test("GET /api/realtime/ticket returns the tenant ticket for a valid session", async () => {
  const response = handleRealtimeTicket(ticketRequest(sessionCookie("sys-001")));

  assert.equal(response.status, 200);
  assert.equal(response.headers.get("cache-control"), "no-store");
  assert.equal(response.headers.get("content-type"), "application/json");

  const body = (await response.json()) as Record<string, unknown>;
  assert.equal(body.systemId, "sys-001");
  assert.equal(body.webrtcWhepUrl, "https://cam.example.com/sys-001/whep");
  assert.equal(body.streamToken, "jwt-for-sys-001");
  assert.equal(body.mqttWsUrl, "wss://cam.example.com/mqtt");
  assert.equal(body.mqttUsername, "dashboard");
  assert.equal(body.mqttPassword, "dashboard-pass");
});

test("GET /api/realtime/ticket never leaks another tenant's stream", async () => {
  const sys1 = handleRealtimeTicket(ticketRequest(sessionCookie("sys-001")));
  const sys2 = handleRealtimeTicket(ticketRequest(sessionCookie("sys-002")));

  const body1 = (await sys1.json()) as Record<string, unknown>;
  const body2 = (await sys2.json()) as Record<string, unknown>;

  assert.equal(body1.webrtcWhepUrl, "https://cam.example.com/sys-001/whep");
  assert.equal(body1.streamToken, "jwt-for-sys-001");
  assert.equal(body2.webrtcWhepUrl, "https://cam.example.com/sys-002/whep");
  assert.equal(body2.streamToken, "jwt-for-sys-002");
});

test("GET /api/realtime/ticket rejects a missing cookie", async () => {
  const response = handleRealtimeTicket(ticketRequest());

  assert.equal(response.status, 401);
  const body = (await response.json()) as Record<string, unknown>;
  assert.equal(body.title, "Unauthorized");
});

test("GET /api/realtime/ticket rejects a tampered cookie", async () => {
  const valid = sessionCookie("sys-001");
  const tampered = `${valid.slice(0, -4)}AAAA`;

  const response = handleRealtimeTicket(ticketRequest(tampered));
  assert.equal(response.status, 401);
});

test("GET /api/realtime/ticket rejects an expired session", async () => {
  const response = handleRealtimeTicket(ticketRequest(sessionCookie("sys-001", -10)));
  assert.equal(response.status, 401);
});

test("GET /api/realtime/ticket returns 503 when MediaMTX is not configured", async () => {
  delete process.env.MEDIAMTX_PUBLIC_URL;

  const response = handleRealtimeTicket(ticketRequest(sessionCookie("sys-001")));

  assert.equal(response.status, 503);
  const body = (await response.json()) as Record<string, unknown>;
  assert.equal(body.title, "Realtime unavailable");
  assert.match(String(body.detail), /MEDIAMTX_PUBLIC_URL/);
});
