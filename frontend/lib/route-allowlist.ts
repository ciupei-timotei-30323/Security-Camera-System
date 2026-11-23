/**
 * Strict route allowlist for the BFF proxy.
 *
 * The dashboard proxy must expose *only* the read/write routes the UI needs. In particular the
 * device/ingestion surface — `POST /api/events`, `POST /api/events/analyze`,
 * `POST /api/camera/frame`, `/api/device/*` — must be unreachable from the browser even with a
 * valid session cookie, because those endpoints authenticate Raspberry Pis by API key.
 *
 * This module is dependency-free so it can be unit tested with `node --test`.
 */

const SEGMENT = /^[a-zA-Z0-9_-]{1,64}$/;
const NUMERIC_ID = /^\d{1,9}$/;

export type HttpMethod = "GET" | "POST" | "PUT" | "PATCH" | "DELETE" | "HEAD";

interface AllowlistRule {
  /** Path segments relative to `/api` (no leading `api`). Segments starting with `:` are params. */
  segments: string[];
  /** Methods permitted for this path. */
  methods: HttpMethod[];
  /** How to validate a dynamic `:id` segment. */
  idKind?: "numeric" | "segments";
}

/**
 * Every entry here is a dashboard route. Anything absent is answered with `404 Not Found`
 * before any upstream call is attempted.
 */
const RULES: AllowlistRule[] = [
  { segments: ["auth", "login"], methods: ["POST"] },
  { segments: ["auth", "logout"], methods: ["POST"] },
  { segments: ["auth", "me"], methods: ["GET"] },

  { segments: ["users"], methods: ["GET", "POST"] },
  { segments: ["users", ":id"], methods: ["GET", "DELETE"], idKind: "numeric" },

  { segments: ["events"], methods: ["GET"] },
  { segments: ["events", ":id"], methods: ["GET", "DELETE"], idKind: "numeric" },
  { segments: ["events", ":id", "face"], methods: ["GET"], idKind: "numeric" },
  { segments: ["events", ":id", "annotated"], methods: ["GET"], idKind: "numeric" },

  { segments: ["camera", "latest"], methods: ["GET"] },
  { segments: ["camera", "stream"], methods: ["GET"] },

  { segments: ["system", "status"], methods: ["GET"] },

  { segments: ["away-mode-settings"], methods: ["GET", "PUT"] },
  { segments: ["settings", "away-mode"], methods: ["GET", "PUT"] },
];

/** Routes that stream and therefore need long response/connect timeouts instead of JSON timeouts. */
const STREAMING_RULES: Array<{ segments: string[]; idKind?: "numeric" }> = [
  { segments: ["camera", "stream"] },
  { segments: ["camera", "latest"] },
  { segments: ["events", ":id", "face"], idKind: "numeric" },
  { segments: ["events", ":id", "annotated"], idKind: "numeric" },
];

export const ALLOWED_ROUTE_PATHS: ReadonlyArray<string> = RULES.flatMap((rule) =>
  rule.methods.map(
    (method) => `${method} /api/${rule.segments.join("/").replace(/:id/g, "{id}")}`,
  ),
);

function matches(rule: AllowlistRule, path: string[]): boolean {
  if (rule.segments.length !== path.length) {
    return false;
  }

  for (let index = 0; index < rule.segments.length; index += 1) {
    const expected = rule.segments[index];
    const actual = path[index];

    if (expected === ":id") {
      if (rule.idKind === "numeric" && !NUMERIC_ID.test(actual)) {
        return false;
      }

      continue;
    }

    if (expected !== actual) {
      return false;
    }
  }

  return true;
}

/**
 * Decides whether a request may be proxied.
 *
 * `path` must be the decoded path segments below `/api`. Only exact route + method pairs pass;
 * unknown paths, unknown methods and malformed parameter segments are all rejected (404).
 */
export function isRouteAllowed(method: string, path: string[] | undefined): boolean {
  if (!path || path.length === 0) {
    return false;
  }

  if (!path.every((segment) => SEGMENT.test(segment))) {
    return false;
  }

  const normalizedMethod = method.toUpperCase();

  return RULES.some(
    (rule) =>
      rule.methods.includes(normalizedMethod as HttpMethod) && matches(rule, path),
  );
}

/**
 * True when the route is one of the unbuffered streaming endpoints. Those keep the connection
 * open for as long as the browser wants (MJPEG), so no response timeout may be applied.
 */
export function isStreamingRoute(path: string[] | undefined): boolean {
  if (!path || path.length === 0) {
    return false;
  }

  return STREAMING_RULES.some((rule) => {
    if (rule.segments.length !== path.length) {
      return false;
    }

    return rule.segments.every((expected, index) => {
      if (expected === ":id") {
        return rule.idKind === "numeric" ? NUMERIC_ID.test(path[index]) : true;
      }

      return expected === path[index];
    });
  });
}
