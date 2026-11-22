export const MAX_LIVE_DURATION_MS = 5 * 60 * 1_000;
export const FALLBACK_POLL_INTERVAL_MS = 1_000;

export type LiveFeedStatus =
  | "snapshot-loading"
  | "snapshot"
  | "offline"
  | "live-connecting"
  | "live"
  | "live-fallback"
  | "paused-hidden"
  | "paused-timeout";

export interface LiveFeedState {
  status: LiveFeedStatus;
  snapshotUrl: string | null;
  snapshotTakenAt: number | null;
  snapshotLoading: boolean;
  streamArmed: boolean;
  streamKey: number;
  message: string | null;
}

export type StopLiveReason = "user" | "hidden" | "timeout" | "limited";

type SnapshotRequestKind = "initial" | "refresh" | "fallback";

export type LiveFeedFetch = (input: string, init?: RequestInit) => Promise<Response>;

export interface LiveFeedScheduler {
  setTimeout(callback: () => void, delay: number): unknown;
  clearTimeout(handle: unknown): void;
  setInterval(callback: () => void, delay: number): unknown;
  clearInterval(handle: unknown): void;
}

export interface LiveFeedControllerOptions {
  fetch?: LiveFeedFetch;
  createObjectURL?: (blob: Blob) => string;
  revokeObjectURL?: (url: string) => void;
  scheduler?: LiveFeedScheduler;
  captureFrame?: () => Promise<Blob | null>;
  now?: () => Date;
  maxLiveDurationMs?: number;
}

const defaultScheduler: LiveFeedScheduler = {
  setTimeout: (callback, delay) => globalThis.setTimeout(callback, delay),
  clearTimeout: (handle) =>
    globalThis.clearTimeout(handle as ReturnType<typeof globalThis.setTimeout>),
  setInterval: (callback, delay) => globalThis.setInterval(callback, delay),
  clearInterval: (handle) =>
    globalThis.clearInterval(handle as ReturnType<typeof globalThis.setInterval>),
};

const defaultFetch: LiveFeedFetch = (input, init) => globalThis.fetch(input, init);

export function isLiveFeedLive(state: LiveFeedState): boolean {
  return (
    state.status === "live-connecting" ||
    state.status === "live" ||
    state.status === "live-fallback"
  );
}

export function isLiveFeedPaused(state: LiveFeedState): boolean {
  return state.status === "paused-hidden" || state.status === "paused-timeout";
}

/**
 * Owns the feed's network/timer lifecycle. Keeping this independent from React makes the
 * transitions deterministic and lets tests drive fetch and timers without a DOM.
 */
export class LiveFeedController {
  private readonly fetchFrame: LiveFeedFetch;
  private readonly createObjectURL: (blob: Blob) => string;
  private readonly revokeObjectURL: (url: string) => void;
  private readonly scheduler: LiveFeedScheduler;
  private captureFrame: (() => Promise<Blob | null>) | undefined;
  private readonly now: () => Date;
  private readonly maxLiveDurationMs: number;

  private readonly listeners = new Set<() => void>();
  private state: LiveFeedState = {
    status: "snapshot-loading",
    snapshotUrl: null,
    snapshotTakenAt: null,
    snapshotLoading: true,
    streamArmed: false,
    streamKey: 0,
    message: null,
  };

  private started = false;
  private disposed = false;
  private snapshotInFlight = false;
  private requestId = 0;
  private snapshotAbort: AbortController | null = null;
  private probeAbort: AbortController | null = null;
  private fallbackTimer: unknown = null;
  private liveTimer: unknown = null;

