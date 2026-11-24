/**
 * WHEP (WebRTC-HTTP Egress Protocol) player core.
 *
 * Deliberately dependency-free: no React, no `next/*`, no third-party WebRTC SDK. Everything the
 * browser needs — `RTCPeerConnection`, `fetch`, timers — is injected so the negotiation, the
 * 5-second ICE watchdog and the teardown path can be exercised by `node --test`.
 *
 * Flow against MediaMTX:
 *   1. GET  /api/realtime/ticket            -> { webrtcWhepUrl, streamToken, ... }
 *   2. POST <webrtcWhepUrl>  application/sdp -> 201 Created + SDP answer + Location
 *   3. setRemoteDescription(answer)          -> `ontrack` fires with the MediaStream
 *   4. DELETE <Location> on teardown         -> MediaMTX stops sending
 *
 * Video never traverses Next.js: the browser talks to MediaMTX directly over UDP/SRTP.
 */

export type WebRtcPlayerStatus =
  /** Not started yet. */
  | "idle"
  /** Ticket fetched, SDP offer posted, waiting for the answer/ICE. */
  | "connecting"
  /** ICE connected and a video track is flowing. */
  | "live"
  /** WebRTC unavailable or failed; the caller should render the MJPEG fallback. */
  | "fallback"
  /** MediaMTX reachable but no publisher (camera offline). */
  | "offline";

export interface WebRtcPlayerState {
  status: WebRtcPlayerStatus;
  /** Human-readable reason shown next to the badge. */
  message: string | null;
  /** True once a MediaStream with at least one video track has been attached. */
  hasVideo: boolean;
  /** MediaMTX WHEP resource URL, when the server advertised one. */
  resourceUrl: string | null;
}

export interface RealtimeTicket {
  systemId: string;
  webrtcWhepUrl: string;
  streamToken: string;
  mqttWsUrl: string;
  mqttUsername: string;
  mqttPassword: string;
}

export type WebRtcFetch = (input: string, init?: RequestInit) => Promise<Response>;

export interface WebRtcScheduler {
  setTimeout(callback: () => void, delay: number): unknown;
  clearTimeout(handle: unknown): void;
}

/** Minimal slice of `RTCPeerConnection` the controller depends on (keeps tests honest). */
export interface PeerConnectionLike {
  createOffer(): Promise<{ type: string; sdp?: string }>;
  setLocalDescription(description: { type: string; sdp?: string }): Promise<void>;
  setRemoteDescription(description: { type: string; sdp?: string }): Promise<void>;
  addTransceiver(trackOrKind: string, init: { direction: string }): unknown;
  close(): void;
  readonly iceConnectionState: string;
  readonly connectionState: string;
  localDescription: { type: string; sdp?: string } | null;
  oniceconnectionstatechange: ((event: unknown) => void) | null;
  ontrack: ((event: { streams?: MediaStream[] }) => void) | null;
}

export interface WebRtcPlayerOptions {
  fetch?: WebRtcFetch;
  createPeerConnection?: (config: { iceServers: Array<{ urls: string }> }) => PeerConnectionLike;
  scheduler?: WebRtcScheduler;
  /** How long to wait for ICE to reach `connected` before falling back to MJPEG. */
  iceTimeoutMs?: number;
  /** Ticket endpoint. Defaults to the BFF route. */
  ticketUrl?: string;
  /** Called when the SDP answer arrives or the state changes. */
  onStateChange?: (state: WebRtcPlayerState) => void;
  onStream?: (stream: MediaStream) => void;
}

/** Public STUN server used by both the browser and MediaMTX. */
export const ICE_SERVERS = [{ urls: "stun:stun.cloudflare.com:3478" }];

/** The spec's 5-second budget for a first frame before the MJPEG fallback takes over. */
export const DEFAULT_ICE_TIMEOUT_MS = 5_000;

const defaultScheduler: WebRtcScheduler = {
  setTimeout: (callback, delay) => globalThis.setTimeout(callback, delay),
  clearTimeout: (handle) =>
    globalThis.clearTimeout(handle as ReturnType<typeof globalThis.setTimeout>),
};

const defaultFetch: WebRtcFetch = (input, init) => globalThis.fetch(input, init);

export class WebRtcUnavailableError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "WebRtcUnavailableError";
  }
}

/**
 * Fetches the tenant ticket from the BFF. A 401 means the session expired (the caller should
 * redirect to /login); a 503 means the operator has not configured MediaMTX yet.
 */
