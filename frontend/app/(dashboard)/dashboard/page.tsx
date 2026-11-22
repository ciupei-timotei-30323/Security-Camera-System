"use client";

import { RadioTowerIcon } from "lucide-react";
import { useCurrentUser } from "@/components/auth-context";
import { EntranceLog } from "@/components/entrance-log";
import { LiveFeed } from "@/components/live-feed";
import { StatusPanel } from "@/components/status-panel";
import { Badge } from "@/components/ui/badge";

export default function DashboardPage() {
  const user = useCurrentUser();

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-col gap-2 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Dashboard</h1>
          <p className="text-sm text-muted-foreground">
            Live view, device health and the latest entrance events.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant="outline" className="font-normal">
            <RadioTowerIcon className="size-3" />
            {user.systemId}
          </Badge>
          <Badge variant="secondary" className="font-normal">
            {user.role}
          </Badge>
        </div>
      </div>

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1.9fr)_minmax(0,1fr)]">
        <LiveFeed />
        <StatusPanel />
      </div>

      <EntranceLog />
    </div>
  );
}
