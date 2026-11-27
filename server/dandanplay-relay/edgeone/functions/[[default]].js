import { handleRelayRequest } from "../../relay-core.mjs";

/**
 * Tencent EdgeOne Pages Functions catch-all entry point (`functions/[[default]].js`).
 * Free tier with GitHub auto-deploy and China/Asia edge routing.
 * Configure DANDANPLAY_APP_ID, DANDANPLAY_APP_SECRET (and optional DANDANPLAY_BACKUP_SECRET)
 * as encrypted Environment Variables in the EdgeOne Pages console.
 */
export async function onRequest(context) {
  const { request, env } = context;
  const fallbackIp = context.clientIp || request.headers.get("EO-Connecting-IP") || "";
  return handleRelayRequest(request, env ?? {}, { clientIp: fallbackIp });
}
