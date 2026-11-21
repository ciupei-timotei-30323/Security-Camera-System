"use client";

import {
  KeyRoundIcon,
  Loader2Icon,
  RefreshCwIcon,
  ShieldCheckIcon,
  Trash2Icon,
  UserPlusIcon,
  UserRoundIcon,
  UsersIcon,
} from "lucide-react";
import { FormEvent, useState } from "react";
import { useCurrentUser } from "@/components/auth-context";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { ApiError } from "@/lib/api";
import { useCreateUser, useDeleteUser, useUsers } from "@/lib/queries";
import type { Role, UserSummary } from "@/lib/types";
import { formatDateTime } from "@/lib/format";
import { cn } from "@/lib/utils";

function errorMessage(error: unknown): string | null {
  if (!error) {
    return null;
  }

  if (error instanceof ApiError) {
    return error.detail ? `${error.message} ${error.detail}` : error.message;
  }

  return error instanceof Error ? error.message : "The request failed.";
}

export default function AccountsPage() {
  const currentUser = useCurrentUser();
  const usersQuery = useUsers(true);
  const createMutation = useCreateUser();
  const deleteMutation = useDeleteUser();

  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState<Role>("User");
  const [createdMessage, setCreatedMessage] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<UserSummary | null>(null);

  function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setFormError(null);
    setCreatedMessage(null);

    const trimmedUsername = username.trim();

    if (!trimmedUsername || !password) {
      setFormError("Enter a username and password.");
      return;
    }

    createMutation.mutate(
      { username: trimmedUsername, password, role },
      {
        onSuccess: (created) => {
          setCreatedMessage(`Created ${created.username} (${created.role}).`);
          setUsername("");
          setPassword("");
          setRole("User");
        },
        onError: (error) => setFormError(errorMessage(error)),
      },
    );
  }

  const users = usersQuery.data ?? [];
  const createError = formError ?? errorMessage(createMutation.error);
  const deleteError = errorMessage(deleteMutation.error);

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-col gap-2 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Accounts</h1>
          <p className="text-sm text-muted-foreground">
            Manage sub-users for camera system {currentUser.systemId}.
          </p>
        </div>
        <Badge variant="outline" className="w-fit font-normal">
          <ShieldCheckIcon className="size-3" />
          Admin only
        </Badge>
      </div>

      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.6fr)]">
        <Card className="h-fit">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <UserPlusIcon className="size-4 text-muted-foreground" />
              Create sub-user
            </CardTitle>
            <CardDescription>
              Sub-users can view the dashboard and logs for this system only.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <form onSubmit={handleCreate} className="flex flex-col gap-4">
              <div className="flex flex-col gap-2">
                <Label htmlFor="new-username">Username</Label>
                <div className="relative">
                  <UserRoundIcon className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" />
                  <Input
                    id="new-username"
                    className="pl-8"
                    autoComplete="off"
                    autoCapitalize="none"
                    spellCheck={false}
                    value={username}
                    onChange={(event) => setUsername(event.target.value)}
                    disabled={createMutation.isPending}
                    required
                  />
                </div>
              </div>

              <div className="flex flex-col gap-2">
                <Label htmlFor="new-password">Password</Label>
                <div className="relative">
                  <KeyRoundIcon className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" />
                  <Input
                    id="new-password"
                    type="password"
                    className="pl-8"
                    autoComplete="new-password"
                    value={password}
                    onChange={(event) => setPassword(event.target.value)}
                    disabled={createMutation.isPending}
                    required
                  />
                </div>
              </div>

              <div className="flex flex-col gap-2">
                <Label htmlFor="new-role">Role</Label>
                <select
                  id="new-role"
                  value={role}
                  onChange={(event) => setRole(event.target.value as Role)}
                  disabled={createMutation.isPending}
                  className="h-8 w-full min-w-0 rounded-lg border border-input bg-transparent px-2.5 py-1 text-sm outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 dark:bg-input/30"
                >
                  <option value="User">User</option>
                  <option value="Admin">Admin</option>
                </select>
                <p className="text-xs text-muted-foreground">
                  Admins can also manage accounts and delete events.
                </p>
              </div>

              {createError && (
                <p
                  role="alert"
                  className="rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
                >
                  {createError}
                </p>
              )}

              {createdMessage && !createError && (
                <p
                  role="status"
                  className="rounded-lg border border-emerald-500/30 bg-emerald-500/10 p-3 text-sm text-emerald-700 dark:text-emerald-300"
                >
                  {createdMessage}
                </p>
              )}

              <Button type="submit" disabled={createMutation.isPending}>
                {createMutation.isPending ? (
                  <Loader2Icon className="animate-spin" />
                ) : (
                  <UserPlusIcon />
                )}
                {createMutation.isPending ? "Creating…" : "Create sub-user"}
              </Button>
            </form>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center justify-between gap-2">
              <span className="flex items-center gap-2">
                <UsersIcon className="size-4 text-muted-foreground" />
                Sub-users
              </span>
              <Badge variant="secondary">{usersQuery.data?.length ?? 0}</Badge>
            </CardTitle>
            <CardDescription>Accounts that can sign in to this camera system.</CardDescription>
          </CardHeader>
          <CardContent className="flex flex-col gap-3">
            {usersQuery.isLoading && (
              <>
                <Skeleton className="h-16 w-full" />
                <Skeleton className="h-16 w-full" />
                <Skeleton className="h-16 w-full" />
              </>
            )}

            {usersQuery.isError && (
              <div className="rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">
                <p>{errorMessage(usersQuery.error)}</p>
                <Button
                  variant="outline"
                  size="sm"
                  className="mt-2"
                  onClick={() => void usersQuery.refetch()}
                  disabled={usersQuery.isFetching}
                >
                  <RefreshCwIcon className={cn(usersQuery.isFetching && "animate-spin")} />
                  Retry
                </Button>
              </div>
            )}

            {deleteError && (
              <p
                role="alert"
                className="rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
              >
                {deleteError}
              </p>
            )}

            {!usersQuery.isLoading && !usersQuery.isError && users.length === 0 && (
              <div className="flex flex-col items-center justify-center gap-2 rounded-lg border border-dashed py-10 text-center">
                <UsersIcon className="size-8 text-muted-foreground" />
                <p className="text-sm font-medium">No sub-users yet</p>
                <p className="max-w-xs text-xs text-muted-foreground">
                  Create the first account with the form on the left.
                </p>
              </div>
            )}

            {users.map((user) => {
              const isSelf = user.username.toLowerCase() === currentUser.username.toLowerCase();

              return (
                <div
                  key={user.id}
                  className="flex flex-col gap-3 rounded-xl border p-3 sm:flex-row sm:items-center sm:justify-between"
                >
                  <div className="flex min-w-0 items-center gap-3">
                    <span className="flex size-9 shrink-0 items-center justify-center rounded-full bg-muted text-sm font-semibold uppercase">
                      {user.username.slice(0, 1)}
                    </span>
                    <div className="min-w-0">
                      <p className="flex flex-wrap items-center gap-2 text-sm font-medium">
                        <span className="truncate">{user.username}</span>
                        <Badge variant={user.role === "Admin" ? "default" : "secondary"}>
                          {user.role}
                        </Badge>
                        {isSelf && <Badge variant="outline">You</Badge>}
                      </p>
                      <p className="text-xs text-muted-foreground">
                        Created {formatDateTime(user.createdAtUtc)} · #{user.id}
                      </p>
                    </div>
                  </div>

                  <Button
                    variant="destructive"
                    size="sm"
                    className="w-fit"
                    disabled={isSelf || deleteMutation.isPending}
                    onClick={() => setDeleteTarget(user)}
                  >
                    <Trash2Icon />
                    Delete
                  </Button>
                </div>
              );
            })}
          </CardContent>
        </Card>
      </div>

      <AlertDialog
        open={Boolean(deleteTarget)}
        onOpenChange={(open) => {
          if (!open) {
            setDeleteTarget(null);
          }
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete sub-user?</AlertDialogTitle>
            <AlertDialogDescription>
              {deleteTarget
                ? `Delete “${deleteTarget.username}”? Their sessions stop working immediately and this cannot be undone.`
                : "Delete this sub-user?"}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={deleteMutation.isPending}>Cancel</AlertDialogCancel>
            <AlertDialogAction
              variant="destructive"
              disabled={deleteMutation.isPending}
              onClick={() => {
                if (!deleteTarget) {
                  return;
                }

                const target = deleteTarget;
                setDeleteTarget(null);
                deleteMutation.mutate(target.id);
              }}
            >
              {deleteMutation.isPending ? (
                <Loader2Icon className="animate-spin" />
              ) : (
                <Trash2Icon />
              )}
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
