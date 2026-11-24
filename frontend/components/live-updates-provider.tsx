"use client";

import { createContext, useContext, useEffect, useRef, useState, useSyncExternalStore } from "react";
import {
  LiveUpdatesController,
  type LiveUpdatesState,
} from "@/lib/live-updates";

/**
 * One broker connection per dashboard tab.
 *
 * Every live widget (alarm banner, status panel, entrance log) reads the same controller, so the
 * dashboard opens exactly one MQTT-over-WebSockets connection instead of one per component. When
 * the broker is not configured the controller reports `unavailable` and each widget keeps its
 * existing polling behaviour.
 */

const LiveUpdatesContext = createContext<LiveUpdatesState | null>(null);

export interface LiveUpdatesProviderProps {
  children: React.ReactNode;
  /** Test/embedding seam: inject a controller instead of letting the provider create one. */
  controllerFactory?: () => LiveUpdatesController;
}

export function LiveUpdatesProvider({ children, controllerFactory }: LiveUpdatesProviderProps) {
  const [controller] = useState(() => controllerFactory?.() ?? new LiveUpdatesController());
  const state = useSyncExternalStore(
    controller.subscribe,
    controller.getSnapshot,
    controller.getSnapshot,
  );

  useEffect(() => {
    void controller.start();

    return () => controller.stop();
  }, [controller]);

  return (
    <LiveUpdatesContext.Provider value={state}>{children}</LiveUpdatesContext.Provider>
  );
}

/** The live state, or `null` when the dashboard is rendered outside the provider. */
export function useLiveUpdatesState(): LiveUpdatesState | null {
  return useContext(LiveUpdatesContext);
}

/**
 * Calls `onEvents` whenever a new detection summary arrives. Components that want to refresh a
 * query use this instead of polling, and fall back to their own interval when the broker is
 * unavailable.
 */
export function useLiveEvents(handler: () => void): void {
  const state = useLiveUpdatesState();
  const handlerRef = useRef(handler);
  const received = state?.eventsReceived ?? 0;
  const previous = useRef(received);

  // Keep the latest handler without making it an effect dependency: callers pass an inline
  // closure, so a dependency on it would re-subscribe on every render.
  useEffect(() => {
    handlerRef.current = handler;
  }, [handler]);

  useEffect(() => {
    if (received === previous.current) {
      return;
    }

    previous.current = received;
    handlerRef.current();
  }, [received]);
}
