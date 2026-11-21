"use client";

import {
  AlarmClockIcon,
  CalendarClockIcon,
  CheckCircle2Icon,
  Loader2Icon,
  MailIcon,
  RefreshCwIcon,
  SaveIcon,
  ShieldCheckIcon,
  ShieldOffIcon,
  TimerResetIcon,
} from "lucide-react";
import { FormEvent, useMemo, useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Separator } from "@/components/ui/separator";
import { Skeleton } from "@/components/ui/skeleton";
import { ApiError } from "@/lib/api";
import { useAwayMode, useUpdateAwayMode } from "@/lib/queries";
import type { AwayMode, AwayModeSetting } from "@/lib/types";
import { cn } from "@/lib/utils";

const FALLBACK_TIME_ZONES = [
  "Europe/Bucharest",
  "Europe/London",
  "Europe/Berlin",
  "Europe/Madrid",
  "America/New_York",
  "America/Chicago",
  "America/Denver",
  "America/Los_Angeles",
  "Asia/Tokyo",
  "Australia/Sydney",
  "UTC",
];

const MODES: Array<{
  value: AwayMode;
  label: string;
  description: string;
  icon: typeof ShieldCheckIcon;
}> = [
  {
    value: "off",
    label: "Off",
    description: "Disarmed. No scheduled arm window.",
    icon: ShieldOffIcon,
  },
  {
    value: "indefinite",
    label: "Indefinite",
    description: "Armed until you switch it off.",
    icon: ShieldCheckIcon,
  },
  {
    value: "scheduled",
    label: "Scheduled",
    description: "Armed only during a daily window.",
    icon: CalendarClockIcon,
  },
];

type FormState = {
  mode: AwayMode;
  startTime: string;
  endTime: string;
  alertEmail: string;
  timeZone: string;
};

function toFormState(setting: AwayModeSetting): FormState {
  return {
    mode: setting.mode,
    startTime: setting.startTime ?? "22:00",
    endTime: setting.endTime ?? "06:00",
    alertEmail: setting.alertEmail ?? "",
    timeZone: setting.timeZone || "Europe/Bucharest",
  };
}

