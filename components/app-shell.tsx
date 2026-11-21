"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useQueryClient } from "@tanstack/react-query";
import {
  CameraIcon,
  LayoutDashboardIcon,
  LogOutIcon,
  MenuIcon,
  ShieldCheckIcon,
  UserCogIcon,
  XIcon,
} from "lucide-react";
import { useState } from "react";
import { AuthProvider, useCurrentUser } from "@/components/auth-context";
import { ThemeToggle } from "@/components/theme-toggle";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { logoutRequest } from "@/lib/api";
import type { AuthUser } from "@/lib/types";
import { cn } from "@/lib/utils";

const navItems = [
  { href: "/dashboard", label: "Dashboard", icon: LayoutDashboardIcon },
  { href: "/settings", label: "Settings", icon: UserCogIcon },
  { href: "/accounts", label: "Accounts", icon: ShieldCheckIcon, adminOnly: true },
];

function NavLink({
  href,
  label,
  icon: Icon,
  active,
  onClick,
}: {
  href: string;
  label: string;
  icon: typeof LayoutDashboardIcon;
  active: boolean;
  onClick?: () => void;
}) {
  return (
    <Link
      href={href}
      onClick={onClick}
      aria-current={active ? "page" : undefined}
      className={cn(
        "inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-medium transition-colors",
        active
          ? "bg-secondary text-secondary-foreground"
          : "text-muted-foreground hover:bg-muted hover:text-foreground",
      )}
    >
      <Icon className="size-4" aria-hidden="true" />
      {label}
    </Link>
  );
}

function Shell({ children }: { children: React.ReactNode }) {
  const user = useCurrentUser();
  const pathname = usePathname();
  const router = useRouter();
  const queryClient = useQueryClient();
  const [mobileOpen, setMobileOpen] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);

  const visibleItems = navItems.filter((item) => !item.adminOnly || user.role === "Admin");

  async function handleLogout() {
    setLoggingOut(true);

    try {
      await logoutRequest();
    } catch {
      // The cookie is cleared by the BFF even when backend revocation fails.
    } finally {
      queryClient.clear();
      router.replace("/login");
      router.refresh();
    }
  }

  return (
    <div className="flex min-h-dvh flex-col">
      <header className="sticky top-0 z-40 border-b bg-background/90 backdrop-blur supports-backdrop-filter:bg-background/70">
        <div className="mx-auto flex h-14 w-full max-w-7xl items-center gap-2 px-3 sm:px-6">
          <Link href="/dashboard" className="flex min-w-0 items-center gap-2">
            <span className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-primary text-primary-foreground">
              <CameraIcon className="size-4" aria-hidden="true" />
            </span>
            <span className="hidden min-w-0 flex-col leading-tight sm:flex">
              <span className="truncate text-sm font-semibold">Security Camera</span>
              <span className="truncate text-xs text-muted-foreground">{user.systemId}</span>
            </span>
          </Link>

          <nav className="ml-2 hidden items-center gap-1 md:flex" aria-label="Main navigation">
            {visibleItems.map((item) => (
              <NavLink
                key={item.href}
                href={item.href}
                label={item.label}
                icon={item.icon}
                active={pathname === item.href || pathname.startsWith(`${item.href}/`)}
              />
            ))}
          </nav>

          <div className="ml-auto flex items-center gap-1">
            <ThemeToggle />
            <div className="hidden items-center gap-2 pl-1 sm:flex">
              <span className="text-sm font-medium">{user.username}</span>
              <Badge variant="secondary">{user.role}</Badge>
            </div>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              className="hidden md:inline-flex"
              onClick={handleLogout}
              disabled={loggingOut}
            >
              <LogOutIcon />
              {loggingOut ? "Signing out…" : "Log off"}
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="icon"
              className="md:hidden"
              aria-label={mobileOpen ? "Close navigation" : "Open navigation"}
              aria-expanded={mobileOpen}
              onClick={() => setMobileOpen((open) => !open)}
            >
              {mobileOpen ? <XIcon /> : <MenuIcon />}
            </Button>
          </div>
        </div>

        {mobileOpen && (
          <nav
            className="border-t bg-background px-3 py-3 md:hidden"
            aria-label="Mobile navigation"
          >
            <div className="flex flex-col gap-1">
              {visibleItems.map((item) => (
                <NavLink
                  key={item.href}
                  href={item.href}
                  label={item.label}
                  icon={item.icon}
                  active={pathname === item.href || pathname.startsWith(`${item.href}/`)}
                  onClick={() => setMobileOpen(false)}
                />
              ))}
              <div className="mt-2 flex items-center justify-between gap-2 border-t pt-3">
                <div className="flex min-w-0 items-center gap-2">
                  <span className="truncate text-sm font-medium">{user.username}</span>
                  <Badge variant="secondary">{user.role}</Badge>
                </div>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={handleLogout}
                  disabled={loggingOut}
                >
                  <LogOutIcon />
                  Log off
                </Button>
              </div>
            </div>
          </nav>
        )}
      </header>

      <main className="mx-auto w-full max-w-7xl flex-1 px-3 py-5 sm:px-6 sm:py-8">
        {children}
      </main>

      <footer className="border-t py-4">
        <div className="mx-auto flex w-full max-w-7xl flex-col gap-1 px-3 text-xs text-muted-foreground sm:flex-row sm:items-center sm:justify-between sm:px-6">
          <span>{user.systemId}</span>
          <span>Live monitoring · auto-refresh enabled</span>
        </div>
      </footer>
    </div>
  );
}

export function AppShell({ user, children }: { user: AuthUser; children: React.ReactNode }) {
  return (
    <AuthProvider user={user}>
      <Shell>{children}</Shell>
    </AuthProvider>
  );
}
