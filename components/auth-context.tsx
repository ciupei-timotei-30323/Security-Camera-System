"use client";

import { createContext, useContext } from "react";
import type { AuthUser } from "@/lib/types";

const AuthContext = createContext<AuthUser | null>(null);

export function AuthProvider({
  user,
  children,
}: {
  user: AuthUser;
  children: React.ReactNode;
}) {
  return <AuthContext.Provider value={user}>{children}</AuthContext.Provider>;
}

export function useCurrentUser(): AuthUser {
  const user = useContext(AuthContext);

  if (!user) {
    throw new Error("useCurrentUser must be used inside AuthProvider.");
  }

  return user;
}
