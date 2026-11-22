import assert from "node:assert/strict";
import test from "node:test";
import {
  FALLBACK_POLL_INTERVAL_MS,
  LiveFeedController,
  MAX_LIVE_DURATION_MS,
  type LiveFeedFetch,
  type LiveFeedScheduler,
} from "../lib/live-feed-controller.ts";

type FakeTimer = {
  handle: number;
  due: number;
  interval: number | null;
  callback: () => void;
};

function createScheduler(): LiveFeedScheduler & {
  advance: (milliseconds: number) => void;
  timerCount: () => number;
  intervalCount: () => number;
} {
  let now = 0;
  let nextHandle = 1;
  const timers = new Map<number, FakeTimer>();

  const scheduler = {
    setTimeout(callback: () => void, delay: number): unknown {
      const handle = nextHandle++;
      timers.set(handle, { handle, due: now + delay, interval: null, callback });
      return handle;
    },
    clearTimeout(handle: unknown): void {
      timers.delete(handle as number);
    },
    setInterval(callback: () => void, delay: number): unknown {
      const handle = nextHandle++;
      timers.set(handle, { handle, due: now + delay, interval: delay, callback });
      return handle;
    },
    clearInterval(handle: unknown): void {
      timers.delete(handle as number);
    },
    advance(milliseconds: number): void {
      const target = now + milliseconds;

      while (true) {
        let next: FakeTimer | null = null;

        for (const timer of timers.values()) {
          if (timer.due > target) {
            continue;
          }

          if (!next || timer.due < next.due || (timer.due === next.due && timer.handle < next.handle)) {
            next = timer;
          }
        }

        if (!next) {
          break;
        }

        now = next.due;

        if (next.interval === null) {
          timers.delete(next.handle);
        } else {
          next.due += next.interval;
        }

        next.callback();
      }

      now = target;
    },
    timerCount(): number {
      return timers.size;
    },
    intervalCount(): number {
      return Array.from(timers.values()).filter((timer) => timer.interval !== null).length;
    },
  };

  return scheduler;
}

async function flush(times = 8): Promise<void> {
  for (let index = 0; index < times; index += 1) {
    await new Promise<void>((resolve) => setImmediate(resolve));
  }
}

function jpegResponse(content: string): Response {
  return new Response(new Blob([content], { type: "image/jpeg" }), {
    status: 200,
    headers: { "Content-Type": "image/jpeg" },
  });
}

function createBackend(options: {
  streamStatus?: number;
  streamError?: boolean;
  snapshot?: (requestNumber: number) => Response;
} = {}) {
  const calls: Array<{ url: string; init?: RequestInit }> = [];
  let snapshotRequests = 0;

  const fetch: LiveFeedFetch = async (url, init) => {
    calls.push({ url, init });

    if (url === "/api/camera/latest") {
      snapshotRequests += 1;

      return options.snapshot
        ? options.snapshot(snapshotRequests)
        : jpegResponse(`snapshot-${snapshotRequests}`);
    }

    if (url === "/api/camera/stream") {
      if (options.streamError) {
        throw new TypeError("network error");
      }

      return new Response(null, { status: options.streamStatus ?? 200 });
    }

    throw new Error(`Unexpected request: ${url}`);
  };

  return {
    fetch,
    calls,
    snapshotRequests: () => snapshotRequests,
    streamRequests: () => calls.filter((call) => call.url === "/api/camera/stream").length,
  };
}

function createHarness(options: {
  streamStatus?: number;
  streamError?: boolean;
  snapshot?: (requestNumber: number) => Response;
  maxLiveDurationMs?: number;
} = {}) {
  const scheduler = createScheduler();
  const backend = createBackend(options);
  const createdUrls: string[] = [];
  const revokedUrls: string[] = [];
  let captureCalls = 0;
  let nextUrl = 1;

  const controller = new LiveFeedController({
    fetch: backend.fetch,
    scheduler,
    maxLiveDurationMs: options.maxLiveDurationMs,
    now: () => new Date(1_700_000_000_000 + createdUrls.length * 1_000),
    createObjectURL: () => {
      const url = `blob:test-${nextUrl++}`;
      createdUrls.push(url);
      return url;
    },
    revokeObjectURL: (url) => {
      revokedUrls.push(url);
    },
    captureFrame: async () => {
      captureCalls += 1;
      return new Blob(["last-frame"], { type: "image/jpeg" });
    },
  });

  return {
    controller,
    scheduler,
    backend,
    createdUrls,
    revokedUrls,
    captureCalls: () => captureCalls,
  };
}

async function startWithSnapshot(
  harness: ReturnType<typeof createHarness>,
): Promise<void> {
  harness.controller.start();
  await flush();
  assert.equal(harness.controller.getSnapshot().status, "snapshot");
}

async function enterLive(harness: ReturnType<typeof createHarness>): Promise<void> {
  await harness.controller.startLive();
  await flush();
  harness.controller.handleStreamFrame();
  assert.equal(harness.controller.getSnapshot().status, "live");
}

test("loads exactly one snapshot on start and does not schedule polling", async () => {
  const harness = createHarness();

  harness.controller.start();
  await flush();

  const state = harness.controller.getSnapshot();

  assert.equal(harness.backend.snapshotRequests(), 1);
  assert.equal(harness.backend.streamRequests(), 0);
  assert.equal(harness.backend.calls[0].url, "/api/camera/latest");
  assert.equal(harness.backend.calls[0].init?.cache, "no-store");
  assert.equal(state.status, "snapshot");
  assert.equal(state.snapshotLoading, false);
  assert.ok(state.snapshotUrl);
  assert.equal(typeof state.snapshotTakenAt, "number");
  assert.equal(harness.scheduler.timerCount(), 0);
});

