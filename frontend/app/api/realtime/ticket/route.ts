import { handleRealtimeTicket } from "@/lib/realtime-ticket";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

/**
 * Tenant-scoped realtime credentials for the browser: WHEP URL + stream token, and the MQTT-over-
 * WebSockets broker URL. Served by the BFF itself (not proxied to the backend) so the ticket is
 * derived strictly from the sealed `bff_session` cookie.
 */
export async function GET(request: Request): Promise<Response> {
  return handleRealtimeTicket(request);
}
