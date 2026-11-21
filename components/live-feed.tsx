"use client";

/* eslint-disable @next/next/no-img-element */

import {
  CameraOffIcon,
  Loader2Icon,
  RefreshCwIcon,
  VideoIcon,
  WifiOffIcon,
} from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { useLatestFrame } from "@/lib/queries";
import { cn } from "@/lib/utils";

type FeedMode = "stream" | "poll";

export function LiveFeed({ cameraOnline }: { cameraOnline?: boolean }) {
  const [mode, setMode] = useState<FeedMode>("stream");
  const [streamNonce, setStreamNonce] = useState(0);
  const [streamLive, setStreamLive] = useState(false);
  const [streamNotice, setStreamNotice] = useState<string | null>(null);
  const wasOnline = useRef<boolean | undefined>(cameraOnline);

  const latestQuery = useLatestFrame(mode === "poll" && cameraOnline !== false);
  const snapshotUrl = useMemo(
    () => (latestQuery.data ? URL.createObjectURL(latestQuery.data) : null),
    [latestQuery.data],
  );

  useEffect(() => {
    return () => {
      if (snapshotUrl) {
        URL.revokeObjectURL(snapshotUrl);
      }
    };
  }, [snapshotUrl]);

  // A multipart <img> can stay connected without ever delivering a frame. If that happens,
  // fall back to the 1 Hz /api/camera/latest snapshot endpoint.
  useEffect(() => {
    if (mode !== "stream" || cameraOnline === false) {
      return;
    }

    const timer = window.setTimeout(() => {
      if (!streamLive) {
        setMode("poll");
        setStreamNotice("Live stream did not start; showing snapshots instead.");
      }
    }, 8000);

    return () => window.clearTimeout(timer);
  }, [mode, streamNonce, streamLive, cameraOnline]);

  // Retry MJPEG when the camera comes back after being offline.
  useEffect(() => {
    if (cameraOnline && wasOnline.current === false) {
      setMode("stream");
      setStreamNonce((nonce) => nonce + 1);
      setStreamLive(false);
      setStreamNotice(null);
    }

    wasOnline.current = cameraOnline;
  }, [cameraOnline]);

  function retryStream() {
    setMode("stream");
    setStreamNonce((nonce) => nonce + 1);
    setStreamLive(false);
    setStreamNotice(null);
  }

  const showStream = mode === "stream" && cameraOnline !== false;
  const showSnapshot = mode === "poll" && cameraOnline !== false && Boolean(snapshotUrl);
  const hasFrame = cameraOnline !== false && (showStream ? streamLive : Boolean(snapshotUrl));
  const cameraOffline = cameraOnline === false;
  const frameError = latestQuery.isError;

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
              hasFrame && "border-emerald-500/30 bg-emerald-500/10 text-emerald-600 dark:text-emerald-400",
              cameraOffline && "border-destructive/30 bg-destructive/10 text-destructive",
            )}
          >
            <span
              className={cn(
                "size-1.5 rounded-full",
                hasFrame && "animate-pulse bg-emerald-500",
                cameraOffline && "bg-destructive",
                !hasFrame && !cameraOffline && "bg-muted-foreground",
              )}
            />
            {cameraOffline ? "Camera offline" : hasFrame ? (mode === "poll" ? "Snapshot" : "Live") : "Connecting"}
          </Badge>
        </CardTitle>
        <CardDescription>
          MJPEG stream with automatic snapshot fallback.
        </CardDescription>
      </CardHeader>

      <CardContent className="px-0 pb-0 sm:px-0">
        <div className="relative aspect-video w-full overflow-hidden bg-zinc-950">
          {showStream && (
            <img
              key={streamNonce}
              src="/api/camera/stream"
              alt="Live camera feed"
              className={cn(
                "h-full w-full object-contain transition-opacity duration-300",
                streamLive ? "opacity-100" : "opacity-0",
              )}
              onLoad={() => setStreamLive(true)}
              onError={() => {
                setMode("poll");
                setStreamNotice("Live stream unavailable; showing snapshots instead.");
              }}
            />
          )}

          {showSnapshot && (
            <img
              src={snapshotUrl ?? undefined}
              alt="Latest camera snapshot"
              className="h-full w-full object-contain"
            />
          )}

          {!hasFrame && !cameraOffline && !frameError && (
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-zinc-300">
              <Loader2Icon className="size-6 animate-spin" />
              <span className="text-sm">Waiting for a camera frame…</span>
              <span className="text-xs text-zinc-500">
                {mode === "poll" ? "Polling every second" : "Connecting to MJPEG"}
              </span>
            </div>
          )}

          {cameraOffline && (
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 px-6 text-center text-zinc-300">
              <WifiOffIcon className="size-8 text-zinc-500" />
              <span className="text-base font-medium">Camera offline</span>
              <span className="max-w-xs text-xs text-zinc-500">
                The Raspberry Pi has not sent a frame recently. The feed reconnects
                automatically when it comes back.
              </span>
            </div>
          )}

          {frameError && !cameraOffline && (
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 px-6 text-center text-zinc-300">
              <CameraOffIcon className="size-8 text-zinc-500" />
              <span className="text-base font-medium">Live view unavailable</span>
              <span className="max-w-xs text-xs text-zinc-500">
                The backend could not return the latest frame. Retrying automatically.
              </span>
            </div>
          )}
        </div>
      </CardContent>

      <div className="flex flex-wrap items-center justify-between gap-2 border-t px-4 py-3 text-xs text-muted-foreground">
        <span>
          {mode === "stream"
            ? "MJPEG · low latency"
            : snapshotUrl
              ? "Snapshots · 1 s poll"
              : "Snapshots · waiting for frame"}
        </span>
        <div className="flex items-center gap-2">
          {streamNotice && <span className="text-amber-600 dark:text-amber-400">{streamNotice}</span>}
          {latestQuery.isFetching && mode === "poll" && (
            <RefreshCwIcon className="size-3.5 animate-spin" />
          )}
          <Button variant="outline" size="sm" onClick={retryStream}>
            <RefreshCwIcon />
            Retry live
          </Button>
        </div>
      </div>
    </Card>
  );
}