export async function fetchRealtimeTicket(
  fetchImpl: WebRtcFetch = defaultFetch,
  ticketUrl = "/api/realtime/ticket",
): Promise<RealtimeTicket> {
  const response = await fetchImpl(ticketUrl, {
    method: "GET",
    headers: { accept: "application/json" },
    cache: "no-store",
    credentials: "same-origin",
  });

  if (response.status === 401) {
    throw new WebRtcUnavailableError("Your session expired. Sign in again to watch live video.");
  }

  if (!response.ok) {
    let detail = `Realtime ticket request failed (HTTP ${response.status}).`;

    try {
      const body = (await response.json()) as { detail?: string; title?: string };
      detail = body.detail ?? body.title ?? detail;
    } catch {
      // Non-JSON error body; keep the generic message.
    }

    throw new WebRtcUnavailableError(detail);
  }

  const ticket = (await response.json()) as Partial<RealtimeTicket>;

  if (
    typeof ticket.webrtcWhepUrl !== "string" ||
    ticket.webrtcWhepUrl.length === 0 ||
    typeof ticket.streamToken !== "string" ||
    ticket.streamToken.length === 0
  ) {
    throw new WebRtcUnavailableError("The realtime ticket is missing the WHEP URL or stream token.");
  }

  return {
    systemId: typeof ticket.systemId === "string" ? ticket.systemId : "",
    webrtcWhepUrl: ticket.webrtcWhepUrl,
    streamToken: ticket.streamToken,
    mqttWsUrl: typeof ticket.mqttWsUrl === "string" ? ticket.mqttWsUrl : "",
    mqttUsername: typeof ticket.mqttUsername === "string" ? ticket.mqttUsername : "",
    mqttPassword: typeof ticket.mqttPassword === "string" ? ticket.mqttPassword : "",
  };
}

/**
 * Posts the SDP offer to MediaMTX and returns the answer plus the WHEP resource URL.
 *
 * MediaMTX answers `201 Created` with `Content-Type: application/sdp` and a `Location` header
 * pointing at the session resource; that URL is what a `DELETE` tears down.
 */
export async function postWhepOffer(
  fetchImpl: WebRtcFetch,
  whepUrl: string,
  streamToken: string,
  offerSdp: string,
): Promise<{ answerSdp: string; resourceUrl: string | null; status: number }> {
  const response = await fetchImpl(whepUrl, {
    method: "POST",
    headers: {
      "Content-Type": "application/sdp",
      Accept: "application/sdp",
      Authorization: `Bearer ${streamToken}`,
    },
    body: offerSdp,
    // Never send cookies to MediaMTX; the bearer token is the only credential it needs.
    credentials: "omit",
    cache: "no-store",
  });

  if (response.status === 404) {
    // MediaMTX answers 404 when the path has no publisher yet (camera not streaming).
    throw new WebRtcUnavailableError("The camera is not publishing a stream yet.");
  }

  if (response.status === 401 || response.status === 403) {
    throw new WebRtcUnavailableError("The stream ticket was rejected by MediaMTX.");
  }

  if (!response.ok) {
    throw new WebRtcUnavailableError(`MediaMTX rejected the WHEP offer (HTTP ${response.status}).`);
  }

  const answerSdp = await response.text();

  if (!answerSdp.trim()) {
    throw new WebRtcUnavailableError("MediaMTX returned an empty SDP answer.");
  }

  return {
    answerSdp,
    resourceUrl: response.headers.get("location"),
    status: response.status,
  };
}

/**
 * Owns one WHEP session: ticket -> offer -> answer -> ICE watchdog -> teardown.
 *
 * Call {@link start} to connect, {@link stop} to release the peer connection and the MediaMTX
 * session. Every failure lands in the `fallback` state so the UI can show the MJPEG stream.
 */
export class WebRtcPlayerController {
  private readonly fetchImpl: WebRtcFetch;
  private readonly createPeerConnection: (config: {
    iceServers: Array<{ urls: string }>;
  }) => PeerConnectionLike;
  private readonly scheduler: WebRtcScheduler;
  private readonly iceTimeoutMs: number;
  private readonly ticketUrl: string;
  private readonly onStateChange?: (state: WebRtcPlayerState) => void;
  private readonly onStream?: (stream: MediaStream) => void;

  private peer: PeerConnectionLike | null = null;
  private resourceUrl: string | null = null;
  private iceTimer: unknown = null;
  private stopped = false;
  private state: WebRtcPlayerState = {
    status: "idle",
    message: null,
    hasVideo: false,
    resourceUrl: null,
  };

  constructor(options: WebRtcPlayerOptions = {}) {
    this.fetchImpl = options.fetch ?? defaultFetch;
    this.scheduler = options.scheduler ?? defaultScheduler;
    this.iceTimeoutMs = options.iceTimeoutMs ?? DEFAULT_ICE_TIMEOUT_MS;
    this.ticketUrl = options.ticketUrl ?? "/api/realtime/ticket";
    this.onStateChange = options.onStateChange;
    this.onStream = options.onStream;
    this.createPeerConnection =
      options.createPeerConnection ??
      ((config) => new globalThis.RTCPeerConnection(config) as unknown as PeerConnectionLike);
  }

