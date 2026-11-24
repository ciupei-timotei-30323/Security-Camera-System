"use client";

import { RadioIcon, ShieldAlertIcon, ShieldCheckIcon, WifiOffIcon } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { useLiveUpdatesState } from "@/components/live-updates-provider";
import { formatTime } from "@/lib/format";
import { cn } from "@/lib/utils";

/**
 * Push-driven alarm banner.
 *
 * Renders nothing when the broker is not configured (the dashboard then behaves exactly as
 * before) and nothing while the state is quiet. A suspect alarm turns the whole strip red and the
 * armed/disarmed state is shown as a badge, so the dashboard reacts to an event in the same
 * second the Pi reported it instead of up to 5 seconds later on the next poll.
 */
export function AlarmBanner() {
  const live = useLiveUpdatesState();

  if (!live || live.status === "unavailable" || live.status === "closed") {
    return null;
  }

  const alarm = live.alarm;
  const armed = live.armed === true;
  const connecting = live.status === "connecting" || live.status === "reconnecting";

  if (!alarm && live.armed === null && !connecting) {
    return null;
  }

  return (
    <div
      role="status"
      aria-live="polite"
      className={cn(
        "flex flex-wrap items-center justify-between gap-3 rounded-lg border px-4 py-2.5 text-sm",
        alarm
          ? "border-destructive/40 bg-destructive/10 text-destructive"
          : "border-emerald-500/30 bg-emerald-500/10 text-emerald-700 dark:text-emerald-400",
      )}
    >
      <span className="flex min-w-0 items-center gap-2 font-medium">
        {alarm ? (
          <ShieldAlertIcon className="size-4 shrink-0 animate-pulse" aria-hidden="true" />
        ) : (
          <ShieldCheckIcon className="size-4 shrink-0" aria-hidden="true" />
        )}
        <span className="truncate">
          {alarm
            ? "SUSPECT DETECTED"
            : armed
              ? "System armed"
              : "System disarmed"}
        </span>
        {alarm && live.alarmUntil && (
          <span className="text-xs font-normal opacity-80">
            until {formatTime(live.alarmUntil)}
          </span>
        )}
      </span>

      <span className="flex items-center gap-2 text-xs">
        {live.serverTime && (
          <span className="hidden text-muted-foreground sm:inline">
            camera time {formatTime(live.serverTime)}
          </span>
        )}
        <Badge
          variant="outline"
          className={cn(
            "gap-1 font-normal",
            connecting
              ? "border-amber-500/30 bg-amber-500/10 text-amber-700 dark:text-amber-400"
              : "border-emerald-500/30 bg-emerald-500/10 text-emerald-700 dark:text-emerald-400",
          )}
        >
          {connecting ? (
            <WifiOffIcon className="size-3" aria-hidden="true" />
          ) : (
            <RadioIcon className="size-3" aria-hidden="true" />
          )}
          {connecting ? "Reconnecting" : "Live"}
        </Badge>
      </span>
    </div>
  );
}
