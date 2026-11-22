"use client";

/* eslint-disable @next/next/no-img-element */

import {
  CameraOffIcon,
  Loader2Icon,
  PauseCircleIcon,
  PlayIcon,
  RefreshCwIcon,
  SquareIcon,
  VideoIcon,
  WifiOffIcon,
} from "lucide-react";
import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
  isLiveFeedLive,
  isLiveFeedPaused,
  LiveFeedController,
  type LiveFeedState,
} from "@/lib/live-feed-controller";
import { cn } from "@/lib/utils";

const clockFormatter = new Intl.DateTimeFormat(undefined, {
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  hour12: false,
});

function formatSnapshotTime(timestamp: number): string {
  return clockFormatter.format(new Date(timestamp));
}

function feedLabel(state: LiveFeedState): {
  label: string;
  tone: "live" | "offline" | "loading" | "idle" | "paused";
} {
  if (isLiveFeedPaused(state)) {
    return { label: "Paused", tone: "paused" };
  }

  if (state.status === "live" || state.status === "live-fallback") {
    return { label: "LIVE", tone: "live" };
  }

  if (state.status === "live-connecting") {
    return { label: "Connecting", tone: "loading" };
  }

  if (state.status === "offline" && !state.snapshotUrl) {
    return { label: "Camera offline", tone: "offline" };
  }

  if (state.status === "snapshot-loading") {
    return { label: "Loading", tone: "loading" };
  }

  return { label: "Snapshot", tone: state.snapshotUrl ? "idle" : "offline" };
}

async function captureElementFrame(image: HTMLImageElement | null): Promise<Blob | null> {
  if (!image || !image.complete || image.naturalWidth === 0 || image.naturalHeight === 0) {
    return null;
  }

  try {
    const canvas = document.createElement("canvas");
    canvas.width = image.naturalWidth;
    canvas.height = image.naturalHeight;

    const context = canvas.getContext("2d");

    if (!context) {
      return null;
    }

    context.drawImage(image, 0, 0);

    return await new Promise<Blob | null>((resolve) => {
      canvas.toBlob((blob) => resolve(blob), "image/jpeg", 0.9);
    });
  } catch {
    return null;
  }
}

