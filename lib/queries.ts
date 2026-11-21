"use client";

import {
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query";
import { apiBlob, apiFetch } from "@/lib/api";
import type {
  AwayModeSetting,
  CreateUserRequest,
  PagedEvents,
  SystemStatus,
  UpdateAwayModeRequest,
  UserSummary,
} from "@/lib/types";

const EVENTS_PAGE_SIZE = 12;

export const queryKeys = {
  systemStatus: ["system-status"] as const,
  events: (suspectOnly: boolean) => ["events", suspectOnly] as const,
  awayMode: ["away-mode"] as const,
  users: ["users"] as const,
  latestFrame: ["latest-frame"] as const,
};

export function useSystemStatus() {
  return useQuery({
    queryKey: queryKeys.systemStatus,
    queryFn: () => apiFetch<SystemStatus>("/api/system/status"),
    refetchInterval: 5_000,
    refetchIntervalInBackground: false,
  });
}

export function useEvents(suspectOnly: boolean) {
  return useInfiniteQuery({
    queryKey: queryKeys.events(suspectOnly),
    queryFn: ({ pageParam }) =>
      apiFetch<PagedEvents>(
        `/api/events?page=${pageParam}&pageSize=${EVENTS_PAGE_SIZE}&suspectOnly=${suspectOnly}`,
      ),
    initialPageParam: 1,
    getNextPageParam: (lastPage) =>
      lastPage.page * lastPage.pageSize < lastPage.total
        ? lastPage.page + 1
        : undefined,
    refetchInterval: 3_000,
    refetchIntervalInBackground: false,
  });
}

export function useLatestFrame(enabled: boolean) {
  return useQuery({
    queryKey: queryKeys.latestFrame,
    queryFn: () => apiBlob("/api/camera/latest"),
    enabled,
    refetchInterval: enabled ? 1_000 : false,
    retry: false,
    staleTime: 0,
  });
}

export function useAwayMode() {
  return useQuery({
    queryKey: queryKeys.awayMode,
    queryFn: () => apiFetch<AwayModeSetting>("/api/settings/away-mode"),
  });
}

export function useUpdateAwayMode() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (input: UpdateAwayModeRequest) =>
      apiFetch<AwayModeSetting>("/api/settings/away-mode", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(input),
      }),
    onSuccess: (setting) => {
      queryClient.setQueryData(queryKeys.awayMode, setting);
    },
  });
}

export function useUsers(enabled: boolean) {
  return useQuery({
    queryKey: queryKeys.users,
    queryFn: () => apiFetch<UserSummary[]>("/api/users"),
    enabled,
  });
}

export function useCreateUser() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (input: CreateUserRequest) =>
      apiFetch<UserSummary>("/api/users", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(input),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.users });
    },
  });
}

export function useDeleteUser() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (id: number) =>
      apiFetch<void>(`/api/users/${id}`, { method: "DELETE" }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.users });
    },
  });
}