  getState(): WebRtcPlayerState {
    return this.state;
  }

  private setState(patch: Partial<WebRtcPlayerState>): void {
    this.state = { ...this.state, ...patch };
    this.onStateChange?.(this.state);
  }

  /** Connects to MediaMTX. Resolves with the negotiated MediaStream, or `null` on fallback. */
  async start(): Promise<MediaStream | null> {
    this.stopped = false;
    this.setState({ status: "connecting", message: null, hasVideo: false, resourceUrl: null });

    try {
      const ticket = await fetchRealtimeTicket(this.fetchImpl, this.ticketUrl);
      if (this.stopped) {
        return null;
      }

      return await this.negotiate(ticket);
    } catch (error) {
      if (this.stopped) {
        return null;
      }

      this.fail(error);
      return null;
    }
  }

  private async negotiate(ticket: RealtimeTicket): Promise<MediaStream | null> {
    const peer = this.createPeerConnection({ iceServers: ICE_SERVERS });
    this.peer = peer;
    peer.addTransceiver("video", { direction: "recvonly" });

    const streamPromise = new Promise<MediaStream | null>((resolve) => {
      peer.ontrack = (event) => {
        const stream = event.streams?.[0] ?? null;

        if (!stream) {
          return;
        }

        this.clearIceTimer();
        this.setState({
          status: "live",
          message: null,
          hasVideo: true,
          resourceUrl: this.resourceUrl,
        });
        this.onStream?.(stream);
        resolve(stream);
      };
    });

    peer.oniceconnectionstatechange = () => {
      const iceState = peer.iceConnectionState;

      if (iceState === "failed") {
        this.fail(new WebRtcUnavailableError("WebRTC ICE negotiation failed."));
        return;
      }

      if (iceState === "connected" || iceState === "completed") {
        this.clearIceTimer();
      }
    };

    const offer = await peer.createOffer();
    await peer.setLocalDescription(offer);

    const offerSdp = peer.localDescription?.sdp ?? offer.sdp;

    if (!offerSdp) {
      throw new WebRtcUnavailableError("The browser produced an empty SDP offer.");
    }

    const answer = await postWhepOffer(
      this.fetchImpl,
      ticket.webrtcWhepUrl,
      ticket.streamToken,
      offerSdp,
    );
    this.resourceUrl = answer.resourceUrl;

    if (this.stopped) {
      await this.releaseResource();
      return null;
    }

    await peer.setRemoteDescription({ type: "answer", sdp: answer.answerSdp });

    // Watchdog: a first frame within 5 s or the UI falls back to MJPEG. An `ontrack` or a
    // connected ICE state clears it.
    this.iceTimer = this.scheduler.setTimeout(() => {
      if (this.state.status === "live" || this.stopped) {
        return;
      }

      if (peer.iceConnectionState === "connected" || peer.iceConnectionState === "completed") {
        return;
      }

      this.fail(new WebRtcUnavailableError("WebRTC did not connect within 5 seconds."));
    }, this.iceTimeoutMs);

    return streamPromise;
  }

  /** Tears down the peer connection and the MediaMTX session. */
  async stop(): Promise<void> {
    this.stopped = true;
    this.clearIceTimer();

    if (this.peer) {
      this.peer.oniceconnectionstatechange = null;
      this.peer.ontrack = null;
      try {
        this.peer.close();
      } catch {
        // Closing an already-closed peer is harmless.
      }
      this.peer = null;
    }

    await this.releaseResource();
    this.setState({ status: "idle", message: null, hasVideo: false, resourceUrl: null });
  }

  private async releaseResource(): Promise<void> {
    const resourceUrl = this.resourceUrl;
    this.resourceUrl = null;

    if (!resourceUrl) {
      return;
    }

    try {
      await this.fetchImpl(resourceUrl, { method: "DELETE", credentials: "omit", cache: "no-store" });
    } catch {
      // MediaMTX expires idle sessions on its own; a failed DELETE is not fatal.
    }
  }

  private clearIceTimer(): void {
    if (this.iceTimer !== null) {
      this.scheduler.clearTimeout(this.iceTimer);
      this.iceTimer = null;
    }
  }

  private fail(error: unknown): void {
    this.clearIceTimer();
    const message =
      error instanceof Error ? error.message : "WebRTC video is unavailable.";

    this.setState({ status: "fallback", message, hasVideo: false, resourceUrl: null });

    if (this.peer) {
      this.peer.oniceconnectionstatechange = null;
      this.peer.ontrack = null;
      try {
        this.peer.close();
      } catch {
        // ignore
      }
      this.peer = null;
    }

    void this.releaseResource();
  }
}
