"use client";

/* eslint-disable @next/next/no-img-element */

import {
  CloudOffIcon,
  ExternalLinkIcon,
  ImageOffIcon,
  Loader2Icon,
  ShieldAlertIcon,
  XIcon,
} from "lucide-react";
import { useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Separator } from "@/components/ui/separator";
import { formatConfidence, formatDateTime } from "@/lib/format";
import type { DetectionEvent } from "@/lib/types";

function EventDialogBody({ event }: { event: DetectionEvent }) {
  const imageUrl = event.annotatedImageUrl ?? `/api/events/${event.id}/annotated`;
  const [imageState, setImageState] = useState<"loading" | "loaded" | "error">("loading");
  const confidence = formatConfidence(event.confidence);

  return (
    <>
      <div className="relative aspect-video w-full overflow-hidden bg-zinc-950">
        <img
          src={imageUrl}
          alt={`Annotated frame for event ${event.id}`}
          className="h-full w-full object-contain"
          onLoad={() => setImageState("loaded")}
          onError={() => setImageState("error")}
        />

        {imageState === "loading" && (
          <div className="absolute inset-0 flex items-center justify-center gap-2 text-sm text-zinc-300">
            <Loader2Icon className="size-5 animate-spin" />
            Loading annotated frame…
          </div>
        )}

        {imageState === "error" && (
          <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 px-6 text-center text-zinc-300">
            <ImageOffIcon className="size-8 text-zinc-500" />
            <span className="text-sm font-medium">Annotated frame unavailable</span>
            <span className="text-xs text-zinc-500">
              The image file may have been removed or your session expired.
            </span>
          </div>
        )}

        <DialogClose
          render={
            <Button
              variant="secondary"
              size="icon-sm"
              className="absolute top-2 right-2 z-10 border border-white/10 bg-background/80 backdrop-blur"
            />
          }
        >
          <XIcon />
          <span className="sr-only">Close</span>
        </DialogClose>
      </div>

      <div className="flex flex-col gap-4 p-4">
        <DialogHeader>
          <div className="flex flex-wrap items-center gap-2">
            <Badge variant={event.isSuspect ? "destructive" : "secondary"}>
              {event.isSuspect ? "Suspect" : "Logged"}
            </Badge>
            {event.isOfflineSync && (
              <Badge variant="outline" className="gap-1">
                <CloudOffIcon className="size-3" />
                synced offline
              </Badge>
            )}
            <Badge variant="outline">Event #{event.id}</Badge>
          </div>
          <DialogTitle>
            {event.isSuspect ? "Suspect detected" : "Entrance logged"}
          </DialogTitle>
          <DialogDescription>
            {formatDateTime(event.timestamp)}
          </DialogDescription>
        </DialogHeader>

        <Separator />

        <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm sm:grid-cols-3">
          <div>
            <dt className="text-xs text-muted-foreground">Captured</dt>
            <dd className="break-words">{formatDateTime(event.timestamp)}</dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">Confidence</dt>
            <dd>{confidence ?? "Not reported"}</dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">Camera system</dt>
            <dd className="break-all">{event.systemId}</dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">Source</dt>
            <dd>{event.isOfflineSync ? "Offline sync" : "Live capture"}</dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">Client event ID</dt>
            <dd className="break-all font-mono text-xs">
              {event.clientEventId ?? "—"}
            </dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">Alert</dt>
            <dd className="flex items-center gap-1">
              {event.isSuspect ? (
                <>
                  <ShieldAlertIcon className="size-3.5 text-destructive" />
                  Suspect
                </>
              ) : (
                "None"
              )}
            </dd>
          </div>
        </dl>
      </div>

      <DialogFooter className="mx-0 mb-0 rounded-b-xl px-4" showCloseButton={false}>
        <a
          href={imageUrl}
          target="_blank"
          rel="noreferrer"
          className="inline-flex h-8 items-center justify-center gap-1.5 rounded-lg border border-border bg-background px-2.5 text-sm font-medium hover:bg-muted"
        >
          <ExternalLinkIcon className="size-4" />
          Open full frame
        </a>
        <DialogClose render={<Button variant="outline" />}>Close</DialogClose>
      </DialogFooter>
    </>
  );
}

export function EventDialog({
  event,
  onClose,
}: {
  event: DetectionEvent | null;
  onClose: () => void;
}) {
  return (
    <Dialog
      open={Boolean(event)}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
    >
      {event && (
        <DialogContent
          key={event.id}
          showCloseButton={false}
          className="max-h-[92dvh] gap-0 overflow-y-auto p-0 sm:max-w-3xl"
        >
          <EventDialogBody event={event} />
        </DialogContent>
      )}
    </Dialog>
  );
}
