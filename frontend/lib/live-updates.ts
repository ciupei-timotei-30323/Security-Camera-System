/**
 * Live dashboard updates over MQTT-over-WebSockets.
 *
 * The backend publishes two message families per tenant:
 *
 *   systems/{systemId}/alarm   retained, QoS 1 — same schema as `GET /api/device/status`
 *   systems/{systemId}/events  non-retained  — one summary per ingested detection
 *
 * This module owns the broker connection, the topic parsing and the exponential-backoff
 * reconnect. Everything the browser needs (the MQTT client, timers, the ticket endpoint) is
 * injected, so the whole state machine can be exercised by `node --test` without a broker.
 *
 * Degradation is explicit: when the ticket has no `mqttWsUrl` the controller reports
 * `unavailable` and the dashboard keeps its polling queries. A broker that drops lands in
 * `reconnecting` and the dashboard keeps rendering the last known state.
 */

export interface AlarmStateMessage {
  armed: boolean;
  alarm: boolean;
  alarmUntil: string | null;
  line1: string;
  line2: string;
  serverTime: string;
}

export interface EventNotification {
  eventId: number;
  timestamp: string;
  isSuspect: boolean;
  hasFace: boolean;
}

export type LiveUpdatesStatus =
  /** Not started. */
  | "idle"
  /** Ticket fetched; opening the WebSocket. */
  | "connecting"
  /** Subscribed and receiving messages. */
  | "connected"
  /** The broker dropped; a retry is scheduled. */
  | "reconnecting"
  /** No `mqttWsUrl` in the ticket — live updates are not configured. */
  | "unavailable"
  /** Stopped by the caller. */
  | "closed";

export interface LiveUpdatesState {
  status: LiveUpdatesStatus;
  /** Human-readable reason for the current status. */
  message: string | null;
  systemId: string | null;
  /** Latest retained arm state. `null` until the first alarm document arrives. */
  armed: boolean | null;
  alarm: boolean;
  alarmUntil: string | null;
  line1: string | null;
  line2: string | null;
  serverTime: string | null;
  /** Most recent detection summary from `.../events`. */
  lastEvent: EventNotification | null;
  /** Monotonic counter so the UI can react to repeated events without diffing objects. */
  eventsReceived: number;
  /** Wall-clock time the last alarm document was applied. */
  alarmReceivedAt: number | null;
}

export interface RealtimeTicketLike {
  systemId: string;
  mqttWsUrl: string;
  mqttUsername: string;
  mqttPassword: string;
}

export interface MqttMessageLike {
  topic: string;
  payload: Uint8Array | string;
}

export type MqttEventHandler = (...args: unknown[]) => void;

/** Minimal slice of `mqtt.MqttClient` the controller depends on. */
export interface MqttClientLike {
  on(event: string, handler: MqttEventHandler): unknown;
  subscribe(
    topics: string[],
    options: { qos: 0 | 1 | 2 },
    callback?: (error: Error | null) => void,
  ): unknown;
  end(force?: boolean): unknown;
}

export type MqttConnect = (
  url: string,
  options: Record<string, unknown>,
) => MqttClientLike | Promise<MqttClientLike>;

export interface LiveUpdatesScheduler {
  setTimeout(callback: () => void, delay: number): unknown;
  clearTimeout(handle: unknown): void;
  now(): number;
}

export interface LiveUpdatesOptions {
  fetch?: (input: string, init?: RequestInit) => Promise<Response>;
  connect?: MqttConnect;
  scheduler?: LiveUpdatesScheduler;
  ticketUrl?: string;
  /** First reconnect delay; doubles up to {@link MAX_RECONNECT_DELAY_MS}. */
  reconnectBaseDelayMs?: number;
  onStateChange?: (state: LiveUpdatesState) => void;
}

export const RECONNECT_BASE_DELAY_MS = 1_000;
export const MAX_RECONNECT_DELAY_MS = 30_000;

