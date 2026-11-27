import { handleRelayRequest } from "../relay-core.mjs";

/**
 * Alibaba Cloud ESA Pages / Edge Routine entry point.
 * Free Entrance tier with GitHub auto-deploy and 3200+ China/Asia edge nodes.
 * Configure DANDANPLAY_APP_ID, DANDANPLAY_APP_SECRET (and optional DANDANPLAY_BACKUP_SECRET)
 * as Environment Variables in the Alibaba Cloud ESA Pages console.
 */
function pickEnv(a, b, c, d, e) {
  const v = a ?? b ?? c ?? d ?? e;
  return v !== undefined && v !== null && v !== "" ? String(v).trim() : "";
}

export default {
  // Alibaba Cloud ESA Edge Routine passes (request, context, env) with lazy static parsing of env.<KEY>.
  async fetch(request, context, env) {
    const p = typeof process !== "undefined" && process.env ? process.env : undefined;
    const runtimeEnv = {
      DANDANPLAY_APP_ID: pickEnv(
        env ? env.DANDANPLAY_APP_ID : undefined,
        context ? context.DANDANPLAY_APP_ID : undefined,
        context && context.env ? context.env.DANDANPLAY_APP_ID : undefined,
        p ? p.DANDANPLAY_APP_ID : undefined,
        globalThis.DANDANPLAY_APP_ID,
      ),
      DANDANPLAY_APP_SECRET: pickEnv(
        env ? env.DANDANPLAY_APP_SECRET : undefined,
        context ? context.DANDANPLAY_APP_SECRET : undefined,
        context && context.env ? context.env.DANDANPLAY_APP_SECRET : undefined,
        p ? p.DANDANPLAY_APP_SECRET : undefined,
        globalThis.DANDANPLAY_APP_SECRET,
      ),
      DANDANPLAY_BACKUP_SECRET: pickEnv(
        env ? env.DANDANPLAY_BACKUP_SECRET : undefined,
        context ? context.DANDANPLAY_BACKUP_SECRET : undefined,
        context && context.env ? context.env.DANDANPLAY_BACKUP_SECRET : undefined,
        p ? p.DANDANPLAY_BACKUP_SECRET : undefined,
        globalThis.DANDANPLAY_BACKUP_SECRET,
      ),
      DANDANPLAY_APP_SECRET2: pickEnv(
        env ? env.DANDANPLAY_APP_SECRET2 : undefined,
        context ? context.DANDANPLAY_APP_SECRET2 : undefined,
        context && context.env ? context.env.DANDANPLAY_APP_SECRET2 : undefined,
        p ? p.DANDANPLAY_APP_SECRET2 : undefined,
        globalThis.DANDANPLAY_APP_SECRET2,
      ),
      CLIENT_RATE_LIMIT: pickEnv(
        env ? env.CLIENT_RATE_LIMIT : undefined,
        context ? context.CLIENT_RATE_LIMIT : undefined,
        context && context.env ? context.env.CLIENT_RATE_LIMIT : undefined,
        p ? p.CLIENT_RATE_LIMIT : undefined,
        globalThis.CLIENT_RATE_LIMIT,
      ),
      SERVICE_RATE_LIMIT: pickEnv(
        env ? env.SERVICE_RATE_LIMIT : undefined,
        context ? context.SERVICE_RATE_LIMIT : undefined,
        context && context.env ? context.env.SERVICE_RATE_LIMIT : undefined,
        p ? p.SERVICE_RATE_LIMIT : undefined,
        globalThis.SERVICE_RATE_LIMIT,
      ),
    };
    return handleRelayRequest(request, runtimeEnv, {});
  },
};
