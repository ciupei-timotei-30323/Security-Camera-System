"use client";

import { useRouter } from "next/navigation";
import {
  CameraIcon,
  EyeIcon,
  EyeOffIcon,
  Loader2Icon,
  LockKeyholeIcon,
  ShieldAlertIcon,
} from "lucide-react";
import { FormEvent, useEffect, useRef, useState } from "react";
import { ThemeToggle } from "@/components/theme-toggle";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ApiError, loginRequest } from "@/lib/api";

export function LoginForm({
  expired,
  nextPath,
}: {
  expired: boolean;
  nextPath: string | null;
}) {
  const router = useRouter();
  const systemIdRef = useRef<HTMLInputElement>(null);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [systemId, setSystemId] = useState("");
  const [systemIdRequired, setSystemIdRequired] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (systemIdRequired) {
      systemIdRef.current?.focus();
    }
  }, [systemIdRequired]);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);

    const normalizedUsername = username.trim();
    const normalizedSystemId = systemId.trim();

    if (!normalizedUsername || !password) {
      setError("Enter your username and password.");
      return;
    }

    if (systemIdRequired && !normalizedSystemId) {
      setError("Enter the Camera System ID for your camera system.");
      systemIdRef.current?.focus();
      return;
    }

    setSubmitting(true);

    try {
      await loginRequest({
        username: normalizedUsername,
        password,
        ...(normalizedSystemId ? { systemId: normalizedSystemId } : {}),
      });

      router.replace(nextPath ?? "/dashboard");
      router.refresh();
    } catch (requestError) {
      if (requestError instanceof ApiError && requestError.code === "SYSTEM_ID_REQUIRED") {
        setSystemIdRequired(true);
        setError(
          "This username exists on more than one camera system. Enter your Camera System ID to continue.",
        );
      } else if (requestError instanceof ApiError) {
        setError(
          requestError.detail && requestError.status >= 500
            ? `${requestError.message} ${requestError.detail}`
            : requestError.message,
        );
      } else {
        setError("Could not reach the dashboard. Check your connection and try again.");
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="relative flex min-h-dvh flex-col items-center justify-center px-4 py-10">
      <div className="absolute top-4 right-4">
        <ThemeToggle />
      </div>

      <div className="mb-6 flex flex-col items-center gap-3 text-center">
        <span className="flex size-12 items-center justify-center rounded-2xl bg-primary text-primary-foreground shadow-sm">
          <CameraIcon className="size-6" aria-hidden="true" />
        </span>
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Security Camera</h1>
          <p className="text-sm text-muted-foreground">Sign in to monitor your system.</p>
        </div>
      </div>

      <Card className="w-full max-w-md">
        <form onSubmit={handleSubmit} noValidate>
          <CardHeader>
            <CardTitle>Dashboard sign in</CardTitle>
            <CardDescription>
              Use your factory administrator or sub-user credentials.
            </CardDescription>
          </CardHeader>

          <CardContent className="flex flex-col gap-4">
            {expired && (
              <div
                role="status"
                className="flex items-start gap-2 rounded-lg border border-amber-500/30 bg-amber-500/10 p-3 text-sm text-amber-700 dark:text-amber-300"
              >
                <ShieldAlertIcon className="mt-0.5 size-4 shrink-0" />
                <span>Your session expired or was revoked. Please sign in again.</span>
              </div>
            )}

            {error && (
              <div
                role="alert"
                className="flex items-start gap-2 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
              >
                <ShieldAlertIcon className="mt-0.5 size-4 shrink-0" />
                <span>{error}</span>
              </div>
            )}

            <div className="flex flex-col gap-2">
              <Label htmlFor="username">Username</Label>
              <Input
                id="username"
                name="username"
                autoComplete="username"
                autoCapitalize="none"
                spellCheck={false}
                value={username}
                onChange={(event) => setUsername(event.target.value)}
                disabled={submitting}
                required
              />
            </div>

            <div className="flex flex-col gap-2">
              <Label htmlFor="password">Password</Label>
              <div className="relative">
                <Input
                  id="password"
                  name="password"
                  type={showPassword ? "text" : "password"}
                  autoComplete="current-password"
                  value={password}
                  onChange={(event) => setPassword(event.target.value)}
                  disabled={submitting}
                  className="pr-10"
                  required
                />
                <Button
                  type="button"
                  variant="ghost"
                  size="icon-sm"
                  className="absolute top-1/2 right-1 -translate-y-1/2"
                  onClick={() => setShowPassword((shown) => !shown)}
                  aria-label={showPassword ? "Hide password" : "Show password"}
                  tabIndex={-1}
                >
                  {showPassword ? <EyeOffIcon /> : <EyeIcon />}
                </Button>
              </div>
            </div>

            {systemIdRequired && (
              <div className="flex flex-col gap-2">
                <Label htmlFor="systemId">Camera System ID</Label>
                <Input
                  ref={systemIdRef}
                  id="systemId"
                  name="systemId"
                  placeholder="e.g. sys-001"
                  autoComplete="off"
                  autoCapitalize="none"
                  spellCheck={false}
                  value={systemId}
                  onChange={(event) => setSystemId(event.target.value)}
                  disabled={submitting}
                  required
                />
                <p className="flex items-start gap-1.5 text-xs text-muted-foreground">
                  <LockKeyholeIcon className="mt-0.5 size-3.5 shrink-0" />
                  This username belongs to more than one system. The system ID selects which
                  camera system you manage.
                </p>
              </div>
            )}
          </CardContent>

          <CardFooter className="mt-4">
            <Button type="submit" className="w-full" disabled={submitting}>
              {submitting && <Loader2Icon className="animate-spin" />}
              {submitting ? "Signing in…" : "Sign in"}
            </Button>
          </CardFooter>
        </form>
      </Card>

      <p className="mt-6 max-w-sm text-center text-xs text-muted-foreground">
        Sessions are stored in a secure, http-only cookie. Signing out revokes the token on the
        backend.
      </p>
    </div>
  );
}