  constructor(options: LiveFeedControllerOptions = {}) {
    this.fetchFrame = options.fetch ?? defaultFetch;
    this.createObjectURL = options.createObjectURL ?? ((blob) => URL.createObjectURL(blob));
    this.revokeObjectURL = options.revokeObjectURL ?? ((url) => URL.revokeObjectURL(url));
    this.scheduler = options.scheduler ?? defaultScheduler;
    this.captureFrame = options.captureFrame;
    this.now = options.now ?? (() => new Date());
    this.maxLiveDurationMs = options.maxLiveDurationMs ?? MAX_LIVE_DURATION_MS;
  }

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);

    return () => {
      this.listeners.delete(listener);
    };
  };

  getSnapshot = (): LiveFeedState => this.state;

  setCaptureFrame(captureFrame: () => Promise<Blob | null>): void {
    this.captureFrame = captureFrame;
  }

  start(): void {
    if (this.started) {
      return;
    }

    this.started = true;
    this.disposed = false;
    void this.requestSnapshot("initial");
  }

  dispose(): void {
    this.disposed = true;
    this.started = false;
    this.requestId += 1;
    this.clearLiveTimers();
    this.snapshotAbort?.abort();
    this.probeAbort?.abort();
    this.snapshotAbort = null;
    this.probeAbort = null;
    this.snapshotInFlight = false;

    if (this.state.snapshotUrl) {
      this.revokeObjectURL(this.state.snapshotUrl);
    }

    this.state = {
      ...this.state,
      snapshotUrl: null,
      snapshotTakenAt: null,
      streamArmed: false,
    };
    this.listeners.clear();
  }

  refreshSnapshot(): void {
    if (!this.started || this.disposed || isLiveFeedLive(this.state)) {
      return;
    }

    void this.requestSnapshot("refresh");
  }

  async startLive(): Promise<void> {
    if (!this.started || this.disposed || isLiveFeedLive(this.state)) {
      return;
    }

    const streamKey = this.state.streamKey + 1;

    this.setState({
      status: "live-connecting",
      streamArmed: false,
      streamKey,
      message: null,
    });
    this.startLiveTimer(streamKey);

    await this.probeStream(streamKey);
  }

  handleStreamFrame(): void {
    if (!this.isCurrentLive(this.state.streamKey) || !this.state.streamArmed) {
      return;
    }

    if (this.state.status === "live-connecting") {
      this.setState({ status: "live" });
    }
  }

  handleStreamError(): void {
    if (!isLiveFeedLive(this.state) || this.state.status === "live-fallback") {
      return;
    }

    this.beginFallback();
  }

  handleVisibilityChange(hidden: boolean): void {
    if (hidden && isLiveFeedLive(this.state)) {
      this.stopLive("hidden");
    }
  }

  stopLive(reason: StopLiveReason): void {
    if (!isLiveFeedLive(this.state) && reason !== "limited") {
      return;
    }

    const capturedFrame =
      reason === "limited" ? null : this.captureFrame?.() ?? null;

    this.clearLiveTimers();
    this.probeAbort?.abort();
    this.probeAbort = null;

    let status: LiveFeedStatus;

    if (reason === "hidden") {
      status = "paused-hidden";
    } else if (reason === "timeout") {
      status = "paused-timeout";
    } else {
      status = this.state.snapshotUrl ? "snapshot" : "offline";
    }

    this.setState({
      status,
      streamArmed: false,
      message: reason === "limited" ? "Too many viewers, try again shortly" : null,
    });

    if (capturedFrame) {
      void capturedFrame
        .then((blob) => {
          if (this.disposed || !blob) {
            return;
          }

          this.replaceSnapshot(blob, this.now());

          if (this.state.status === "offline" && reason === "user") {
            this.setState({ status: "snapshot" });
          }
        })
        .catch(() => {
          // Keep the previous snapshot when the browser cannot read the stream frame.
        });
    }
  }

  private setState(patch: Partial<LiveFeedState>): void {
    this.state = { ...this.state, ...patch };
    this.emit();
  }

  private emit(): void {
    for (const listener of this.listeners) {
      listener();
    }
  }

  private async requestSnapshot(kind: SnapshotRequestKind): Promise<void> {
    if (this.snapshotInFlight) {
      return;
    }

    this.snapshotInFlight = true;
    const requestId = ++this.requestId;
    const abort = new AbortController();
    this.snapshotAbort = abort;

    if (kind !== "fallback") {
      this.setState({ snapshotLoading: true });
    }

    try {
      const response = await this.fetchFrame("/api/camera/latest", {
        cache: "no-store",
        credentials: "same-origin",
        headers: { Accept: "image/jpeg" },
        signal: abort.signal,
      });

      if (!this.isCurrentRequest(requestId, abort)) {
        return;
      }

      if (response.status === 204) {
        this.handleSnapshotFailure(kind);
        return;
      }

      if (!response.ok) {
        throw new Error(`Snapshot request failed (${response.status})`);
      }

      const blob = await response.blob();

      if (!this.isCurrentRequest(requestId, abort) || blob.size === 0) {
        if (blob.size === 0) {
          this.handleSnapshotFailure(kind);
        }
        return;
      }

      this.replaceSnapshot(blob, this.now());

      if (kind === "fallback") {
        if (this.state.status === "live-fallback") {
          this.setState({ message: null });
        }
        return;
      }

      if (isLiveFeedLive(this.state) || isLiveFeedPaused(this.state)) {
        this.setState({ snapshotLoading: false });
      } else {
        this.setState({
          status: "snapshot",
          snapshotLoading: false,
          message: null,
        });
      }
    } catch {
      if (!this.isCurrentRequest(requestId, abort)) {
        return;
      }

      this.handleSnapshotFailure(kind);
    } finally {
      if (requestId === this.requestId) {
        this.snapshotInFlight = false;
        this.snapshotAbort = null;
      }
    }
  }

  private handleSnapshotFailure(kind: SnapshotRequestKind): void {
    if (kind === "fallback") {
      if (this.state.status === "live-fallback") {
        this.setState({ message: "Camera offline" });
      }
      return;
    }

    if (isLiveFeedLive(this.state) || isLiveFeedPaused(this.state)) {
      this.setState({ snapshotLoading: false });
      return;
    }

    this.clearSnapshot();
    this.setState({
      status: "offline",
      snapshotLoading: false,
      message: null,
    });
  }

  private isCurrentRequest(requestId: number, abort: AbortController): boolean {
    return (
      !this.disposed &&
      requestId === this.requestId &&
      !abort.signal.aborted
    );
  }

  private replaceSnapshot(blob: Blob, takenAt: Date): void {
    const nextUrl = this.createObjectURL(blob);
    const previousUrl = this.state.snapshotUrl;

    this.setState({
      snapshotUrl: nextUrl,
      snapshotTakenAt: takenAt.getTime(),
    });

    if (previousUrl) {
      this.revokeObjectURL(previousUrl);
    }
  }

  private clearSnapshot(): void {
    const previousUrl = this.state.snapshotUrl;

    this.setState({
      snapshotUrl: null,
      snapshotTakenAt: null,
    });

    if (previousUrl) {
      this.revokeObjectURL(previousUrl);
    }
  }

  private async probeStream(streamKey: number): Promise<void> {
    const abort = new AbortController();
    this.probeAbort = abort;

    try {
      const response = await this.fetchFrame("/api/camera/stream", {
        cache: "no-store",
        credentials: "same-origin",
        headers: { Accept: "multipart/x-mixed-replace,*/*" },
        signal: abort.signal,
      });

      if (!this.isCurrentLive(streamKey)) {
        await this.cancelBody(response);
        return;
      }

      if (response.status === 429) {
        await this.cancelBody(response);
        this.stopLive("limited");
        return;
      }

      if (!response.ok) {
        await this.cancelBody(response);
        this.beginFallback();
        return;
      }

      // Release the probe connection before arming the <img> stream. The proxy forwards this
      // abort to the backend so the probe does not keep a concurrent-stream slot.
      abort.abort();
      await this.cancelBody(response);

      if (!this.isCurrentLive(streamKey)) {
        return;
      }

      this.setState({ streamArmed: true });
    } catch {
      if (abort.signal.aborted || !this.isCurrentLive(streamKey)) {
        return;
      }

      this.beginFallback();
    } finally {
      if (this.probeAbort === abort) {
        this.probeAbort = null;
      }
    }
  }

  private async cancelBody(response: Response): Promise<void> {
    try {
      await response.body?.cancel();
    } catch {
      // The connection may already have been aborted.
    }
  }

  private isCurrentLive(streamKey: number): boolean {
    return (
      !this.disposed &&
      streamKey === this.state.streamKey &&
      isLiveFeedLive(this.state)
    );
  }

  private beginFallback(): void {
    if (!isLiveFeedLive(this.state)) {
      return;
    }

    this.setState({
      status: "live-fallback",
      streamArmed: false,
      message: "Live stream unavailable; showing snapshots instead.",
    });
    this.startFallbackPolling();
  }

  private startFallbackPolling(): void {
    if (this.fallbackTimer !== null) {
      return;
    }

    this.fallbackTimer = this.scheduler.setInterval(() => {
      if (this.disposed || this.state.status !== "live-fallback") {
        return;
      }

      void this.requestSnapshot("fallback");
    }, FALLBACK_POLL_INTERVAL_MS);
  }

  private startLiveTimer(streamKey: number): void {
    this.clearLiveTimer();

    this.liveTimer = this.scheduler.setTimeout(() => {
      if (this.isCurrentLive(streamKey)) {
        this.stopLive("timeout");
      }
    }, this.maxLiveDurationMs);
  }

  private clearLiveTimer(): void {
    if (this.liveTimer === null) {
      return;
    }

    this.scheduler.clearTimeout(this.liveTimer);
    this.liveTimer = null;
  }

  private clearFallbackPolling(): void {
    if (this.fallbackTimer === null) {
      return;
    }

    this.scheduler.clearInterval(this.fallbackTimer);
    this.fallbackTimer = null;
  }

  private clearLiveTimers(): void {
    this.clearLiveTimer();
    this.clearFallbackPolling();
  }
}
