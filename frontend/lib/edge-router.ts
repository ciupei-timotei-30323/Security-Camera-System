/**
 * Private edge resolver + request hygiene helpers.
 *
 * The backend is never assumed to have a public URL: the BFF picks an upstream target strictly
 * from a verified `systemId` (taken from the sealed session cookie), using `EDGE_BACKENDS_JSON`
 * (private Tailscale/LAN/Docker URLs) with `BACKEND_URL` as the single-backend fallback.
 *
 * This module is dependency-free (no `next/*`, no `react`) so it can be unit tested directly with
 * `node --test`.
 */

/** `systemId` values are opaque, short and URL-safe. Never derived from user input without this check. */
export const SYSTEM_ID_PATTERN = /^[a-zA-Z0-9_-]{1,64}$/;

/**
 * Request headers the BFF is willing to forward. Everything else — notably `Authorization`,
 * `X-Api-Key`, `X-Internal-Secret`, cookies and any client-supplied forwarding header — is dropped.
 */
export const FORWARDED_REQUEST_HEADERS = [
  "accept",
  "accept-language",
  "content-type",
  "range",
  "if-none-match",
  "if-modified-since",
] as const;

/**
 * Headers carrying the real client address. Only honoured when <c>TRUST_PROXY_HEADERS</c> is
 * enabled, because every one of them is client-controlled on a direct connection.
 */
const PROXY_CLIENT_IP_HEADERS = ["cf-connecting-ip", "x-real-ip"] as const;

/** Headers that must never reach an upstream backend even if a caller adds them later. */
export const STRIPPED_REQUEST_HEADERS = [
  "authorization",
  "cookie",
  "x-api-key",
  "x-internal-secret",
  "host",
  "connection",
  "content-length",
  "transfer-encoding",
  "x-forwarded-for",
  "x-forwarded-host",
  "x-forwarded-proto",
  "x-real-ip",
] as const;

const SAFE_METHODS = new Set(["GET", "HEAD", "OPTIONS"]);

export class UnknownSystemError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "UnknownSystemError";
  }
}

export class BackendNotConfiguredError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "BackendNotConfiguredError";
  }
}

export function isValidSystemId(value: unknown): value is string {
  return typeof value === "string" && SYSTEM_ID_PATTERN.test(value);
}

function normalizeBaseUrl(rawUrl: string, source: string): string {
  const candidate = rawUrl.trim().replace(/\/+$/, "");

  if (!candidate) {
    throw new BackendNotConfiguredError(`${source} is empty for this camera system.`);
  }

  let url: URL;

  try {
    url = new URL(candidate);
  } catch {
    throw new BackendNotConfiguredError(`${source} ('${candidate}') is not a valid URL.`);
  }

  if (url.protocol !== "http:" && url.protocol !== "https:") {
    throw new BackendNotConfiguredError(`${source} must use http or https.`);
  }

  return candidate;
}

/**
 * Parses `EDGE_BACKENDS_JSON` (e.g. `{"sys-001":"http://100.64.0.10:8080"}`). Invalid keys or
 * unusable URLs are dropped rather than trusted; a malformed document yields an empty map.
 */
export function parseEdgeBackends(raw: string | undefined | null): Record<string, string> {
  if (!raw || !raw.trim()) {
    return {};
  }

  let parsed: unknown;

  try {
    parsed = JSON.parse(raw);
  } catch {
    return {};
  }

  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    return {};
  }

  const backends: Record<string, string> = {};

  for (const [systemId, url] of Object.entries(parsed as Record<string, unknown>)) {
    if (!isValidSystemId(systemId) || typeof url !== "string") {
      continue;
    }

    try {
      backends[systemId] = normalizeBaseUrl(url, `EDGE_BACKENDS_JSON['${systemId}']`);
    } catch {
      // Ignore broken entries instead of failing every request.
    }
  }

  return backends;
}

export function getEdgeBackends(): Record<string, string> {
  return parseEdgeBackends(process.env.EDGE_BACKENDS_JSON);
}

/** Single-backend fallback used for local demos or a single-Pi deployment. */
export function getDefaultBackendUrl(): string | null {
  const raw = process.env.BACKEND_URL;

  if (!raw || !raw.trim()) {
    return null;
  }

  return normalizeBaseUrl(raw, "BACKEND_URL");
}

/** All private backends the BFF may talk to. Never includes a public hostname by design. */
export function getCandidateBackends(): Record<string, string> {
  const backends = getEdgeBackends();
  const fallback = getDefaultBackendUrl();

  if (fallback && Object.keys(backends).length === 0) {
    return { [process.env.BACKEND_DEFAULT_SYSTEM_ID?.trim() || "default"]: fallback };
  }

  return backends;
}

/**
 * Resolves the private upstream URL for a *verified* system id.
 *
 * Throws `UnknownSystemError` when the id is malformed or unmapped (callers answer 403), and
 * `BackendNotConfiguredError` when no backend at all is configured (callers answer 500).
 */
