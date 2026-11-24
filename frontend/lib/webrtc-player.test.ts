import assert from "node:assert/strict";
import test from "node:test";
import {
  DEFAULT_ICE_TIMEOUT_MS,
  fetchRealtimeTicket,
  ICE_SERVERS,
  postWhepOffer,
  WebRtcPlayerController,
  WebRtcUnavailableError,
  type PeerConnectionLike,
  type WebRtcScheduler,
} from "./webrtc-player.ts";

/** Fake timer scheduler; time only moves when the test advances it. */
function createScheduler(): WebRtcScheduler & {
  advance: (milliseconds: number) => void;
  pendingDelays: () => number[];
} {
  let now = 0;
  let nextHandle = 1;
  const timers = new Map<number, { due: number; callback: () => void }>();

  return {
    setTimeout(callback: () => void, delay: number): unknown {
      const handle = nextHandle++;
      timers.set(handle, { due: now + delay, callback });
      return handle;
    },
    clearTimeout(handle: unknown): void {
      timers.delete(handle as number);
    },
    advance(milliseconds: number): void {
      const target = now + milliseconds;

      while (true) {
        let next: { handle: number; due: number; callback: () => void } | null = null;

        for (const [handle, timer] of timers) {
          if (timer.due > target) {
            continue;
          }

          if (!next || timer.due < next.due || (timer.due === next.due && handle < next.handle)) {
            next = { handle, ...timer };
          }
        }

        if (!next) {
          break;
        }

        timers.delete(next.handle);
        now = next.due;
        next.callback();
      }

      now = target;
    },
    pendingDelays(): number[] {
      return [...timers.values()].map((timer) => timer.due - now).sort((a, b) => a - b);
    },
  };
}

/** Minimal RTCPeerConnection stub. `iceConnectionState` is driven by the test. */
class FakePeerConnection implements PeerConnectionLike {
  readonly transceivers: Array<{ kind: string; direction: string }> = [];
  readonly localDescriptions: Array<{ type: string; sdp?: string }> = [];
  readonly remoteDescriptions: Array<{ type: string; sdp?: string }> = [];
  closed = false;
  iceConnectionState = "new";
  connectionState = "new";
  localDescription: { type: string; sdp?: string } | null = null;
  oniceconnectionstatechange: ((event: unknown) => void) | null = null;
  ontrack: ((event: { streams?: MediaStream[] }) => void) | null = null;

  async createOffer(): Promise<{ type: string; sdp?: string }> {
    return { type: "offer", sdp: "v=0\r\noffer-sdp" };
  }

  async setLocalDescription(description: { type: string; sdp?: string }): Promise<void> {
    this.localDescriptions.push(description);
    this.localDescription = description;
  }

  async setRemoteDescription(description: { type: string; sdp?: string }): Promise<void> {
    this.remoteDescriptions.push(description);
  }

  addTransceiver(kind: string, init: { direction: string }): unknown {
    this.transceivers.push({ kind, direction: init.direction });
    return {};
  }

  close(): void {
    this.closed = true;
  }

  setIceState(state: string): void {
    this.iceConnectionState = state;
    this.oniceconnectionstatechange?.(null);
  }
}

interface CapturedRequest {
  url: string;
  method: string;
  headers: Headers;
  body: string | null;
}

const TICKET = {
  systemId: "sys-001",
  webrtcWhepUrl: "https://cam.example.com/sys-001/whep",
  streamToken: "edge-jwt-for-sys-001",
  mqttWsUrl: "wss://mqtt.example.com/mqtt",
  mqttUsername: "dashboard",
  mqttPassword: "dashboard-pass",
};

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function sdpResponse(status: number, body: string, location?: string): Response {
  const headers: Record<string, string> = { "Content-Type": "application/sdp" };
  if (location) {
    headers.location = location;
  }
  return new Response(body, { status, headers });
}

interface Harness {
  controller: WebRtcPlayerController;
  peer: FakePeerConnection;
  scheduler: ReturnType<typeof createScheduler>;
  requests: CapturedRequest[];
  streams: MediaStream[];
}

function createHarness(
  responder: (request: CapturedRequest) => Response | Promise<Response> = () => {
    throw new Error("unexpected request");
  },
  iceTimeoutMs = DEFAULT_ICE_TIMEOUT_MS,
): Harness {
  const scheduler = createScheduler();
  const peer = new FakePeerConnection();
  const requests: CapturedRequest[] = [];
  const streams: MediaStream[] = [];

  const controller = new WebRtcPlayerController({
    scheduler,
    iceTimeoutMs,
    createPeerConnection: () => peer,
    onStream: (stream) => streams.push(stream),
    fetch: async (input, init) => {
      const request: CapturedRequest = {
        url: input,
        method: init?.method ?? "GET",
        headers: new Headers(init?.headers),
        body: typeof init?.body === "string" ? init.body : null,
      };
      requests.push(request);
      return responder(request);
    },
  });

  return { controller, peer, scheduler, requests, streams };
}