export function alarmTopic(systemId: string): string {
  return `systems/${systemId}/alarm`;
}

export function eventsTopic(systemId: string): string {
  return `systems/${systemId}/events`;
}

const defaultScheduler: LiveUpdatesScheduler = {
  setTimeout: (callback, delay) => globalThis.setTimeout(callback, delay),
  clearTimeout: (handle) =>
    globalThis.clearTimeout(handle as ReturnType<typeof globalThis.setTimeout>),
  now: () => Date.now(),
};

const defaultFetch = (input: string, init?: RequestInit) => globalThis.fetch(input, init);

/**
 * Lazily loads `mqtt` so the module stays importable in Node tests (and so the browser build is
 * only pulled in when a dashboard actually subscribes).
 */
const defaultConnect: MqttConnect = async (url, options) => {
  const mqtt = await import("mqtt");
  return mqtt.connect(url, options) as unknown as MqttClientLike;
};

function decodePayload(payload: Uint8Array | string): string {
  if (typeof payload === "string") {
    return payload;
  }

  return new TextDecoder().decode(payload);
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null ? (value as Record<string, unknown>) : null;
}

function asBoolean(value: unknown): boolean | null {
  return typeof value === "boolean" ? value : null;
}

function asString(value: unknown): string | null {
  return typeof value === "string" ? value : null;
}

/** Parses the retained alarm document. Returns `null` for anything that is not that shape. */
export function parseAlarmMessage(payload: string): AlarmStateMessage | null {
  let parsed: unknown;

  try {
    parsed = JSON.parse(payload);
  } catch {
    return null;
  }

  const record = asRecord(parsed);

  if (!record) {
    return null;
  }

  const armed = asBoolean(record.armed);
  const alarm = asBoolean(record.alarm);

  if (armed === null || alarm === null) {
    return null;
  }

  return {
    armed,
    alarm,
    alarmUntil: asString(record.alarmUntil),
    line1: asString(record.line1) ?? "",
    line2: asString(record.line2) ?? "",
    serverTime: asString(record.serverTime) ?? "",
  };
}

/** Parses the detection summary. Returns `null` for anything that is not that shape. */
export function parseEventMessage(payload: string): EventNotification | null {
  let parsed: unknown;

  try {
    parsed = JSON.parse(payload);
  } catch {
    return null;
  }

  const record = asRecord(parsed);

  if (!record) {
    return null;
  }

  const eventId = record.eventId;

  if (typeof eventId !== "number" || !Number.isFinite(eventId)) {
    return null;
  }

  return {
    eventId,
    timestamp: asString(record.timestamp) ?? "",
    isSuspect: asBoolean(record.isSuspect) ?? false,
    hasFace: asBoolean(record.hasFace) ?? false,
  };
}

/**
 * Owns the dashboard's broker session: ticket -> connect -> subscribe -> state -> backoff.
 *
 * Call {@link start} once the dashboard mounts and {@link stop} on unmount. Every failure is
 * reported through {@link LiveUpdatesState} instead of throwing, so a missing broker can never
 * break the page.
 */
export class LiveUpdatesController {
  private readonly fetchImpl: NonNullable<LiveUpdatesOptions["fetch"]>;
  private readonly connect: MqttConnect;
  private readonly scheduler: LiveUpdatesScheduler;
  private readonly ticketUrl: string;
  private readonly reconnectBaseDelayMs: number;
  private readonly onStateChange?: (state: LiveUpdatesState) => void;

  private readonly listeners = new Set<() => void>();

  private client: MqttClientLike | null = null;
  private stopped = false;
  private reconnectTimer: unknown = null;
  private alarmTimer: unknown = null;
  private reconnectDelayMs: number;
  private state: LiveUpdatesState = {
    status: "idle",
    message: null,
    systemId: null,
    armed: null,
    alarm: false,
    alarmUntil: null,
    line1: null,
    line2: null,
    serverTime: null,
    lastEvent: null,
    eventsReceived: 0,
    alarmReceivedAt: null,
  };

