import { handleLogin } from "@/lib/bff-login";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

export async function POST(request: Request): Promise<Response> {
  return handleLogin(request);
}
