import { getProfile } from "@/lib/session";
import { redirect } from "next/navigation";

export default async function AccountsLayout({ children }: { children: React.ReactNode }) {
  const profile = await getProfile();

  if (!profile) {
    redirect("/login");
  }

  if (profile.role !== "Admin") {
    redirect("/dashboard");
  }

  return children;
}