function SettingsContent({ setting }: { setting: AwayModeSetting }) {
  const updateMutation = useUpdateAwayMode();
  const [form, setForm] = useState<FormState>(() => toFormState(setting));
  const [saved, setSaved] = useState(false);
  const [validationError, setValidationError] = useState<string | null>(null);

  const timeZones = useMemo(() => {
    let zones: string[] = [];

    try {
      zones = Intl.supportedValuesOf("timeZone");
    } catch {
      zones = FALLBACK_TIME_ZONES;
    }

    return Array.from(
      new Set([form.timeZone, setting.timeZone, ...zones, ...FALLBACK_TIME_ZONES]),
    ).sort((a, b) => a.localeCompare(b));
  }, [form.timeZone, setting.timeZone]);

  function update<K extends keyof FormState>(key: K, value: FormState[K]) {
    setSaved(false);
    setValidationError(null);
    setForm((current) => ({ ...current, [key]: value }));
  }

  function handleReset() {
    setSaved(false);
    setValidationError(null);
    updateMutation.reset();
    setForm(toFormState(setting));
  }

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaved(false);
    setValidationError(null);

    if (form.mode === "scheduled") {
      if (!form.startTime || !form.endTime) {
        setValidationError("Scheduled mode requires both a start and an end time.");
        return;
      }

      if (form.startTime === form.endTime) {
        setValidationError("Start time and end time must be different.");
        return;
      }
    }

    updateMutation.mutate(
      {
        mode: form.mode,
        startTime: form.mode === "scheduled" ? form.startTime : null,
        endTime: form.mode === "scheduled" ? form.endTime : null,
        alertEmail: form.alertEmail.trim() || null,
        timeZone: form.timeZone,
      },
      {
        onSuccess: () => setSaved(true),
      },
    );
  }

  const isArmedNow = setting.isArmedNow;
  const error =
    validationError ??
    (updateMutation.error instanceof ApiError
      ? updateMutation.error.detail
        ? `${updateMutation.error.message} ${updateMutation.error.detail}`
        : updateMutation.error.message
      : null);

  return (
    <div className="flex flex-col gap-6">
      <Card
        className={cn(
          "overflow-hidden",
          isArmedNow
            ? "ring-2 ring-red-500/40 dark:ring-red-500/50"
            : "ring-2 ring-emerald-500/40 dark:ring-emerald-500/50",
        )}
      >
        <CardContent
          className={cn(
            "flex flex-col gap-4 p-5 sm:flex-row sm:items-center sm:justify-between",
            isArmedNow
              ? "bg-red-500/5 dark:bg-red-500/10"
              : "bg-emerald-500/5 dark:bg-emerald-500/10",
          )}
        >
          <div className="flex items-center gap-4">
            <span
              className={cn(
                "flex size-14 shrink-0 items-center justify-center rounded-2xl",
                isArmedNow
                  ? "bg-red-500/15 text-red-600 dark:text-red-400"
                  : "bg-emerald-500/15 text-emerald-600 dark:text-emerald-400",
              )}
            >
              {isArmedNow ? (
                <ShieldCheckIcon className="size-7" />
              ) : (
                <ShieldOffIcon className="size-7" />
              )}
            </span>
            <div>
              <p className="text-xs font-medium tracking-[0.18em] text-muted-foreground uppercase">
                System is
              </p>
              <p
                className={cn(
                  "text-3xl font-bold tracking-tight sm:text-4xl",
                  isArmedNow
                    ? "text-red-600 dark:text-red-400"
                    : "text-emerald-600 dark:text-emerald-400",
                )}
              >
                {isArmedNow ? "ARMED" : "DISARMED"}
              </p>
              <p className="mt-1 text-xs text-muted-foreground">
                {isArmedNow
                  ? "Suspect events will trigger alerts right now."
                  : "The system is currently disarmed."}
              </p>
            </div>
          </div>

          <div className="flex flex-wrap items-center gap-2 sm:flex-col sm:items-end">
            <Badge variant="outline" className="bg-background/60 font-normal">
              <AlarmClockIcon className="size-3" />
              Mode: {setting.mode}
            </Badge>
            {setting.mode === "scheduled" && (
              <Badge variant="outline" className="bg-background/60 font-normal">
                {setting.startTime ?? "—"} → {setting.endTime ?? "—"}
              </Badge>
            )}
            <span className="text-xs text-muted-foreground">{setting.timeZone}</span>
          </div>
        </CardContent>
      </Card>

      <form
        onSubmit={handleSubmit}
        className="grid gap-6 lg:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)]"
      >
        <Card>
          <CardHeader>
            <CardTitle>Away mode</CardTitle>
            <CardDescription>
              Choose when the system should be armed and where suspect alerts go.
            </CardDescription>
          </CardHeader>
          <CardContent className="flex flex-col gap-6">
            <fieldset className="flex flex-col gap-2">
              <legend className="mb-1 text-sm font-medium">Mode</legend>
              <div className="grid gap-2 sm:grid-cols-3">
                {MODES.map((mode) => {
                  const Icon = mode.icon;
                  const selected = form.mode === mode.value;

                  return (
                    <button
                      key={mode.value}
                      type="button"
                      aria-pressed={selected}
                      onClick={() => update("mode", mode.value)}
                      className={cn(
                        "flex flex-col items-start gap-1 rounded-xl border p-3 text-left transition-colors",
                        selected
                          ? "border-primary bg-primary/5 ring-2 ring-primary/20"
                          : "border-border hover:bg-muted/50",
                      )}
                    >
                      <span className="flex items-center gap-2 text-sm font-medium">
                        <Icon className="size-4" />
                        {mode.label}
                        {selected && <CheckCircle2Icon className="ml-auto size-4" />}
                      </span>
                      <span className="text-xs text-muted-foreground">
                        {mode.description}
                      </span>
                    </button>
                  );
                })}
              </div>
            </fieldset>

            {form.mode === "scheduled" && (
              <div className="grid gap-4 rounded-xl border bg-muted/30 p-4 sm:grid-cols-2">
                <div className="flex flex-col gap-2">
                  <Label htmlFor="startTime">Arm at</Label>
                  <Input
                    id="startTime"
                    type="time"
                    value={form.startTime}
                    onChange={(event) => update("startTime", event.target.value)}
                    required
                  />
                </div>
                <div className="flex flex-col gap-2">
                  <Label htmlFor="endTime">Disarm at</Label>
                  <Input
                    id="endTime"
                    type="time"
                    value={form.endTime}
                    onChange={(event) => update("endTime", event.target.value)}
                    required
                  />
                </div>
                <p className="flex items-start gap-1.5 text-xs text-muted-foreground sm:col-span-2">
                  <TimerResetIcon className="mt-0.5 size-3.5 shrink-0" />
                  Overnight windows such as 22:00 → 06:00 are supported. Times are evaluated
                  in the selected time zone.
                </p>
              </div>
            )}

            <Separator />

            <div className="flex flex-col gap-2">
              <Label htmlFor="alertEmail">Alert e-mail</Label>
              <div className="relative">
                <MailIcon className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" />
                <Input
                  id="alertEmail"
                  type="email"
                  inputMode="email"
                  autoComplete="email"
                  placeholder="alerts@example.com"
                  className="pl-8"
                  value={form.alertEmail}
                  onChange={(event) => update("alertEmail", event.target.value)}
                />
              </div>
              <p className="text-xs text-muted-foreground">
                Suspect events are e-mailed to this address while away mode is active. Leave
                blank to disable e-mail alerts.
              </p>
            </div>

            <div className="flex flex-col gap-2">
              <Label htmlFor="timeZone">Time zone</Label>
              <select
                id="timeZone"
                value={form.timeZone}
                onChange={(event) => update("timeZone", event.target.value)}
                className="h-8 w-full min-w-0 rounded-lg border border-input bg-transparent px-2.5 py-1 text-sm outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 dark:bg-input/30"
              >
                {timeZones.map((zone) => (
                  <option key={zone} value={zone}>
                    {zone}
                  </option>
                ))}
              </select>
              <p className="text-xs text-muted-foreground">
                IANA time-zone id. Defaults to Europe/Bucharest.
              </p>
            </div>

            {error && (
              <p
                role="alert"
                className="rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
              >
                {error}
              </p>
            )}

            {saved && !error && (
              <p
                role="status"
                className="flex items-center gap-2 rounded-lg border border-emerald-500/30 bg-emerald-500/10 p-3 text-sm text-emerald-700 dark:text-emerald-300"
              >
                <CheckCircle2Icon className="size-4" />
                Settings saved.
              </p>
            )}

            <div className="flex flex-wrap items-center gap-2">
              <Button type="submit" disabled={updateMutation.isPending}>
                {updateMutation.isPending ? (
                  <Loader2Icon className="animate-spin" />
                ) : (
                  <SaveIcon />
                )}
                {updateMutation.isPending ? "Saving…" : "Save settings"}
              </Button>
              <Button
                type="button"
                variant="outline"
                onClick={handleReset}
                disabled={updateMutation.isPending}
              >
                Reset
              </Button>
            </div>
          </CardContent>
        </Card>

        <div className="flex flex-col gap-4">
          <Card>
            <CardHeader>
              <CardTitle className="text-base">How away mode works</CardTitle>
            </CardHeader>
            <CardContent className="flex flex-col gap-3 text-sm text-muted-foreground">
              <div className="flex gap-2">
                <ShieldCheckIcon className="mt-0.5 size-4 shrink-0 text-foreground" />
                <p>
                  <span className="font-medium text-foreground">Indefinite</span> stays armed
                  until you turn it off.
                </p>
              </div>
              <div className="flex gap-2">
                <CalendarClockIcon className="mt-0.5 size-4 shrink-0 text-foreground" />
                <p>
                  <span className="font-medium text-foreground">Scheduled</span> arms and
                  disarms daily using the selected time zone.
                </p>
              </div>
              <div className="flex gap-2">
                <MailIcon className="mt-0.5 size-4 shrink-0 text-foreground" />
                <p>
                  E-mail alerts are sent for suspect events only, with a per-system cooldown.
                </p>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle className="text-base">Current schedule</CardTitle>
              <CardDescription>Computed by the backend at request time.</CardDescription>
            </CardHeader>
            <CardContent className="flex flex-col gap-2 text-sm">
              <div className="flex items-center justify-between gap-2">
                <span className="text-muted-foreground">Mode</span>
                <Badge variant="secondary">{setting.mode}</Badge>
              </div>
              <div className="flex items-center justify-between gap-2">
                <span className="text-muted-foreground">Armed now</span>
                <Badge variant={setting.isArmedNow ? "destructive" : "secondary"}>
                  {setting.isArmedNow ? "Yes" : "No"}
                </Badge>
              </div>
              <div className="flex items-center justify-between gap-2">
                <span className="text-muted-foreground">Schedule</span>
                <span>
                  {setting.mode === "scheduled"
                    ? `${setting.startTime ?? "—"} → ${setting.endTime ?? "—"}`
                    : "—"}
                </span>
              </div>
              <div className="flex items-center justify-between gap-2">
                <span className="text-muted-foreground">Alert e-mail</span>
                <span className="max-w-[60%] truncate">
                  {setting.alertEmail ?? "Not set"}
                </span>
              </div>
            </CardContent>
          </Card>
        </div>
      </form>
    </div>
  );
}

