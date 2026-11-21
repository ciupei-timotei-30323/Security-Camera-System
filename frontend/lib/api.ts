import type { LoginResult, ProblemDetails } from "@/lib/types";

export class ApiError extends Error {
  readonly status: number;
  readonly code?: string;
  readonly detail?: string;

  constructor(message: string, status: number, code?: string, detail?: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.detail = detail;
  }
}

function redirectToLogin(): void {
  if (typeof window === "undefined") {
    return;
  }

  if (window.location.pathname === "/login") {
    return;
  }

  const next = `${window.location.pathname}${window.location.search}`;
  const loginUrl = new URL("/login", window.location.origin);
  loginUrl.searchParams.set("reason", "expired");
  loginUrl.searchParams.set("next", next);
  window.location.assign(loginUrl.toString());
}

async function readBody(response: Response): Promise<unknown> {
  const text = await response.text();

  if (!text) {
    return null;
  }

  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

export async function toApiError(response: Response): Promise<ApiError> {
  const body = (await readBody(response)) as ProblemDetails | string | null;

  if (typeof body === "string") {
    return new ApiError(body || `Request failed (${response.status})`, response.status);
  }

  return new ApiError(
    body?.title || `Request failed (${response.status})`,
    response.status,
    body?.code,
    body?.detail,
  );
}

type ApiFetchOptions = RequestInit & { skipAuthRedirect?: boolean };

export async function apiFetch<T>(path: string, options: ApiFetchOptions = {}): Promise<T> {
  const { skipAuthRedirect, headers, ...init } = options;

  const response = await fetch(path, {
    ...init,
    credentials: "same-origin",
    cache: "no-store",
    headers: {
      Accept: "application/json",
      ...headers,
    },
  });

  if (response.status === 401 && !skipAuthRedirect) {
    redirectToLogin();
  }

  if (!response.ok) {
    throw await toApiError(response);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

export async function apiBlob(path: string): Promise<Blob | null> {
  const response = await fetch(path, {
    credentials: "same-origin",
    cache: "no-store",
    headers: { Accept: "image/jpeg" },
  });

  if (response.status === 401) {
    redirectToLogin();
    throw await toApiError(response);
  }

  if (response.status === 204) {
    return null;
  }

  if (!response.ok) {
    throw await toApiError(response);
  }

  return response.blob();
}

export async function loginRequest(input: {
  username: string;
  password: string;
  systemId?: string;
}): Promise<LoginResult> {
  const response = await fetch("/api/auth/login", {
    method: "POST",
    credentials: "same-origin",
    cache: "no-store",
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify(input),
  });

  if (!response.ok) {
    throw await toApiError(response);
  }

  return (await response.json()) as LoginResult;
}

export async function logoutRequest(): Promise<void> {
  const response = await fetch("/api/auth/logout", {
    method: "POST",
    credentials: "same-origin",
    cache: "no-store",
    headers: { Accept: "application/json" },
  });

  if (!response.ok && response.status !== 401) {
    throw await toApiError(response);
  }
}
