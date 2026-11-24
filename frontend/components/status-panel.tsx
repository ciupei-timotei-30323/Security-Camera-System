"use client";

import {
  CameraIcon,
  CheckCircle2Icon,
  CpuIcon,
  RadioTowerIcon,
  RefreshCwIcon,
  ServerIcon,
  ShieldAlertIcon,
  ShieldCheckIcon,
  XCircleIcon,
} from "lucide-react";
import { useLiveUpdatesState } from "@/components/live-updates-provider";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { Skeleton } from "@/components/ui/skeleton";
import { formatDateTime, formatRelative } from "@/lib/format";
import { useSystemStatus } from "@/lib/queries";
import { cn } from "@/lib/utils";

function StatusChip({
  label,
  online,
  detail,
  icon: Icon,
}: {
  label: string;
  online: boolean | null;
  detail?: string;
  icon: typeof CameraIcon;
}) {
  const state =
    online === null ? "unknown" : online ? "online" : "offline";

  return (
    <div className="flex min-w-0 items-center gap-2 rounded-lg border bg-muted/30 px-3 py-2">
      <span
        className={cn(
          "flex size-8 shrink-0 items-center justify-center rounded-md",
          state === "online" && "bg-emerald-500/15 text-emerald-600 dark:text-emerald-400",
          state === "offline" && "bg-destructive/10 text-destructive",
          state === "unknown" && "bg-muted text-muted-foreground",
        )}
      >
        <Icon className="size-4" aria-hidden="true" />
      </span>
      <span className="min-w-0">
        <span className="block truncate text-sm font-medium">{label}</span>
        <span className="flex items-center gap-1 text-xs text-muted-foreground">
          {state === "online" ? (
            <CheckCircle2Icon className="size-3.5 text-emerald-600 dark:text-emerald-400" />
          ) : state === "offline" ? (
            <XCircleIcon className="size-3.5 text-destructive" />
          ) : null}
          <span className="truncate">{detail ?? (online ? "Online" : "Offline")}</span>
        </span>
      </span>
    </div>
  );
}

/**
 * Arm/alarm state pushed over MQTT. Rendered only when the broker delivered a document, so the
 * panel keeps its pre-existing shape on deployments without MQTT.
 */
function LiveArmStateChip() {
  const live = useLiveUpdatesState();

  if (!live || live.armed === null) {
    return null;
  }

  const alarm = live.alarm;

  return (
    <div
      className={cn(
        "flex min-w-0 items-center gap-2 rounded-lg border px-3 py-2",
        alarm
          ? "border-destructive/40 bg-destructive/10"
          : "border-emerald-500/30 bg-emerald-500/10",
      )}
    >
      <span
        className={cn(
          "flex size-8 shrink-0 items-center justify-center rounded-md",
          alarm
            ? "bg-destructive/15 text-destructive"
            : "bg-emerald-500/15 text-emerald-600 dark:text-emerald-400",
        )}
      >
        {alarm ? (
          <ShieldAlertIcon className="size-4" aria-hidden="true" />
        ) : (
          <ShieldCheckIcon className="size-4" aria-hidden="true" />
        )}
      </span>
      <span className="min-w-0">
        <span className="block truncate text-sm font-medium">
          {alarm ? "Alarm active" : live.armed ? "Armed" : "Disarmed"}
        </span>
        <span className="block truncate text-xs text-muted-foreground">
          {live.line1?.trim() || (alarm ? "SUSPECT DETECTED" : "MQTT live")}
        </span>
      </span>
    </div>
  );
}

export function StatusPanel() {
  const statusQuery = useSystemStatus();

  if (statusQuery.isLoading) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>System status</CardTitle>
          <CardDescription>Polling every 5 seconds</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          {[0, 1, 2].map((index) => (
            <Skeleton key={index} className="h-14 w-full" />
          ))}
        </CardContent>
      </Card>
    );
  }

  if (statusQuery.isError || !statusQuery.data) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>System status</CardTitle>
          <CardDescription>Could not load the system status.</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <StatusChip
            label="Camera"
            online={null}
            detail="Unknown"
            icon={CameraIcon}
          />
          <StatusChip
            label="Alert terminal"
            online={null}
            detail="Unknown"
            icon={RadioTowerIcon}
          />
          <StatusChip
            label="Backend"
            online={false}
            detail="Unreachable"
            icon={ServerIcon}
          />
          <Button
            variant="outline"
            size="sm"
            onClick={() => void statusQuery.refetch()}
            disabled={statusQuery.isFetching}
          >
            <RefreshCwIcon className={cn(statusQuery.isFetching && "animate-spin")} />
            Retry
          </Button>
        </CardContent>
      </Card>
    );
  }

  const status = statusQuery.data;
  const backendOnline = status.backend.toLowerCase() === "ok";
  const cvOnline = status.cvService.toLowerCase() === "ok";

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center justify-between gap-2">
          <span>System status</span>
          <Badge variant="outline" className="font-normal">
            <RefreshCwIcon
              className={cn("size-3", statusQuery.isFetching && "animate-spin")}
            />
            5 s
          </Badge>
        </CardTitle>
        <CardDescription>Camera, terminal and backend health.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <LiveArmStateChip />
        <StatusChip
          label="Camera"
          online={status.camera.online}
          detail={
            status.camera.online
              ? "Online"
              : `Offline · seen ${formatRelative(status.camera.lastSeen)}`
          }
          icon={CameraIcon}
        />
        <StatusChip
          label="Alert terminal"
          online={status.alertTerminal.online}
          detail={
            status.alertTerminal.online
              ? "Online"
              : `Offline · seen ${formatRelative(status.alertTerminal.lastSeen)}`
          }
          icon={RadioTowerIcon}
        />
        <StatusChip
          label="Backend"
          online={backendOnline}
          detail={backendOnline ? "Healthy" : status.backend}
          icon={ServerIcon}
        />

        <Separator />

        <div className="flex items-center justify-between gap-3 text-xs text-muted-foreground">
          <span className="flex items-center gap-1.5">
            <CpuIcon className="size-3.5" />
            CV service
          </span>
          <span className="flex items-center gap-1.5">
            <span
              className={cn(
                "size-2 rounded-full",
                cvOnline ? "bg-emerald-500" : "bg-destructive",
              )}
            />
            {cvOnline ? "OK" : status.cvService}
          </span>
        </div>

        <div className="grid grid-cols-2 gap-2 text-xs text-muted-foreground">
          <div className="rounded-md bg-muted/50 p-2">
            <span className="block font-medium text-foreground">Camera seen</span>
            <span title={status.camera.lastSeen ? formatDateTime(status.camera.lastSeen) : undefined}>
              {formatRelative(status.camera.lastSeen)}
            </span>
          </div>
          <div className="rounded-md bg-muted/50 p-2">
            <span className="block font-medium text-foreground">Terminal seen</span>
            <span
              title={
                status.alertTerminal.lastSeen
                  ? formatDateTime(status.alertTerminal.lastSeen)
                  : undefined
              }
            >
              {formatRelative(status.alertTerminal.lastSeen)}
            </span>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}
