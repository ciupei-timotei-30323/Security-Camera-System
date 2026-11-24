"use client";

/* eslint-disable @next/next/no-img-element */

import { Loader2Icon, RadioIcon, RefreshCwIcon, TriangleAlertIcon, WifiOffIcon } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  WebRtcPlayerController,
  type WebRtcPlayerState,
} from "@/lib/webrtc-player";
import { cn } from "@/lib/utils";

/**
 * Live view: WebRTC/WHEP first, MJPEG `<img>` fallback.
 *
 * Video never passes through Next.js — the browser pulls the H.264 stream directly from MediaMTX
 * over SRTP, which is what gets the sub-second latency. When the WHEP handshake or ICE fails
 * within five seconds the component falls back to the existing `/api/camera/stream` MJPEG route,
 * so the dashboard keeps working exactly as before on a deployment without MediaMTX.
 */

const TONE_CLASSES: Record<string, string> = {
  live: "border-emerald-500/30 bg-emerald-500/10 text-emerald-600 dark:text-emerald-400",
  fallback: "border-amber-500/30 bg-amber-500/10 text-amber-700 dark:text-amber-400",
  offline: "border-destructive/30 bg-destructive/10 text-destructive",
  connecting: "border-sky-500/30 bg-sky-500/10 text-sky-600 dark:text-sky-400",
};

function badgeLabel(state: WebRtcPlayerState): { label: string; tone: string } {
  if (state.status === "live") {
    return { label: "WebRTC Live", tone: "live" };
  }

  if (state.status === "connecting") {
    return { label: "Connecting", tone: "connecting" };
  }

  if (state.status === "offline") {
    return { label: "Offline", tone: "offline" };
  }

  if (state.status === "fallback") {
    return { label: "MJPEG Fallback", tone: "fallback" };
  }

  return { label: "Idle", tone: "connecting" };
}

export interface WebRtcStreamPlayerProps {
  /** Optional override so tests/embedders can inject a fake controller. */
  controllerFactory?: () => WebRtcPlayerController;
  /** Fallback MJPEG route. Kept as a prop so the legacy route stays easy to swap. */
  fallbackSrc?: string;
}

export function WebRtcStreamPlayer({
  controllerFactory,
  fallbackSrc = "/api/camera/stream",
}: WebRtcStreamPlayerProps) {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const controllerRef = useRef<WebRtcPlayerController | null>(null);
  const [state, setState] = useState<WebRtcPlayerState>({
    status: "idle",
    message: null,
    hasVideo: false,
    resourceUrl: null,
  });
  // Bumped on every explicit retry so the `<video>` element is remounted with a fresh stream.
  const [attempt, setAttempt] = useState(0);

  const connect = useCallback(async () => {
    const controller = controllerFactory
      ? controllerFactory()
      : new WebRtcPlayerController({
          onStateChange: (next) => setState(next),
          onStream: (stream) => {
            const video = videoRef.current;

            if (video) {
              video.srcObject = stream;
              void video.play().catch(() => undefined);
            }
          },
        });

    controllerRef.current = controller;
    setState(controller.getState());

    await controller.start();
  }, [controllerFactory]);

  useEffect(() => {
    void connect();

    // Copy the element out of the ref: by the time cleanup runs, `videoRef.current` may already
    // point at a different (or unmounted) node.
    const video = videoRef.current;

    return () => {
      const controller = controllerRef.current;
      controllerRef.current = null;

      if (video) {
        video.srcObject = null;
      }

      void controller?.stop();
    };
  }, [connect, attempt]);

  const badge = badgeLabel(state);
  const showFallback = state.status === "fallback" || state.status === "offline";
  const connecting = state.status === "connecting" || state.status === "idle";

  return (
    <div className="relative aspect-video w-full overflow-hidden rounded-md bg-zinc-950">
      <video
        ref={videoRef}
        autoPlay
        playsInline
        muted
        className={cn(
          "absolute inset-0 h-full w-full object-contain transition-opacity duration-300",
          state.status === "live" ? "opacity-100" : "opacity-0",
        )}
      />

      {showFallback && (
        <img
          key={`mjpeg-${attempt}`}
          src={fallbackSrc}
          alt="Live camera feed (MJPEG fallback)"
          className="absolute inset-0 h-full w-full object-contain"
        />
      )}

      {connecting && (
        <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 bg-zinc-950/40 px-6 text-center text-zinc-200">
          <Loader2Icon className="size-6 animate-spin" aria-hidden="true" />
          <span className="text-sm">Negotiating WebRTC…</span>
        </div>
      )}

      <div className="absolute left-3 top-3 flex items-center gap-2">
        <Badge variant="outline" className={cn("gap-1.5 font-normal", TONE_CLASSES[badge.tone])}>
          <span
            className={cn(
              "size-1.5 rounded-full",
              badge.tone === "live" && "animate-pulse bg-emerald-500",
              badge.tone === "fallback" && "bg-amber-500",
              badge.tone === "offline" && "bg-destructive",
              badge.tone === "connecting" && "bg-sky-500",
            )}
          />
          {badge.label}
        </Badge>
      </div>

      {state.status === "fallback" && (
        <div className="absolute inset-x-0 bottom-0 flex flex-wrap items-center justify-between gap-2 bg-zinc-950/85 px-4 py-3 text-zinc-100 backdrop-blur">
          <span className="flex items-center gap-2 text-xs">
            <TriangleAlertIcon className="size-4 text-amber-400" aria-hidden="true" />
            {state.message ?? "WebRTC unavailable — showing the MJPEG stream."}
          </span>
          <Button type="button" size="sm" variant="outline" onClick={() => setAttempt((n) => n + 1)}>
            <RefreshCwIcon />
            Retry WebRTC
          </Button>
        </div>
      )}

      {state.status === "offline" && (
        <div className="absolute inset-0 flex flex-col items-center justify-center gap-3 px-6 text-center text-zinc-300">
          <WifiOffIcon className="size-8 text-zinc-500" aria-hidden="true" />
          <div className="flex flex-col gap-1">
            <span className="text-base font-medium">Camera not publishing</span>
            <span className="max-w-xs text-xs text-zinc-500">
              {state.message ?? "The Raspberry Pi has not started its RTSP stream yet."}
            </span>
          </div>
          <Button type="button" size="sm" variant="outline" onClick={() => setAttempt((n) => n + 1)}>
            <RefreshCwIcon />
            Try again
          </Button>
        </div>
      )}

      {state.status === "live" && (
        <div className="absolute bottom-3 left-3 flex items-center gap-1.5 rounded-full bg-zinc-950/70 px-2.5 py-1 text-[11px] text-zinc-300 backdrop-blur">
          <RadioIcon className="size-3 text-emerald-400" aria-hidden="true" />
          H.264 · WebRTC
        </div>
      )}
    </div>
  );
}