export default function SettingsPage() {
  const awayQuery = useAwayMode();

  if (awayQuery.isLoading) {
    return (
      <div className="flex flex-col gap-6">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Settings</h1>
          <p className="text-sm text-muted-foreground">Away mode, alerts and time zone.</p>
        </div>
        <Skeleton className="h-28 w-full rounded-xl" />
        <Skeleton className="h-96 w-full rounded-xl" />
      </div>
    );
  }

  if (awayQuery.isError || !awayQuery.data) {
    return (
      <div className="flex flex-col gap-6">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Settings</h1>
          <p className="text-sm text-muted-foreground">Away mode, alerts and time zone.</p>
        </div>
        <Card>
          <CardHeader>
            <CardTitle>Could not load settings</CardTitle>
            <CardDescription>
              {awayQuery.error instanceof Error
                ? awayQuery.error.message
                : "The away-mode endpoint failed."}
            </CardDescription>
          </CardHeader>
          <CardContent>
            <Button
              variant="outline"
              onClick={() => void awayQuery.refetch()}
              disabled={awayQuery.isFetching}
            >
              <RefreshCwIcon className={cn(awayQuery.isFetching && "animate-spin")} />
              Retry
            </Button>
          </CardContent>
        </Card>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Settings</h1>
        <p className="text-sm text-muted-foreground">
          Configure away mode, alert e-mail and the time zone used by the schedule.
        </p>
      </div>

      <SettingsContent setting={awayQuery.data} />
    </div>
  );
}
