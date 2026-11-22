import { serve } from "@hono/node-server";
import app from "./app.js";

const port = Number(process.env.PORT ?? 3001);
const hostname = "0.0.0.0";

serve({ fetch: app.fetch, port, hostname }, (info) => {
  console.log(`external-auth-service listening on http://${hostname}:${info.port}`);
});