export function LiveFeed() {
  const streamImageRef = useRef<HTMLImageElement | null>(null);
  const [controller] = useState(() => new LiveFeedController());
  const state = useSyncExternalStore(
    controller.subscribe,
    controller.getSnapshot,
    controller.getSnapshot,
  );

  useEffect(() => {
    const handleVisibilityChange = () => {
      controller.handleVisibilityChange(document.visibilityState === "hidden");
    };

    controller.setCaptureFrame(() => captureElementFrame(streamImageRef.current));
    document.addEventListener("visibilitychange", handleVisibilityChange);
    controller.start();

    return () => {
      document.removeEventListener("visibilitychange", handleVisibilityChange);
      controller.dispose();
    };
  }, [controller]);

  const live = isLiveFeedLive(state);
  const paused = isLiveFeedPaused(state);
  const showStream = live && state.streamArmed;
  const showSnapshot = Boolean(state.snapshotUrl);
  const badge = feedLabel(state);
  const snapshotLoading = state.snapshotLoading;
  const firstFramePending = state.status === "live-connecting";
  const snapshotPending = state.status === "snapshot-loading" && !showSnapshot;
  const offline = state.status === "offline" && !showSnapshot;
  const noFrame = !showSnapshot && !showStream && !snapshotPending && !offline;

  return (
    <Card className="overflow-hidden">
      <CardHeader>
        <CardTitle className="flex items-center justify-between gap-2">
          <span className="flex items-center gap-2">
            <VideoIcon className="size-4 text-muted-foreground" />
            Live feed
          </span>
          <Badge
            variant="outline"
            className={cn(
              "gap-1.5 font-normal",
              badge.tone === "live" &&
                "border-emerald-500/30 bg-emerald-500/10 text-emerald-600 dark:text-emerald-400",
              badge.tone === "offline" &&
                "border-destructive/30 bg-destructive/10 text-destructive",
              badge.tone === "paused" &&
                "border-amber-500/30 bg-amber-500/10 text-amber-700 dark:text-amber-400",
            )}
          >
            <span
              className={cn(
                "size-1.5 rounded-full",
                badge.tone === "live" && "animate-pulse bg-emerald-500",
                badge.tone === "offline" && "bg-destructive",
                badge.tone === "paused" && "bg-amber-500",
                (badge.tone === "idle" || badge.tone === "loading") &&
                  "bg-muted-foreground",
              )}
            />
            {badge.label}
          </Badge>
        </CardTitle>
        <CardDescription>
          Snapshots by default. Start the live feed when you need it.
        </CardDescription>
      </CardHeader>

      <CardContent className="px-0 pb-0 sm:px-0">
        <div
          className="relative aspect-video w-full overflow-hidden bg-zinc-950"
          aria-busy={snapshotLoading || firstFramePending}
        >
          {showSnapshot && (
            <img
              src={state.snapshotUrl ?? undefined}
              alt="Latest camera snapshot"
              className="absolute inset-0 h-full w-full object-contain"
            />
          )}

          {showStream && (
            <img
              ref={streamImageRef}
              key={state.streamKey}
              src="/api/camera/stream"
              alt="Live camera feed"
              className={cn(
                "absolute inset-0 h-full w-full object-contain transition-opacity duration-300",
                state.status === "live" ? "opacity-100" : "opacity-0",
              )}
              onLoad={() => controller.handleStreamFrame()}
              onError={() => controller.handleStreamError()}
            />
          )}

          {(snapshotPending || firstFramePending) && (
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 bg-zinc-950/40 px-6 text-center text-zinc-200">
              <Loader2Icon className="size-6 animate-spin" aria-hidden="true" />
              <span className="text-sm">
                {firstFramePending ? "Waiting for the first live frame…" : "Loading snapshot…"}
              </span>
            </div>
          )}

          {offline && (
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-3 px-6 text-center text-zinc-300">
              <WifiOffIcon className="size-8 text-zinc-500" aria-hidden="true" />
              <div className="flex flex-col gap-1">
                <span className="text-base font-medium">Camera offline</span>
                <span className="max-w-xs text-xs text-zinc-500">
                  No frame was available from the camera.
                </span>
              </div>
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => controller.refreshSnapshot()}
                disabled={snapshotLoading}
              >
                <RefreshCwIcon className={cn(snapshotLoading && "animate-spin")} />
                Try again
              </Button>
            </div>
          )}

          {noFrame && !live && (
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 px-6 text-center text-zinc-300">
              <CameraOffIcon className="size-8 text-zinc-500" aria-hidden="true" />
              <span className="text-base font-medium">No frame available</span>
            </div>
          )}

          {state.status === "live-fallback" && !showSnapshot && (
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 px-6 text-center text-zinc-300">
              <CameraOffIcon className="size-8 text-zinc-500" aria-hidden="true" />
              <span className="text-base font-medium">Camera offline</span>
              <span className="max-w-xs text-xs text-zinc-500">
                Waiting for the next snapshot.
              </span>
            </div>
          )}

          {paused && (
            <div
              className="absolute inset-x-0 bottom-0 flex flex-wrap items-center justify-between gap-2 bg-zinc-950/85 px-4 py-3 text-zinc-100 backdrop-blur"
              role="status"
            >
              <span className="flex items-center gap-2 text-sm">
                <PauseCircleIcon className="size-4 text-amber-400" aria-hidden="true" />
                Live feed paused to save bandwidth
              </span>
              <Button
                type="button"
                size="sm"
                onClick={() => void controller.startLive()}
                disabled={snapshotLoading}
              >
                <PlayIcon />
                Resume
              </Button>
            </div>
          )}
        </div>

        {!live && state.snapshotTakenAt !== null && (
          <p className="px-4 pt-3 text-xs text-muted-foreground">
            Snapshot taken at {formatSnapshotTime(state.snapshotTakenAt)}
          </p>
        )}
      </CardContent>

      <div className="flex flex-wrap items-center justify-between gap-2 border-t px-4 py-3">
        <div className="flex flex-col gap-1 text-xs text-muted-foreground">
          <span>
            {paused
              ? "Snapshot mode · live feed paused"
              : live
                ? state.status === "live-fallback"
                  ? "Snapshot fallback · 1 s poll while live"
                  : "MJPEG · low latency"
                : state.status === "offline"
                  ? "Snapshot mode · camera offline"
                  : "Snapshot mode · one request per refresh"}
          </span>
          {state.message && (
            <span
              className={cn(
                "text-amber-700 dark:text-amber-400",
                state.status === "live-fallback" && "text-muted-foreground",
              )}
              role="status"
            >
              {state.message}
            </span>
          )}
        </div>

        <div className="flex flex-wrap items-center justify-end gap-2">
          {paused ? (
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => controller.refreshSnapshot()}
              disabled={snapshotLoading}
            >
              <RefreshCwIcon className={cn(snapshotLoading && "animate-spin")} />
              Refresh snapshot
            </Button>
          ) : live ? (
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => controller.stopLive("user")}
            >
              <SquareIcon />
              Stop
            </Button>
          ) : (
            <>
              {state.status !== "offline" && (
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => controller.refreshSnapshot()}
                  disabled={snapshotLoading}
                >
                  <RefreshCwIcon className={cn(snapshotLoading && "animate-spin")} />
                  Refresh snapshot
                </Button>
              )}
              <Button type="button" size="sm" onClick={() => void controller.startLive()}>
                <VideoIcon />
                Watch live feed
              </Button>
            </>
          )}
        </div>
      </div>
    </Card>
  );
}
