import { createCipheriv, createDecipheriv, createHash, randomBytes } from "node:crypto";

/**
 * Sealed BFF session cookie.
 *
 * Every camera system (Pi/backend) may sign its own JWTs with an independent key, so the BFF
 * never validates backend JWT signatures. Instead the BFF seals `{ systemId, edgeJwt, exp }`
 * into its own AES-256-GCM token. The `systemId` inside the sealed cookie is the *only*
 * trustworthy source for choosing an upstream backend.
 */

export const SESSION_COOKIE_NAME = "bff_session";

/** AES-256 needs 32 bytes; shorter secrets are rejected outright. */
export const MIN_SESSION_SECRET_BYTES = 32;

const TOKEN_VERSION = "v1";
const IV_BYTES = 12;
const TAG_BYTES = 16;

export interface BffSession {
  /** Verified camera system the session belongs to. */
  systemId: string;
  /** Backend-issued JWT forwarded upstream as `Authorization: Bearer`. */
  edgeJwt: string;
  /** Token expiry as unix seconds (mirrors the backend JWT `exp`). */
  exp: number;
}

export class SessionSecretError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "SessionSecretError";
  }
}

/**
 * Reads and validates `BFF_SESSION_SECRET`. Throws a hard error (fail fast at startup/first use)
 * when the secret is missing or shorter than 32 bytes.
 */
export function getSessionSecret(): string {
  const secret = process.env.BFF_SESSION_SECRET?.trim();

  if (!secret) {
    throw new SessionSecretError(
      "BFF_SESSION_SECRET is not configured. Set it to a random value of at least 32 bytes.",
    );
  }

  if (Buffer.byteLength(secret, "utf8") < MIN_SESSION_SECRET_BYTES) {
    throw new SessionSecretError(
      `BFF_SESSION_SECRET must be at least ${MIN_SESSION_SECRET_BYTES} bytes long.`,
    );
  }

  return secret;
}

/** Deterministic 32-byte AES key derived from an arbitrary-length secret. */
function deriveKey(secret: string): Buffer {
  return createHash("sha256").update(secret, "utf8").digest();
}

export function sealSession(session: BffSession, secret: string): string {
  const iv = randomBytes(IV_BYTES);
  const cipher = createCipheriv("aes-256-gcm", deriveKey(secret), iv);

  const payload = Buffer.from(
    JSON.stringify({
      systemId: session.systemId,
      edgeJwt: session.edgeJwt,
      exp: session.exp,
    }),
    "utf8",
  );

  const ciphertext = Buffer.concat([cipher.update(payload), cipher.final()]);
  const tag = cipher.getAuthTag();

  return [
    TOKEN_VERSION,
    iv.toString("base64url"),
    ciphertext.toString("base64url"),
    tag.toString("base64url"),
  ].join(".");
}

/**
 * Opens a sealed session token. Returns `null` for anything that is not a valid, unexpired,
 * untampered token — callers must treat `null` as "not signed in" and clear the cookie.
 */
export function openSession(
  token: string | null | undefined,
  secret: string,
  nowMs: number = Date.now(),
): BffSession | null {
  if (!token) {
    return null;
  }

  const parts = token.split(".");

  if (parts.length !== 4 || parts[0] !== TOKEN_VERSION) {
    return null;
  }

  const [, ivPart, ciphertextPart, tagPart] = parts;

  let iv: Buffer;
  let ciphertext: Buffer;
  let tag: Buffer;

  try {
    iv = Buffer.from(ivPart, "base64url");
    ciphertext = Buffer.from(ciphertextPart, "base64url");
    tag = Buffer.from(tagPart, "base64url");
  } catch {
    return null;
  }

  if (iv.length !== IV_BYTES || tag.length !== TAG_BYTES || ciphertext.length === 0) {
    return null;
  }

  let plaintext: string;

  try {
    const decipher = createDecipheriv("aes-256-gcm", deriveKey(secret), iv);
    decipher.setAuthTag(tag);
    plaintext = Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString("utf8");
  } catch {
    // Wrong key or a modified ciphertext/tag: treat as a tampered cookie.
    return null;
  }

  let parsed: unknown;

  try {
    parsed = JSON.parse(plaintext);
  } catch {
    return null;
  }

  if (typeof parsed !== "object" || parsed === null) {
    return null;
  }

  const candidate = parsed as Record<string, unknown>;
  const systemId = candidate.systemId;
  const edgeJwt = candidate.edgeJwt;
  const exp = candidate.exp;

  if (typeof systemId !== "string" || systemId.length === 0) {
    return null;
  }

  if (typeof edgeJwt !== "string" || edgeJwt.length === 0) {
    return null;
  }

  if (typeof exp !== "number" || !Number.isFinite(exp)) {
    return null;
  }

  if (exp * 1000 <= nowMs) {
    return null;
  }

  return { systemId, edgeJwt, exp };
}