export function resolveBackendUrl(systemId: string): string {
  if (!isValidSystemId(systemId)) {
    throw new UnknownSystemError(`Invalid camera system id '${systemId}'.`);
  }

  const backends = getEdgeBackends();
  const mapped = backends[systemId];

  if (mapped) {
    return mapped;
  }

  const fallback = getDefaultBackendUrl();

  if (fallback) {
    return fallback;
  }

  if (Object.keys(backends).length > 0) {
    throw new UnknownSystemError(`Camera system '${systemId}' has no configured backend.`);
  }

  throw new BackendNotConfiguredError(
    "No backend is configured. Set EDGE_BACKENDS_JSON or BACKEND_URL.",
  );
}

export function getConfiguredOrigins(): string[] {
  return (process.env.ALLOWED_ORIGINS ?? "")
    .split(",")
    .map((value) => value.trim().replace(/\/+$/, ""))
    .filter((value) => value.length > 0);
}

export function normalizeHost(value: string | null | undefined): string | null {
  if (!value) {
    return null;
  }

  const first = value.split(",")[0]?.trim();

  if (!first) {
    return null;
  }

  try {
    return new URL(`http://${first}`).host.toLowerCase();
  } catch {
    return null;
  }
}

/**
 * CSRF / origin policy for state-changing requests. The browser must send an `Origin` that
 * matches the host it is talking to (or an explicitly allowlisted origin).
 */
export function isOriginAcceptable(input: {
  method: string;
  origin: string | null;
  host: string | null;
  forwardedHost: string | null;
  allowedOrigins?: string[];
}): boolean {
  const method = input.method.toUpperCase();

  if (SAFE_METHODS.has(method)) {
    return true;
  }

  const origin = input.origin?.trim();

  // No Origin, or the opaque "null" origin (file:, sandboxed iframe): reject.
  if (!origin || origin === "null") {
    return false;
  }

  let originUrl: URL;

  try {
    originUrl = new URL(origin);
  } catch {
    return false;
  }

  if (originUrl.protocol !== "http:" && originUrl.protocol !== "https:") {
    return false;
  }

  const normalizedOrigin = `${originUrl.protocol}//${originUrl.host}`.toLowerCase();
  const allowed = (input.allowedOrigins ?? getConfiguredOrigins()).map((value) =>
    value.replace(/\/+$/, "").toLowerCase(),
  );

  if (allowed.includes(normalizedOrigin)) {
    return true;
  }

  const originHost = originUrl.host.toLowerCase();

  return originHost === normalizeHost(input.host) || originHost === normalizeHost(input.forwardedHost);
}

export function isRequestOriginAllowed(
  request: Request,
  allowedOrigins?: string[],
): boolean {
  return isOriginAcceptable({
    method: request.method,
    origin: request.headers.get("origin"),
    host: request.headers.get("host"),
    forwardedHost: request.headers.get("x-forwarded-host"),
    allowedOrigins,
  });
}

/**
 * Builds the upstream request headers: a strict allowlist plus the backend-issued JWT. Any
 * client-supplied `Authorization`, `X-Api-Key` or `X-Internal-Secret` is impossible to leak
 * through, because headers are copied one-by-one from {@link FORWARDED_REQUEST_HEADERS}.
 */
export function buildUpstreamHeaders(request: Request, edgeJwt: string): Headers {
  const headers = new Headers();

  for (const name of FORWARDED_REQUEST_HEADERS) {
    const value = request.headers.get(name);

    if (value) {
      headers.set(name, value);
    }
  }

  for (const name of STRIPPED_REQUEST_HEADERS) {
    headers.delete(name);
  }

  headers.set("Authorization", `Bearer ${edgeJwt}`);

  // The backend rate limits logins per client IP; without this every request would look like it
  // came from the dashboard container. Only forwarded when the deployment opts in, since these
  // headers are spoofable unless a trusted proxy (Cloudflare, Traefik, ...) overwrites them.
  const clientIp = getClientIp(request);

  if (clientIp) {
    headers.set("X-Forwarded-For", clientIp);
  }

  return headers;
}

/**
 * Best-effort client address, or `null` when no trusted proxy is configured. Returning `null`
 * (rather than a guess) keeps the default posture honest: an untrusted header must not be able to
 * influence the upstream rate-limit partition key.
 */
export function getClientIp(request: Request): string | null {
  if (!trustProxyHeaders()) {
    return null;
  }

  for (const name of PROXY_CLIENT_IP_HEADERS) {
    const value = request.headers.get(name)?.trim();

    if (value) {
      return value;
    }
  }

  // X-Forwarded-For is a list; the first entry is the original client for single-proxy setups.
  const forwardedFor = request.headers.get("x-forwarded-for");
  const firstHop = forwardedFor?.split(",", 1)[0]?.trim();

  return firstHop ? firstHop : null;
}

function trustProxyHeaders(): boolean {
  const flag = process.env.TRUST_PROXY_HEADERS?.trim().toLowerCase();

  return flag === "true" || flag === "1" || flag === "yes";
}
