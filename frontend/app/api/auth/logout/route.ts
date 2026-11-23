import { handleLogout } from "@/lib/bff-logout";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

export async function POST(request: Request): Promise<Response> {
  return handleLogout(request);
}