  constructor(options: LiveUpdatesOptions = {}) {
    this.fetchImpl = options.fetch ?? defaultFetch;
    this.connect = options.connect ?? defaultConnect;
    this.scheduler = options.scheduler ?? defaultScheduler;
    this.ticketUrl = options.ticketUrl ?? "/api/realtime/ticket";
    this.reconnectBaseDelayMs = options.reconnectBaseDelayMs ?? RECONNECT_BASE_DELAY_MS;
    this.reconnectDelayMs = this.reconnectBaseDelayMs;
    this.onStateChange = options.onStateChange;
  }

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);

    return () => {
      this.listeners.delete(listener);
    };
  };

  getSnapshot = (): LiveUpdatesState => this.state;

  /** Fetches the ticket and opens the broker connection. Safe to call twice. */
  async start(): Promise<void> {
    if (this.stopped || this.state.status === "connecting" || this.state.status === "connected") {
      return;
    }

    this.setState({ status: "connecting", message: null });

    let ticket: RealtimeTicketLike;

    try {
      ticket = await this.fetchTicket();
    } catch (error) {
      this.setState({
        status: "unavailable",
        message: error instanceof Error ? error.message : "Live updates are unavailable.",
      });
      return;
    }

    if (this.stopped) {
      return;
    }

    if (!ticket.mqttWsUrl) {
      this.setState({
        status: "unavailable",
        systemId: ticket.systemId,
        message: "Live updates are not configured; the dashboard keeps polling.",
      });
      return;
    }

    this.setState({ systemId: ticket.systemId, status: "connecting" });
    await this.open(ticket);
  }

  /** Tears the connection down and stops reconnecting. */
  stop(): void {
    this.stopped = true;
    this.clearReconnectTimer();
    this.clearAlarmTimer();

    const client = this.client;
    this.client = null;

    if (client) {
      try {
        client.end(true);
      } catch {
        // A client that is already closed is harmless.
      }
    }

    this.setState({ status: "closed", message: null });
    this.listeners.clear();
  }

  /**
   * Applies one broker message. Public so tests can drive the reducer without a socket; the
   * socket handler below is a thin wrapper.
   */
  handleMessage(topic: string, payload: string): void {
    if (this.stopped) {
      return;
    }

    const systemId = this.state.systemId;

    if (!systemId) {
      return;
    }

    if (topic === alarmTopic(systemId)) {
      const alarm = parseAlarmMessage(payload);

      if (!alarm) {
        return;
      }

      this.applyAlarm(alarm);
      return;
    }

    if (topic === eventsTopic(systemId)) {
      const event = parseEventMessage(payload);

      if (!event) {
        return;
      }

      this.setState({
        lastEvent: event,
        eventsReceived: this.state.eventsReceived + 1,
      });
    }
  }

  private async fetchTicket(): Promise<RealtimeTicketLike> {
    const response = await this.fetchImpl(this.ticketUrl, {
      method: "GET",
      headers: { accept: "application/json" },
      cache: "no-store",
      credentials: "same-origin",
    });

    if (response.status === 401) {
      throw new Error("Your session expired. Sign in again for live updates.");
    }

    if (!response.ok) {
      throw new Error(`Realtime ticket request failed (HTTP ${response.status}).`);
    }

    const body = (await response.json()) as Partial<RealtimeTicketLike>;

    if (typeof body.systemId !== "string" || body.systemId.length === 0) {
      throw new Error("The realtime ticket is missing the system id.");
    }

    return {
      systemId: body.systemId,
      mqttWsUrl: typeof body.mqttWsUrl === "string" ? body.mqttWsUrl : "",
      mqttUsername: typeof body.mqttUsername === "string" ? body.mqttUsername : "",
      mqttPassword: typeof body.mqttPassword === "string" ? body.mqttPassword : "",
    };
  }

  private async open(ticket: RealtimeTicketLike): Promise<void> {
    let client: MqttClientLike;

    try {
      client = await this.connect(ticket.mqttWsUrl, {
        clientId: `dashboard-${ticket.systemId}-${Math.random().toString(16).slice(2, 10)}`,
        username: ticket.mqttUsername || undefined,
        password: ticket.mqttPassword || undefined,
        clean: true,
        protocolVersion: 4,
        keepalive: 30,
        connectTimeout: 10_000,
        // Backoff is owned by this controller, so mqtt.js must not retry on its own.
        reconnectPeriod: 0,
        resubscribe: false,
      });
    } catch (error) {
      this.scheduleReconnect(error instanceof Error ? error.message : "MQTT connect failed.");
      return;
    }

    if (this.stopped) {
      try {
        client.end(true);
      } catch {
        // ignore
      }
      return;
    }

    this.client = client;

    client.on("connect", () => {
      this.reconnectDelayMs = this.reconnectBaseDelayMs;
      this.setState({ status: "connected", message: null });

      client.subscribe(
        [alarmTopic(ticket.systemId), eventsTopic(ticket.systemId)],
        { qos: 1 },
        (error) => {
          if (error) {
            this.setState({
              status: "reconnecting",
              message: `MQTT subscribe failed: ${error.message}`,
            });
          }
        },
      );
    });

    client.on("message", (topic, payload) => {
      if (typeof topic !== "string") {
        return;
      }

      this.handleMessage(topic, decodePayload(payload as Uint8Array | string));
    });

    client.on("error", (error) => {
      const message = error instanceof Error ? error.message : String(error);
      this.setState({ message });
    });

    client.on("close", () => {
      if (this.stopped) {
        return;
      }

      this.scheduleReconnect("The live update connection dropped.");
    });
  }

  private scheduleReconnect(message: string): void {
    if (this.stopped) {
      return;
    }

    this.clearReconnectTimer();
    this.setState({ status: "reconnecting", message });

    const delay = this.reconnectDelayMs;
    this.reconnectDelayMs = Math.min(MAX_RECONNECT_DELAY_MS, this.reconnectDelayMs * 2);

    this.reconnectTimer = this.scheduler.setTimeout(() => {
      this.reconnectTimer = null;

      if (this.stopped) {
        return;
      }

      const client = this.client;
      this.client = null;

      if (client) {
        try {
          client.end(true);
        } catch {
          // ignore
        }
      }

      void this.start();
    }, delay);
  }

  private applyAlarm(alarm: AlarmStateMessage): void {
    this.clearAlarmTimer();

    this.setState({
      armed: alarm.armed,
      alarm: alarm.alarm,
      alarmUntil: alarm.alarmUntil,
      line1: alarm.line1,
      line2: alarm.line2,
      serverTime: alarm.serverTime,
      alarmReceivedAt: this.scheduler.now(),
      message: null,
    });

    // The backend publishes the cleared document when the window expires. This timer is only a
    // safety net for a broker that dropped the cleared message: the UI must never show a stale
    // alarm banner forever.
    if (alarm.alarm && alarm.alarmUntil) {
      const until = Date.parse(alarm.alarmUntil);

      if (Number.isFinite(until)) {
        const delay = Math.max(0, until - this.scheduler.now()) + 1_000;

        this.alarmTimer = this.scheduler.setTimeout(() => {
          this.alarmTimer = null;
          this.setState({ alarm: false, alarmUntil: null });
        }, delay);
      }
    }
  }

  private clearAlarmTimer(): void {
    if (this.alarmTimer !== null) {
      this.scheduler.clearTimeout(this.alarmTimer);
      this.alarmTimer = null;
    }
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer !== null) {
      this.scheduler.clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }

  private setState(patch: Partial<LiveUpdatesState>): void {
    this.state = { ...this.state, ...patch };
    this.onStateChange?.(this.state);

    for (const listener of this.listeners) {
      listener();
    }
  }
}
