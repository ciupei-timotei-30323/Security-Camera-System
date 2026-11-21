import { AppShell } from "@/components/app-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { getProfile } from "@/lib/session";
import { redirect } from "next/navigation";

export const dynamic = "force-dynamic";

export default async function DashboardLayout({ children }: { children: React.ReactNode }) {
  let profile = null;
  let backendError: string | null = null;

  try {
    profile = await getProfile();
  } catch (error) {
    backendError =
      error instanceof Error ? error.message : "The camera backend is unreachable.";
  }

  if (backendError) {
    return (
      <main className="mx-auto flex min-h-dvh w-full max-w-lg items-center px-4">
        <Card className="w-full">
          <CardHeader>
            <CardTitle>Backend unavailable</CardTitle>
            <CardDescription>{backendError}</CardDescription>
          </CardHeader>
          <CardContent>
            <a className="text-sm font-medium underline underline-offset-4" href="/dashboard">
              Try again
            </a>
          </CardContent>
        </Card>
      </main>
    );
  }

  if (!profile) {
    redirect("/login");
  }

  return <AppShell user={profile}>{children}</AppShell>;
}