test("fetchRealtimeTicket sends the sealed cookie and validates the WHEP fields", async () => {
  const { controller: _unused, requests } = createHarness(() => jsonResponse(200, TICKET));
  void _unused;

  const ticket = await fetchRealtimeTicket(
    async (input, init) => {
      requests.push({
        url: input,
        method: init?.method ?? "GET",
        headers: new Headers(init?.headers),
        body: null,
      });
      return jsonResponse(200, TICKET);
    },
    "/api/realtime/ticket",
  );

  assert.equal(ticket.systemId, "sys-001");
  assert.equal(ticket.webrtcWhepUrl, TICKET.webrtcWhepUrl);
  assert.equal(ticket.streamToken, TICKET.streamToken);
  assert.equal(requests[0]?.url, "/api/realtime/ticket");
  assert.equal(requests[0]?.headers.get("accept"), "application/json");
});

test("fetchRealtimeTicket reports an expired session on 401", async () => {
  await assert.rejects(
    () =>
      fetchRealtimeTicket(
        async () => jsonResponse(401, { title: "Unauthorized", detail: "Please sign in." }),
        "/api/realtime/ticket",
      ),
    (error: unknown) => {
      assert.ok(error instanceof WebRtcUnavailableError);
      assert.match(error.message, /session expired/i);
      return true;
    },
  );
});

test("fetchRealtimeTicket surfaces the 503 detail when MediaMTX is not configured", async () => {
  await assert.rejects(
    () =>
      fetchRealtimeTicket(
        async () =>
          jsonResponse(503, {
            title: "Realtime unavailable",
            detail: "WebRTC is not configured. Set MEDIAMTX_PUBLIC_URL.",
          }),
        "/api/realtime/ticket",
      ),
    (error: unknown) => {
      assert.ok(error instanceof WebRtcUnavailableError);
      assert.match(error.message, /MEDIAMTX_PUBLIC_URL/);
      return true;
    },
  );
});

test("fetchRealtimeTicket rejects a ticket without a WHEP URL or token", async () => {
  await assert.rejects(
    () =>
      fetchRealtimeTicket(
        async () => jsonResponse(200, { systemId: "sys-001", webrtcWhepUrl: "", streamToken: "" }),
        "/api/realtime/ticket",
      ),
    (error: unknown) => {
      assert.ok(error instanceof WebRtcUnavailableError);
      assert.match(error.message, /missing the WHEP URL or stream token/);
      return true;
    },
  );
});

test("postWhepOffer posts SDP with the bearer token and reads the Location header", async () => {
  const captured: CapturedRequest[] = [];

  const result = await postWhepOffer(
    async (input, init) => {
      captured.push({
        url: input,
        method: init?.method ?? "GET",
        headers: new Headers(init?.headers),
        body: typeof init?.body === "string" ? init.body : null,
      });
      return sdpResponse(201, "v=0\r\nanswer-sdp", "https://cam.example.com/sys-001/whep/abc123");
    },
    TICKET.webrtcWhepUrl,
    TICKET.streamToken,
    "v=0\r\noffer-sdp",
  );

  assert.equal(result.answerSdp, "v=0\r\nanswer-sdp");
  assert.equal(result.resourceUrl, "https://cam.example.com/sys-001/whep/abc123");
  assert.equal(result.status, 201);

  assert.equal(captured[0]?.method, "POST");
  assert.equal(captured[0]?.headers.get("content-type"), "application/sdp");
  assert.equal(captured[0]?.headers.get("authorization"), `Bearer ${TICKET.streamToken}`);
  assert.equal(captured[0]?.body, "v=0\r\noffer-sdp");
});

test("postWhepOffer maps MediaMTX 404/401/403 to actionable messages", async () => {
  await assert.rejects(
    () => postWhepOffer(async () => sdpResponse(404, ""), "u", "t", "s"),
    /not publishing a stream yet/,
  );
  await assert.rejects(
    () => postWhepOffer(async () => sdpResponse(401, ""), "u", "t", "s"),
    /rejected by MediaMTX/,
  );
  await assert.rejects(
    () => postWhepOffer(async () => sdpResponse(403, ""), "u", "t", "s"),
    /rejected by MediaMTX/,
  );
  await assert.rejects(
    () => postWhepOffer(async () => sdpResponse(500, ""), "u", "t", "s"),
    /HTTP 500/,
  );
});

