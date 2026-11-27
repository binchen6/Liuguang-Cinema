/**
 * Alibaba Cloud ESA Edge Routine (边缘函数) self-contained single-file DanDanPlay signing relay.
 * Zero external imports — paste directly into ESA Console -> 函数和 Pages -> 代码编辑.
 * Configure DANDANPLAY_APP_ID, DANDANPLAY_APP_SECRET (and optional DANDANPLAY_BACKUP_SECRET)
 * in ESA Console -> 设置 -> 环境变量 (encrypted). Never hardcode secrets in source code.
 */
const upstreamOrigin = "https://api.dandanplay.net";
const commentOrigin = "https://cas2.dandanplay.net";
const maxRequestBytes = 16 * 1024;
const maxResponseBytes = 12 * 1024 * 1024;
const encoder = new TextEncoder();
const memoryBuckets = new Map();
const MAX_TRACKED_CLIENTS = 4096;

function memoryAllow(key, maxRequests, windowMs, now = Date.now()) {
  if (memoryBuckets.size > MAX_TRACKED_CLIENTS) {
    for (const [k, entry] of memoryBuckets) {
      if (now - entry.windowStart >= windowMs) memoryBuckets.delete(k);
    }
    if (memoryBuckets.size > MAX_TRACKED_CLIENTS) memoryBuckets.clear();
  }
  const entry = memoryBuckets.get(key);
  if (!entry || now - entry.windowStart >= windowMs) {
    memoryBuckets.set(key, { windowStart: now, count: 1 });
    return true;
  }
  if (entry.count >= maxRequests) return false;
  entry.count++;
  return true;
}

function createTimeoutSignal(ms) {
  if (typeof AbortSignal !== "undefined" && typeof AbortSignal.timeout === "function") {
    return AbortSignal.timeout(ms);
  }
  if (typeof AbortController !== "undefined") {
    const controller = new AbortController();
    setTimeout(() => controller.abort(), ms);
    return controller.signal;
  }
  return undefined;
}

function reply(status, message, headers = {}) {
  return new Response(
    JSON.stringify({ success: status === 200, errorMessage: message }),
    {
      status,
      headers: {
        "Content-Type": "application/json; charset=utf-8",
        "Cache-Control": "no-store",
        "X-Content-Type-Options": "nosniff",
        ...headers,
      },
    },
  );
}

function readRoute(request, url) {
  if (request.method === "GET" && url.pathname === "/api/v2/search/episodes") {
    const names = url.searchParams.getAll("anime");
    const name = names[0]?.trim();
    if (names.length !== 1 || !name || name.length > 160) return null;
    const query = new URLSearchParams({ anime: name });
    for (const [key, pattern] of [["episode", /^[1-9]\d{0,3}$/], ["v2", /^(true|false)$/]]) {
      const values = url.searchParams.getAll(key);
      if (values.length === 0) continue;
      if (values.length !== 1 || !pattern.test(values[0])) return null;
      query.set(key, values[0]);
    }
    if (query.size !== url.searchParams.size) return null;
    return new URL(url.pathname + "?" + query, upstreamOrigin);
  }
  if (request.method === "GET" && /^\/api\/v2\/comment\/[1-9]\d{0,18}$/.test(url.pathname)) {
    const related = url.searchParams.getAll("withRelated");
    const convert = url.searchParams.getAll("chConvert");
    if (url.searchParams.size !== 2 || related.length !== 1 || convert.length !== 1 ||
        !["true", "false"].includes(related[0]) || !["0", "1", "2"].includes(convert[0])) return null;
    return new URL(url.pathname + "?" + new URLSearchParams({ withRelated: related[0], chConvert: convert[0] }), upstreamOrigin);
  }
  if (request.method === "POST" && url.pathname === "/api/v2/match" && !url.search)
    return new URL(url.pathname, upstreamOrigin);
  return null;
}

async function readMatchBody(request) {
  if (!/^application\/json(?:\s*;|$)/i.test(request.headers.get("Content-Type") ?? ""))
    throw new TypeError("Expected a JSON match request.");
  if (Number(request.headers.get("Content-Length")) > maxRequestBytes)
    throw new RangeError("Match request too large.");
  const text = await request.text();
  if (encoder.encode(text).byteLength > maxRequestBytes)
    throw new RangeError("Match request too large.");
  const payload = JSON.parse(text);
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) throw new TypeError("Invalid match request.");
  const { fileName, fileHash, fileSize, videoDuration, matchMode } = payload;
  if (typeof fileName !== "string" || fileName.trim().length < 2 || fileName.length > 240 ||
      /[\u0000-\u001f/\\]/.test(fileName) || typeof fileHash !== "string" || !/^[a-f0-9]{32}$/i.test(fileHash) ||
      !Number.isSafeInteger(fileSize) || fileSize < 0 ||
      !Number.isInteger(videoDuration) || videoDuration < 0 || videoDuration > 86_400 ||
      !["hashAndFileName", "fileNameOnly"].includes(matchMode))
    throw new TypeError("Invalid match request.");
  return JSON.stringify({ fileName, fileHash, fileSize, videoDuration, matchMode });
}