test("a 204 snapshot shows offline and refresh makes one request per click", async () => {
  const harness = createHarness({
    snapshot: () => new Response(null, { status: 204 }),
  });

  harness.controller.start();
  await flush();

  assert.equal(harness.controller.getSnapshot().status, "offline");
  assert.equal(harness.controller.getSnapshot().snapshotUrl, null);

  harness.controller.refreshSnapshot();
  await flush();

  assert.equal(harness.backend.snapshotRequests(), 2);
  assert.equal(harness.controller.getSnapshot().status, "offline");
  assert.equal(harness.scheduler.timerCount(), 0);
});

test("snapshot -> live -> stopped by the Stop button", async () => {
  const harness = createHarness();

  await startWithSnapshot(harness);
  const firstSnapshotUrl = harness.controller.getSnapshot().snapshotUrl;

  await enterLive(harness);

  assert.equal(harness.backend.streamRequests(), 1);
  assert.equal(harness.controller.getSnapshot().streamArmed, true);

  harness.controller.stopLive("user");
  const stopped = harness.controller.getSnapshot();

  assert.equal(stopped.status, "snapshot");
  assert.equal(stopped.streamArmed, false);
  assert.equal(stopped.message, null);
  assert.equal(harness.captureCalls(), 1);

  await flush();

  assert.notEqual(harness.controller.getSnapshot().snapshotUrl, firstSnapshotUrl);
  assert.ok(harness.revokedUrls.includes(firstSnapshotUrl ?? ""));
  assert.equal(harness.scheduler.timerCount(), 0);
});

test("snapshot -> live -> stopped when the tab becomes hidden", async () => {
  const harness = createHarness();

  await startWithSnapshot(harness);
  await enterLive(harness);

  harness.controller.handleVisibilityChange(true);
  const hidden = harness.controller.getSnapshot();

  assert.equal(hidden.status, "paused-hidden");
  assert.equal(hidden.streamArmed, false);
  assert.equal(harness.scheduler.timerCount(), 0);

  await flush();

  harness.controller.handleVisibilityChange(false);

  assert.equal(harness.controller.getSnapshot().status, "paused-hidden");
  assert.equal(harness.captureCalls(), 1);

  await harness.controller.startLive();
  await flush();

  assert.equal(harness.controller.getSnapshot().status, "live-connecting");
});

test("snapshot -> live -> stopped by the 5 minute timeout", async () => {
  const harness = createHarness();

  await startWithSnapshot(harness);
  await enterLive(harness);

  harness.scheduler.advance(MAX_LIVE_DURATION_MS - 1);
  assert.equal(harness.controller.getSnapshot().status, "live");

  harness.scheduler.advance(1);

  const timedOut = harness.controller.getSnapshot();

  assert.equal(timedOut.status, "paused-timeout");
  assert.equal(timedOut.streamArmed, false);
  assert.equal(harness.scheduler.timerCount(), 0);

  await flush();

  assert.equal(harness.captureCalls(), 1);
  assert.ok(harness.controller.getSnapshot().snapshotUrl);
});

test("a 429 stream request stays in snapshot mode with a viewer-limit message", async () => {
  const harness = createHarness({ streamStatus: 429 });

  await startWithSnapshot(harness);
  await harness.controller.startLive();
  await flush();

  const state = harness.controller.getSnapshot();

  assert.equal(state.status, "snapshot");
  assert.equal(state.streamArmed, false);
  assert.equal(state.message, "Too many viewers, try again shortly");
  assert.equal(harness.scheduler.timerCount(), 0);
});

test("stream errors fall back to 1 s polling only while live", async () => {
  const harness = createHarness();

  await startWithSnapshot(harness);
  await enterLive(harness);

  harness.controller.handleStreamError();

  assert.equal(harness.controller.getSnapshot().status, "live-fallback");
  assert.equal(harness.controller.getSnapshot().streamArmed, false);
  assert.equal(harness.scheduler.intervalCount(), 1);

  const requestsBeforePoll = harness.backend.snapshotRequests();
  harness.scheduler.advance(FALLBACK_POLL_INTERVAL_MS);
  await flush();

  assert.equal(harness.backend.snapshotRequests(), requestsBeforePoll + 1);

  harness.controller.stopLive("user");
  assert.equal(harness.scheduler.timerCount(), 0);

  const requestsAfterStop = harness.backend.snapshotRequests();
  harness.scheduler.advance(FALLBACK_POLL_INTERVAL_MS * 5);
  await flush();

  assert.equal(harness.backend.snapshotRequests(), requestsAfterStop);
});

test("unmounting stops timers, aborts the stream probe and revokes the snapshot URL", async () => {
  const harness = createHarness();

  await startWithSnapshot(harness);
  const snapshotUrl = harness.controller.getSnapshot().snapshotUrl;

  harness.controller.startLive();
  await flush();
  harness.controller.dispose();

  assert.equal(harness.scheduler.timerCount(), 0);
  assert.ok(harness.revokedUrls.includes(snapshotUrl ?? ""));
  assert.equal(harness.controller.getSnapshot().snapshotUrl, null);
});