test("start() negotiates: ticket -> offer -> answer -> live on the first track", async () => {
  const { controller, peer, requests, streams } = createHarness((request) => {
    if (request.url === "/api/realtime/ticket") {
      return jsonResponse(200, TICKET);
    }
    if (request.url === TICKET.webrtcWhepUrl) {
      return sdpResponse(201, "v=0\r\nanswer-sdp", "https://cam.example.com/sys-001/whep/abc123");
    }
    return jsonResponse(500, {});
  });

  const pending = controller.start();
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual(peer.transceivers, [{ kind: "video", direction: "recvonly" }]);
  assert.equal(peer.localDescriptions.length, 1);
  assert.equal(peer.remoteDescriptions.length, 1);
  assert.equal(peer.remoteDescriptions[0]?.type, "answer");
  assert.equal(peer.remoteDescriptions[0]?.sdp, "v=0\r\nanswer-sdp");

  const stream = { id: "stream-1" } as unknown as MediaStream;
  peer.ontrack?.({ streams: [stream] });

  const result = await pending;
  assert.equal(result, stream);
  assert.equal(streams.length, 1);
  assert.equal(controller.getState().status, "live");
  assert.equal(controller.getState().hasVideo, true);
  assert.equal(controller.getState().resourceUrl, "https://cam.example.com/sys-001/whep/abc123");

  // The ticket request carries no cookies for MediaMTX; the WHEP POST is cross-origin.
  assert.equal(requests.length, 2);
  assert.equal(requests[1]?.headers.get("authorization"), `Bearer ${TICKET.streamToken}`);

  await controller.stop();
});

test("start() falls back when the ticket endpoint fails", async () => {
  const { controller } = createHarness(() => jsonResponse(503, { detail: "WebRTC is not configured." }));

  const stream = await controller.start();

  assert.equal(stream, null);
  const state = controller.getState();
  assert.equal(state.status, "fallback");
  assert.match(state.message ?? "", /not configured/);
  assert.equal(state.hasVideo, false);
});

test("the 5-second ICE watchdog falls back to MJPEG", async () => {
  const { controller, peer, scheduler } = createHarness((request) => {
    if (request.url === "/api/realtime/ticket") {
      return jsonResponse(200, TICKET);
    }
    return sdpResponse(201, "v=0\r\nanswer-sdp", "https://cam.example.com/sys-001/whep/abc123");
  });

  void controller.start();
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual(scheduler.pendingDelays(), [DEFAULT_ICE_TIMEOUT_MS]);

  scheduler.advance(DEFAULT_ICE_TIMEOUT_MS - 1);
  assert.equal(controller.getState().status, "connecting");

  scheduler.advance(1);
  const state = controller.getState();
  assert.equal(state.status, "fallback");
  assert.match(state.message ?? "", /did not connect within 5 seconds/);
  assert.equal(peer.closed, true);
});

test("a connected ICE state clears the watchdog", async () => {
  const { controller, peer, scheduler } = createHarness((request) => {
    if (request.url === "/api/realtime/ticket") {
      return jsonResponse(200, TICKET);
    }
    return sdpResponse(201, "v=0\r\nanswer-sdp", "https://cam.example.com/sys-001/whep/abc123");
  });

  void controller.start();
  await new Promise((resolve) => setImmediate(resolve));

  peer.setIceState("connected");
  assert.deepEqual(scheduler.pendingDelays(), []);

  // A late watchdog must not knock a healthy session into fallback.
  scheduler.advance(DEFAULT_ICE_TIMEOUT_MS);
  assert.equal(controller.getState().status, "connecting");
});

test("a failed ICE state falls back immediately", async () => {
  const { controller, peer } = createHarness((request) => {
    if (request.url === "/api/realtime/ticket") {
      return jsonResponse(200, TICKET);
    }
    return sdpResponse(201, "v=0\r\nanswer-sdp", "https://cam.example.com/sys-001/whep/abc123");
  });

  void controller.start();
  await new Promise((resolve) => setImmediate(resolve));

  peer.setIceState("failed");

  const state = controller.getState();
  assert.equal(state.status, "fallback");
  assert.match(state.message ?? "", /ICE negotiation failed/);
  assert.equal(peer.closed, true);
});

test("stop() closes the peer and DELETEs the WHEP resource", async () => {
  const { controller, peer, requests } = createHarness((request) => {
    if (request.url === "/api/realtime/ticket") {
      return jsonResponse(200, TICKET);
    }
    if (request.method === "DELETE") {
      return new Response(null, { status: 200 });
    }
    return sdpResponse(201, "v=0\r\nanswer-sdp", "https://cam.example.com/sys-001/whep/abc123");
  });

  void controller.start();
  await new Promise((resolve) => setImmediate(resolve));

  await controller.stop();

  assert.equal(peer.closed, true);
  const deletes = requests.filter((request) => request.method === "DELETE");
  assert.equal(deletes.length, 1);
  assert.equal(deletes[0]?.url, "https://cam.example.com/sys-001/whep/abc123");
  assert.equal(controller.getState().status, "idle");
});

test("a failed DELETE during teardown is not fatal", async () => {
  const { controller } = createHarness((request) => {
    if (request.url === "/api/realtime/ticket") {
      return jsonResponse(200, TICKET);
    }
    if (request.method === "DELETE") {
      throw new Error("network down");
    }
    return sdpResponse(201, "v=0\r\nanswer-sdp", "https://cam.example.com/sys-001/whep/abc123");
  });

  void controller.start();
  await new Promise((resolve) => setImmediate(resolve));

  await controller.stop();
  assert.equal(controller.getState().status, "idle");
});

test("ICE_SERVERS points at the public STUN server", () => {
  assert.deepEqual(ICE_SERVERS, [{ urls: "stun:stun.cloudflare.com:3478" }]);
});
