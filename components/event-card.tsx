"use client";

/* eslint-disable @next/next/no-img-element */

import {
  CloudOffIcon,
  ImageOffIcon,
  Maximize2Icon,
  ShieldAlertIcon,
  UserRoundIcon,
} from "lucide-react";
import { useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { formatConfidence, formatDateTime } from "@/lib/format";
import type { DetectionEvent } from "@/lib/types";
import { cn } from "@/lib/utils";

function FaceThumbnail({ event }: { event: DetectionEvent }) {
  const [failed, setFailed] = useState(false);

  if (!event.detectedFaceUrl || failed) {
    return (
      <div className="flex h-20 w-20 shrink-0 items-center justify-center rounded-lg bg-muted text-muted-foreground sm:h-24 sm:w-24">
        {event.isSuspect ? (
          <ShieldAlertIcon className="size-7" aria-hidden="true" />
        ) : (
          <UserRoundIcon className="size-7" aria-hidden="true" />
        )}
        <span className="sr-only">No face crop available</span>
      </div>
    );
  }

  return (
    <div className="relative h-20 w-20 shrink-0 overflow-hidden rounded-lg bg-muted sm:h-24 sm:w-24">
      <img
        src={event.detectedFaceUrl}
        alt={`Face crop for event ${event.id}`}
        loading="lazy"
        className="h-full w-full object-cover"
        onError={() => setFailed(true)}
      />
    </div>
  );
}

export function EventCard({
  event,
  highlighted,
  onOpen,
}: {
  event: DetectionEvent;
  highlighted: boolean;
  onOpen: (event: DetectionEvent) => void;
}) {
  const confidence = formatConfidence(event.confidence);

  return (
    <Card
      size="sm"
      role="button"
      tabIndex={0}
      aria-label={`Open event ${event.id} from ${formatDateTime(event.timestamp)}`}
      onClick={() => onOpen(event)}
      onKeyDown={(keyboardEvent) => {
        if (keyboardEvent.key === "Enter" || keyboardEvent.key === " ") {
          keyboardEvent.preventDefault();
          onOpen(event);
        }
      }}
      className={cn(
        "cursor-pointer gap-0 transition-all hover:-translate-y-0.5 hover:shadow-md focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none",
        event.isSuspect && "ring-destructive/20",
        highlighted &&
          "ring-2 ring-amber-400 shadow-md shadow-amber-500/10 dark:ring-amber-500",
      )}
    >
      <CardContent className="flex gap-3 p-3">
        <FaceThumbnail key={event.detectedFaceUrl ?? "no-face"} event={event} />

        <div className="flex min-w-0 flex-1 flex-col gap-2">
          <div className="flex flex-wrap items-center gap-1.5">
            <Badge variant={event.isSuspect ? "destructive" : "secondary"}>
              {event.isSuspect ? "Suspect" : "Logged"}
            </Badge>
            {event.isOfflineSync && (
              <Badge variant="outline" className="gap-1">
                <CloudOffIcon className="size-3" />
                synced offline
              </Badge>
            )}
            {highlighted && (
              <Badge className="bg-amber-500 text-white dark:bg-amber-500">New</Badge>
            )}
          </div>

          <div className="min-w-0">
            <p className="truncate text-sm font-medium">
              {event.isSuspect ? "Suspect detected" : "Entrance logged"}
            </p>
            <time
              dateTime={event.timestamp}
              className="block truncate text-xs text-muted-foreground"
              title={formatDateTime(event.timestamp)}
            >
              {formatDateTime(event.timestamp)}
            </time>
          </div>

          <div className="mt-auto flex items-center justify-between gap-2 text-xs text-muted-foreground">
            <span className="flex items-center gap-1">
              {confidence ? (
                <>
                  <ImageOffIcon className="size-3" aria-hidden="true" />
                  Confidence {confidence}
                </>
              ) : (
                <span>Event #{event.id}</span>
              )}
            </span>
            <Maximize2Icon className="size-3.5 shrink-0" aria-hidden="true" />
          </div>
        </div>
      </CardContent>
    </Card>
  );
}
