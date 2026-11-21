"use client";

import {
  ChevronDownIcon,
  HistoryIcon,
  InboxIcon,
  Loader2Icon,
  RefreshCwIcon,
  ShieldAlertIcon,
  ShieldCheckIcon,
} from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { EventCard } from "@/components/event-card";
import { EventDialog } from "@/components/event-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { useEvents } from "@/lib/queries";
import type { DetectionEvent } from "@/lib/types";
import { cn } from "@/lib/utils";

function EventSkeletons() {
  return (
    <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
      {[0, 1, 2, 3, 4, 5].map((index) => (
        <Skeleton key={index} className="h-32 w-full rounded-xl" />
      ))}
    </div>
  );
}

export function EntranceLog() {
  const [suspectOnly, setSuspectOnly] = useState(false);

  return (
    <EntranceLogContent
      key={suspectOnly ? "suspects-only" : "all-events"}
      suspectOnly={suspectOnly}
      onSuspectOnlyChange={setSuspectOnly}
    />
  );
}

function EntranceLogContent({
  suspectOnly,
  onSuspectOnlyChange,
}: {
  suspectOnly: boolean;
  onSuspectOnlyChange: (value: boolean) => void;
}) {
  const [selectedEvent, setSelectedEvent] = useState<DetectionEvent | null>(null);
  const [newIds, setNewIds] = useState<Set<number>>(() => new Set());
  const initializedRef = useRef(false);
  const latestTimestampRef = useRef(0);

  const eventsQuery = useEvents(suspectOnly);

  const items = useMemo(() => {
    const byId = new Map<number, DetectionEvent>();

    for (const page of eventsQuery.data?.pages ?? []) {
      for (const item of page.items) {
        if (!byId.has(item.id)) {
          byId.set(item.id, item);
        }
      }
    }

    return Array.from(byId.values()).sort(
      (left, right) =>
        new Date(right.timestamp).getTime() - new Date(left.timestamp).getTime() ||
        right.id - left.id,
    );
  }, [eventsQuery.data]);

  const total = eventsQuery.data?.pages[0]?.total ?? 0;
  const refreshing =
    eventsQuery.isFetching && !eventsQuery.isLoading && !eventsQuery.isFetchingNextPage;

  useEffect(() => {
    if (!eventsQuery.data) {
      return;
    }

    const timestamps = items.map((item) => new Date(item.timestamp).getTime());
    const maxTimestamp = timestamps.length ? Math.max(...timestamps) : 0;

    if (!initializedRef.current) {
      initializedRef.current = true;
      latestTimestampRef.current = maxTimestamp;
      return;
    }

    const fresh = items.filter(
      (item) => new Date(item.timestamp).getTime() > latestTimestampRef.current,
    );

    latestTimestampRef.current = Math.max(latestTimestampRef.current, maxTimestamp);

    if (fresh.length > 0) {
      setNewIds((current) => {
        const next = new Set(current);
        fresh.forEach((item) => next.add(item.id));
        return next;
      });
    }
  }, [eventsQuery.data, items]);

  useEffect(() => {
    if (newIds.size === 0) {
      return;
    }

    const timer = window.setTimeout(() => setNewIds(new Set()), 6000);
    return () => window.clearTimeout(timer);
  }, [newIds]);

  return (
    <section className="flex flex-col gap-4" aria-labelledby="entrance-log-title">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <h2
            id="entrance-log-title"
            className="flex items-center gap-2 text-lg font-semibold tracking-tight"
          >
            <HistoryIcon className="size-4 text-muted-foreground" />
            Entrance log
            <Badge variant="secondary">{total}</Badge>
          </h2>
          <p className="text-sm text-muted-foreground">
            Newest first. Auto-refreshes every 3 seconds; click a card for the annotated frame.
          </p>
        </div>

        <div className="flex items-center justify-between gap-3 sm:justify-end">
          <div className="flex items-center gap-2">
            {refreshing ? (
              <RefreshCwIcon className="size-3.5 animate-spin text-muted-foreground" />
            ) : (
              <span className="size-2 rounded-full bg-emerald-500/70" />
            )}
            <span className="text-xs text-muted-foreground">
              {refreshing ? "Refreshing…" : "Auto-refresh · 3 s"}
            </span>
          </div>

          <div className="flex items-center gap-2 rounded-lg border bg-muted/30 px-3 py-2">
            <Switch
              id="suspect-only"
              checked={suspectOnly}
              onCheckedChange={(checked) => onSuspectOnlyChange(checked)}
            />
            <Label htmlFor="suspect-only" className="cursor-pointer whitespace-nowrap text-xs">
              <ShieldAlertIcon className="size-3.5" />
              Suspects only
            </Label>
          </div>
        </div>
      </div>

      {eventsQuery.isLoading && <EventSkeletons />}

      {eventsQuery.isError && (
        <Card>
          <CardHeader>
            <CardTitle>Could not load the entrance log</CardTitle>
            <CardDescription>
              {eventsQuery.error instanceof Error
                ? eventsQuery.error.message
                : "The events endpoint failed."}
            </CardDescription>
          </CardHeader>
          <CardContent>
            <Button
              variant="outline"
              onClick={() => void eventsQuery.refetch()}
              disabled={eventsQuery.isFetching}
            >
              <RefreshCwIcon className={cn(eventsQuery.isFetching && "animate-spin")} />
              Retry
            </Button>
          </CardContent>
        </Card>
      )}

      {!eventsQuery.isLoading && !eventsQuery.isError && items.length === 0 && (
        <Card>
          <CardContent className="flex flex-col items-center justify-center gap-2 py-10 text-center">
            {suspectOnly ? (
              <ShieldCheckIcon className="size-8 text-muted-foreground" />
            ) : (
              <InboxIcon className="size-8 text-muted-foreground" />
            )}
            <p className="text-sm font-medium">
              {suspectOnly ? "No suspect events" : "No entrance events yet"}
            </p>
            <p className="max-w-sm text-xs text-muted-foreground">
              {suspectOnly
                ? "Motion events without a suspect are hidden by the current filter."
                : "Events appear here as soon as the camera and backend record motion."}
            </p>
          </CardContent>
        </Card>
      )}

      {items.length > 0 && (
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {items.map((item) => (
            <EventCard
              key={item.id}
              event={item}
              highlighted={newIds.has(item.id)}
              onOpen={setSelectedEvent}
            />
          ))}
        </div>
      )}

      {eventsQuery.hasNextPage && (
        <div className="flex flex-col items-center gap-2">
          <Button
            variant="outline"
            onClick={() => void eventsQuery.fetchNextPage()}
            disabled={eventsQuery.isFetchingNextPage}
          >
            {eventsQuery.isFetchingNextPage ? (
              <Loader2Icon className="animate-spin" />
            ) : (
              <ChevronDownIcon />
            )}
            {eventsQuery.isFetchingNextPage ? "Loading…" : "Load more"}
          </Button>
          <p className="text-xs text-muted-foreground">
            Showing {items.length} of {total}
          </p>
        </div>
      )}

      {!eventsQuery.hasNextPage && items.length > 0 && total > items.length && (
        <p className="text-center text-xs text-muted-foreground">
          Showing {items.length} of {total}
        </p>
      )}

      <EventDialog event={selectedEvent} onClose={() => setSelectedEvent(null)} />
    </section>
  );
}