async function signedRequest(method, target, body, appId, appSecret, signal) {
  const timestamp = Math.floor(Date.now() / 1000).toString();
  const digest = await crypto.subtle.digest("SHA-256", encoder.encode(appId + timestamp + target.pathname + appSecret));
  return fetch(target.href, {
    method,
    body,
    redirect: "manual",
    signal,
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json",
      "User-Agent": "LiuguangCinema/1.9 (DanDanPlay-OpenAPI)",
      "X-AppId": appId,
      "X-Timestamp": timestamp,
      "X-Signature": btoa(String.fromCharCode(...new Uint8Array(digest))),
    },
  });
}

function commentDelivery(response, target) {
  const path = target.pathname;
  const location = response.headers.get("Location");
  if (response.status !== 302 || !path.startsWith("/api/v2/comment/") || !location || location.length > 4096) return null;
  try {
    const url = new URL(location, target);
    return url.origin === commentOrigin && !url.username && !url.password && !url.hash &&
      url.pathname === path.replace("/api/v2/", "/api/") ? url : null;
  } catch { return null; }
}

function extractClientIp(request) {
  const direct =
    request.headers.get("Ali-CDN-Real-IP") ||
    request.headers.get("Ali-Real-Client-IP") ||
    request.headers.get("X-Client-IP") ||
    request.headers.get("True-Client-IP") ||
    request.headers.get("X-Real-IP") ||
    request.headers.get("X-Forwarded-For")?.split(",")[0];
  const candidate = (direct ?? "").trim();
  return candidate.length > 0 && candidate.length <= 64 ? candidate : "edge-anonymous";
}

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
    const url = new URL(request.url);
    const appId = runtimeEnv.DANDANPLAY_APP_ID;
    const secret = runtimeEnv.DANDANPLAY_APP_SECRET;

    if (request.method === "GET" && url.pathname === "/health" && !url.search)
      return reply(appId && secret ? 200 : 503, "");
    if (url.pathname.length + url.search.length > 2048) return reply(414, "请求地址过长。");

    const target = readRoute(request, url);
    if (!target) return reply(404, "该接口不可用或参数无效。");
    if (!appId || !secret) return reply(503, "默认弹幕服务暂不可用。");

    const client = extractClientIp(request);
    const perClientMax = Number(runtimeEnv.CLIENT_RATE_LIMIT || 60);
    const serviceMax = Number(runtimeEnv.SERVICE_RATE_LIMIT || 600);
    if (!memoryAllow("c:" + client, perClientMax, 60_000) || !memoryAllow("svc", serviceMax, 60_000))
      return reply(429, "弹幕请求过于频繁，请稍后重试。", { "Retry-After": "60" });

    let body;
    if (request.method === "POST") {
      try { body = await readMatchBody(request); }
      catch (error) { return reply(error instanceof RangeError ? 413 : 400, "文件匹配请求无效。"); }
    }

    try {
      const signal = createTimeoutSignal(target.pathname.startsWith("/api/v2/comment/") ? 22_000 : 12_000);
      let response = await signedRequest(request.method, target, body, appId, secret, signal);
      const backup = runtimeEnv.DANDANPLAY_BACKUP_SECRET || runtimeEnv.DANDANPLAY_APP_SECRET2;
      if ((response.status === 401 || response.status === 403) && backup && backup !== secret) {
        await response.body?.cancel?.();
        response = await signedRequest(request.method, target, body, appId, backup, signal);
      }
      const delivery = commentDelivery(response, target);
      if (delivery) {
        await response.body?.cancel?.();
        if (request.headers.get("X-Relay-Delivery") === "direct") {
          return new Response(
            JSON.stringify({ success: true, deliveryUrl: delivery.href }),
            {
              status: 200,
              headers: {
                "Content-Type": "application/json; charset=utf-8",
                "Cache-Control": "no-store",
                "X-Content-Type-Options": "nosniff",
              },
            },
          );
        }
        response = await fetch(delivery.href, { redirect: "manual", signal, headers: { Accept: "application/json" } });
      }
      if (!response.ok) {
        await response.body?.cancel?.();
        return reply(response.status === 429 ? 429 : 502, "弹弹play 服务暂不可用。", {
          "X-Relay-Outcome": "upstream-" + response.status,
        });
      }
      const contentType = response.headers.get("Content-Type") ?? "";
      if (!/^application\/json(?:\s*;|$)/i.test(contentType) || Number(response.headers.get("Content-Length")) > maxResponseBytes) {
        await response.body?.cancel?.();
        return reply(502, "弹弹play 返回了无法读取的响应。");
      }
      const payloadText = await response.text();
      if (encoder.encode(payloadText).byteLength > maxResponseBytes) {
        return reply(502, "弹弹play 返回了无法读取的响应。");
      }
      return new Response(payloadText, {
        status: 200,
        headers: {
          "Content-Type": "application/json; charset=utf-8",
          "Cache-Control": "no-store",
          "X-Content-Type-Options": "nosniff",
        },
      });
    } catch (error) {
      return reply(502, "弹弹play 服务暂不可用。", {
        "X-Relay-Outcome": error?.name === "TimeoutError" ? "timeout" : "upstream-unreachable",
      });
    }
  },
};
