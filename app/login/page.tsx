import { redirect } from "next/navigation";
import { LoginForm } from "@/app/login/login-form";
import { getProfile } from "@/lib/session";

export const dynamic = "force-dynamic";

export default async function LoginPage({
  searchParams,
}: {
  searchParams: Promise<{ reason?: string; next?: string }>;
}) {
  const params = await searchParams;
  let profile = null;

  try {
    profile = await getProfile();
  } catch {
    // Leave the form usable so the user can retry once the backend is back.
  }

  if (profile) {
    redirect("/dashboard");
  }

  const nextPath =
    params.next?.startsWith("/") && !params.next.startsWith("//") ? params.next : null;

  return <LoginForm expired={params.reason === "expired"} nextPath={nextPath} />;
}
