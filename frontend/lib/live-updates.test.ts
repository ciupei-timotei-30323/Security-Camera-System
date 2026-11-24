import assert from "node:assert/strict";
import test from "node:test";
import {
  alarmTopic,
  eventsTopic,
  LiveUpdatesController,
  MAX_RECONNECT_DELAY_MS,
  RECONNECT_BASE_DELAY_MS,
  parseAlarmMessage,
  parseEventMessage,
  type LiveUpdatesScheduler,
  type LiveUpdatesState,
  type MqttClientLike,
  type MqttEventHandler,
} from "./live-updates.ts";

/**
 * Fake timer/scheduler. Mirrors the one in `live-feed.test.ts` so both suites advance time the
 * same way — no wall-clock sleeps, no flakes.
 */
function createScheduler(): LiveUpdatesScheduler & {
  advance: (milliseconds: number) => void;
  pendingDelays: () => number[];
} {
  let now = 0;
  let nextHandle = 1;
  const timers = new Map<number, { due: number; callback: () => void }>();

  const scheduler = {
    setTimeout(callback: () => void, delay: number): unknown {
      const handle = nextHandle++;
      timers.set(handle, { due: now + delay, callback });
      return handle;
    },
    clearTimeout(handle: unknown): void {
      timers.delete(handle as number);
    },
    now(): number {
      return now;
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

  return scheduler;
}

/** Minimal MQTT client stub: records subscriptions and lets the test emit events. */
class FakeMqttClient implements MqttClientLike {
  readonly handlers = new Map<string, MqttEventHandler[]>();
  readonly subscriptions: Array<{ topics: string[]; qos: number }> = [];
  ended = false;

  on(event: string, handler: MqttEventHandler): this {
    const existing = this.handlers.get(event) ?? [];
    existing.push(handler);
    this.handlers.set(event, existing);
    return this;
  }

  subscribe(topics: string[], options: { qos: 0 | 1 | 2 }): this {
    this.subscriptions.push({ topics, qos: options.qos });
    return this;
  }

  end(): this {
    this.ended = true;
    return this;
  }

  emit(event: string, ...args: unknown[]): void {
    for (const handler of this.handlers.get(event) ?? []) {
      handler(...args);
    }
  }

  emitMessage(topic: string, payload: string): void {
    this.emit("message", topic, new TextEncoder().encode(payload));
  }
}

/**
 * Forwards to the most recent `connect` result. mqtt.js returns a new client per connection, so a
 * reconnection must not re-register handlers on the old socket (that would double-schedule the
 * backoff).
 */
class LatestClient {
  private current: FakeMqttClient | null = null;

  set(client: FakeMqttClient): void {
    this.current = client;
  }

  private get inner(): FakeMqttClient {
    assert.ok(this.current, "no MQTT client has been created yet");
    return this.current;
  }

  get ended(): boolean {
    return this.inner.ended;
  }

  get subscriptions(): Array<{ topics: string[]; qos: number }> {
    return this.inner.subscriptions;
  }

  emit(event: string, ...args: unknown[]): void {
    this.inner.emit(event, ...args);
  }

  emitMessage(topic: string, payload: string): void {
    this.inner.emitMessage(topic, payload);
  }
}

interface Harness {
  controller: LiveUpdatesController;
  /** The most recent client; safe to destructure before `start()` has connected. */
  client: LatestClient;
  clients: FakeMqttClient[];
  scheduler: ReturnType<typeof createScheduler>;
  states: LiveUpdatesState[];
  connectCalls: Array<{ url: string; options: Record<string, unknown> }>;
}

function createHarness(
  ticketBody: Record<string, unknown> = {
    systemId: "sys-001",
    mqttWsUrl: "wss://mqtt.example.com/mqtt",
    mqttUsername: "dashboard",
    mqttPassword: "dashboard-pass",
  },
  ticketStatus = 200,
): Harness {
  const scheduler = createScheduler();
  const clients: FakeMqttClient[] = [];
  const client = new LatestClient();
  const states: LiveUpdatesState[] = [];
  const connectCalls: Array<{ url: string; options: Record<string, unknown> }> = [];

  const controller = new LiveUpdatesController({
    scheduler,
    onStateChange: (state) => states.push(state),
    fetch: async () =>
      new Response(JSON.stringify(ticketBody), {
        status: ticketStatus,
        headers: { "Content-Type": "application/json" },
      }),
    connect: (url, options) => {
      connectCalls.push({ url, options });
      // mqtt.js hands back a brand-new client per `connect`; a reconnection must not re-register
      // handlers on the old socket or a single close would trigger two backoff schedules.
      const created = new FakeMqttClient();
      clients.push(created);
      client.set(created);
      return created;
    },
  });

  return { controller, client, clients, scheduler, states, connectCalls };
}

/**
 * The fake scheduler's clock starts at 0, so `alarmUntil` is expressed relative to the epoch:
 * 10 s after "now", matching the backend's `Alarm:DurationSeconds`.
 */
const ALARM_DOC = {
  armed: true,
  alarm: true,
  alarmUntil: new Date(10_000).toISOString(),
  line1: "SUSPECT DETECTED",
  line2: "                ",
  serverTime: new Date(0).toISOString(),
};

/** Lets `start()`/`open()` settle when the fake timer fires an async reconnect. */
function flushAsync(): Promise<void> {
  return new Promise((resolve) => setImmediate(resolve));
}

test("topic helpers scope every message to the tenant", () => {
  assert.equal(alarmTopic("sys-001"), "systems/sys-001/alarm");
  assert.equal(eventsTopic("sys-001"), "systems/sys-001/events");
});

test("parseAlarmMessage rejects documents that are not the device-status shape", () => {
  assert.equal(parseAlarmMessage("not json"), null);
  assert.equal(parseAlarmMessage("[]"), null);
  assert.equal(parseAlarmMessage(JSON.stringify({ armed: true })), null);
  assert.equal(parseAlarmMessage(JSON.stringify({ armed: "yes", alarm: false })), null);

  const parsed = parseAlarmMessage(JSON.stringify(ALARM_DOC));
  assert.ok(parsed);
  assert.equal(parsed.armed, true);
  assert.equal(parsed.alarm, true);
  assert.equal(parsed.line1, "SUSPECT DETECTED");
  assert.equal(parsed.alarmUntil, ALARM_DOC.alarmUntil);
});

test("parseEventMessage requires a numeric eventId", () => {
  assert.equal(parseEventMessage("{}"), null);
  assert.equal(parseEventMessage(JSON.stringify({ eventId: "42" })), null);

  const parsed = parseEventMessage(
    JSON.stringify({ eventId: 42, timestamp: "2026-11-24T12:00:00Z", isSuspect: true, hasFace: false }),
  );
  assert.ok(parsed);
  assert.deepEqual(parsed, {
    eventId: 42,
    timestamp: "2026-11-24T12:00:00Z",
    isSuspect: true,
    hasFace: false,
  });
});

test("start connects with the ticket credentials and subscribes to both tenant topics", async () => {
  const { controller, client, connectCalls, states } = createHarness();

  await controller.start();

  assert.equal(connectCalls.length, 1);
  assert.equal(connectCalls[0]?.url, "wss://mqtt.example.com/mqtt");
  assert.equal(connectCalls[0]?.options.username, "dashboard");
  assert.equal(connectCalls[0]?.options.password, "dashboard-pass");
  // The controller owns the backoff: mqtt.js must never retry on its own.
  assert.equal(connectCalls[0]?.options.reconnectPeriod, 0);

  client.emit("connect");

  assert.equal(client.subscriptions.length, 1);
  assert.deepEqual(client.subscriptions[0]?.topics, [
    "systems/sys-001/alarm",
    "systems/sys-001/events",
  ]);
  assert.equal(client.subscriptions[0]?.qos, 1);

  const last = states.at(-1);
  assert.equal(last?.status, "connected");
  assert.equal(last?.systemId, "sys-001");

  controller.stop();
});

test("an alarm document updates the armed/alarm state", async () => {
  const { controller, client } = createHarness();
  await controller.start();
  client.emit("connect");

  client.emitMessage("systems/sys-001/alarm", JSON.stringify(ALARM_DOC));

  const state = controller.getSnapshot();
  assert.equal(state.armed, true);
  assert.equal(state.alarm, true);
  assert.equal(state.alarmUntil, ALARM_DOC.alarmUntil);
  assert.equal(state.line1, "SUSPECT DETECTED");
  assert.equal(state.serverTime, ALARM_DOC.serverTime);

  controller.stop();
});

test("an event summary bumps eventsReceived and stores lastEvent", async () => {
  const { controller, client } = createHarness();
  await controller.start();
  client.emit("connect");

  client.emitMessage(
    "systems/sys-001/events",
    JSON.stringify({ eventId: 7, timestamp: "2026-11-24T12:00:00Z", isSuspect: false, hasFace: true }),
  );

  const state = controller.getSnapshot();
  assert.equal(state.eventsReceived, 1);
  assert.equal(state.lastEvent?.eventId, 7);
  assert.equal(state.lastEvent?.hasFace, true);

  controller.stop();
});

test("messages for another tenant's topic are ignored", async () => {
  const { controller, client } = createHarness();
  await controller.start();
  client.emit("connect");

  client.emitMessage("systems/sys-002/alarm", JSON.stringify({ ...ALARM_DOC, armed: false }));
  client.emitMessage("systems/sys-002/events", JSON.stringify({ eventId: 99 }));

  const state = controller.getSnapshot();
  assert.equal(state.armed, null);
  assert.equal(state.eventsReceived, 0);

  controller.stop();
});

test("malformed payloads never throw and never change state", async () => {
  const { controller, client } = createHarness();
  await controller.start();
  client.emit("connect");

  client.emitMessage("systems/sys-001/alarm", "{not json");
  client.emitMessage("systems/sys-001/events", "");

  const state = controller.getSnapshot();
  assert.equal(state.armed, null);
  assert.equal(state.eventsReceived, 0);
  assert.equal(state.status, "connected");

  controller.stop();
});

test("the alarm safety timer clears the alarm after alarmUntil", async () => {
  const { controller, client, scheduler } = createHarness();
  await controller.start();
  client.emit("connect");

  // scheduler.now() is 0, so alarmUntil is 10 s away and the timer fires at +11 s.
  client.emitMessage("systems/sys-001/alarm", JSON.stringify(ALARM_DOC));
  assert.equal(controller.getSnapshot().alarm, true);

  scheduler.advance(10_999);
  assert.equal(controller.getSnapshot().alarm, true);

  scheduler.advance(1);
  const state = controller.getSnapshot();
  assert.equal(state.alarm, false);
  assert.equal(state.alarmUntil, null);

  controller.stop();
});

test("a cleared alarm document cancels the safety timer", async () => {
  const { controller, client, scheduler } = createHarness();
  await controller.start();
  client.emit("connect");

  client.emitMessage("systems/sys-001/alarm", JSON.stringify(ALARM_DOC));
  client.emitMessage(
    "systems/sys-001/alarm",
    JSON.stringify({ ...ALARM_DOC, alarm: false, alarmUntil: null, line1: "ARMED           " }),
  );

  assert.equal(scheduler.pendingDelays().length, 0);
  const state = controller.getSnapshot();
  assert.equal(state.alarm, false);
  assert.equal(state.armed, true);

  controller.stop();
});

test("a dropped connection reconnects with exponential backoff capped at 30 s", async () => {
  const { controller, client, scheduler, connectCalls } = createHarness();
  await controller.start();
  client.emit("connect");

  client.emit("close");
  assert.equal(controller.getSnapshot().status, "reconnecting");
  assert.deepEqual(scheduler.pendingDelays(), [RECONNECT_BASE_DELAY_MS]);

  scheduler.advance(RECONNECT_BASE_DELAY_MS);
  await flushAsync();
  assert.equal(connectCalls.length, 2);

  // Fail again: the delay doubles.
  client.emit("close");
  assert.deepEqual(scheduler.pendingDelays(), [RECONNECT_BASE_DELAY_MS * 2]);

  for (let i = 0; i < 10; i += 1) {
    scheduler.advance(MAX_RECONNECT_DELAY_MS);
    await flushAsync();
    client.emit("close");
  }

  const delays = scheduler.pendingDelays();
  assert.ok(delays.every((delay) => delay <= MAX_RECONNECT_DELAY_MS));

  controller.stop();
});

test("a ticket without mqttWsUrl leaves the dashboard on polling", async () => {
  const { controller, connectCalls } = createHarness({
    systemId: "sys-001",
    mqttWsUrl: "",
    mqttUsername: "",
    mqttPassword: "",
  });

  await controller.start();

  const state = controller.getSnapshot();
  assert.equal(state.status, "unavailable");
  assert.equal(state.systemId, "sys-001");
  assert.equal(connectCalls.length, 0);
});

test("a 401 from the ticket endpoint reports an expired session", async () => {
  const { controller } = createHarness({ detail: "Please sign in to continue." }, 401);

  await controller.start();

  const state = controller.getSnapshot();
  assert.equal(state.status, "unavailable");
  assert.match(state.message ?? "", /session expired/i);
});

test("stop() closes the client and ignores late events", async () => {
  const { controller, client } = createHarness();
  await controller.start();
  client.emit("connect");

  controller.stop();

  assert.equal(client.ended, true);
  assert.equal(controller.getSnapshot().status, "closed");

  client.emitMessage("systems/sys-001/alarm", JSON.stringify(ALARM_DOC));
  assert.equal(controller.getSnapshot().armed, null);
});

test("subscribers are notified on every state change", async () => {
  const { controller, client } = createHarness();
  let notifications = 0;
  controller.subscribe(() => {
    notifications += 1;
  });

  await controller.start();
  client.emit("connect");
  client.emitMessage("systems/sys-001/events", JSON.stringify({ eventId: 1 }));

  assert.ok(notifications >= 3);
});
