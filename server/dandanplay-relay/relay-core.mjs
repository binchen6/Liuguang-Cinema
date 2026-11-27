const upstreamOrigin = "https://api.dandanplay.net";
const commentOrigin = "https://cas2.dandanplay.net";
const maxRequestBytes = 16 * 1024;
const maxResponseBytes = 12 * 1024 * 1024;
const encoder = new TextEncoder();

// Lightweight sliding-window in-memory rate limiter for platforms without native KV/RateLimit bindings
// (Tencent EdgeOne Pages, Alibaba ESA Pages, Laf, Sealos, FC/SCF, Node.js).
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

export function resetMemoryRateLimits() {
  memoryBuckets.clear();
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
  const reader = request.body?.getReader();
  if (!reader) throw new TypeError("Empty match request.");
  const buffer = new Uint8Array(maxRequestBytes);
  let length = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      if (length + value.byteLength > maxRequestBytes) {
        await reader.cancel();
        throw new RangeError("Match request too large.");
      }
      buffer.set(value, length);
      length += value.byteLength;
    }
  } finally {
    reader.releaseLock();
  }
  const payload = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(buffer.subarray(0, length)));
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
  return fetch(target, {
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

function extractClientIp(request, fallbackIp, requireNativeBindings) {
  const direct =
    request.headers.get("CF-Connecting-IP") ||
    request.headers.get("EO-Connecting-IP") ||
    request.headers.get("Ali-CDN-Real-IP") ||
    request.headers.get("Ali-Real-Client-IP") ||
    request.headers.get("X-Client-IP") ||
    request.headers.get("True-Client-IP") ||
    request.headers.get("X-Real-IP") ||
    request.headers.get("X-Forwarded-For")?.split(",")[0];
  const candidate = (direct ?? fallbackIp ?? "").trim();
  if (candidate.length > 0 && candidate.length <= 64) return candidate;
  return requireNativeBindings ? "" : "edge-anonymous";
}

async function checkRateLimits(env, clientIp, requireNativeBindings) {
  if (env.CLIENT_LIMIT && env.SERVICE_LIMIT) {
    const perClient = await env.CLIENT_LIMIT.limit({ key: clientIp });
    if (!perClient.success || !(await env.SERVICE_LIMIT.limit({ key: "service" })).success)
      return false;
    return true;
  }
  if (requireNativeBindings) {
    throw new Error("Missing native rate limit bindings");
  }
  const perClientMax = Number(env.CLIENT_RATE_LIMIT || 60);
  const serviceMax = Number(env.SERVICE_RATE_LIMIT || 600);
  return memoryAllow("c:" + clientIp, perClientMax, 60_000) && memoryAllow("svc", serviceMax, 60_000);
}

/**
 * Platform-agnostic DanDanPlay signing relay handler.
 * Works identically on Tencent EdgeOne Pages, Alibaba ESA Pages, Laf, Sealos, FC/SCF, Node.js, and Cloudflare Workers.
 * AppId / AppSecret remain strictly server-side in encrypted environment variables.
 */
export async function handleRelayRequest(request, env = {}, options = {}) {
  const requireNativeBindings = options.requireNativeBindings === true;
  const url = new URL(request.url);
  const appId = env.DANDANPLAY_APP_ID;
  const secret = env.DANDANPLAY_APP_SECRET;
  const bindingsReady = !requireNativeBindings || (Boolean(env.CLIENT_LIMIT) && Boolean(env.SERVICE_LIMIT));

  if (request.method === "GET" && url.pathname === "/health" && !url.search)
    return reply(appId && secret && bindingsReady ? 200 : 503, "");
  if (url.pathname.length + url.search.length > 2048) return reply(414, "请求地址过长。");

  const target = readRoute(request, url);
  if (!target) return reply(404, "该接口不可用或参数无效。");

  const client = extractClientIp(request, options.clientIp, requireNativeBindings);
  if (!client || !appId || !secret || !bindingsReady)
    return reply(503, "默认弹幕服务暂不可用。");

  try {
    const allowed = await checkRateLimits(env, client, requireNativeBindings);
    if (!allowed)
      return reply(429, "弹幕请求过于频繁，请稍后重试。", { "Retry-After": "60" });
  } catch {
    return reply(503, "默认弹幕服务暂不可用。");
  }

  let body;
  if (request.method === "POST") {
    try { body = await readMatchBody(request); }
    catch (error) { return reply(error instanceof RangeError ? 413 : 400, "文件匹配请求无效。"); }
  }

  try {
    const signal = createTimeoutSignal(target.pathname.startsWith("/api/v2/comment/") ? 22_000 : 12_000);
    let response = await signedRequest(request.method, target, body, appId, secret, signal);
    const backup = env.DANDANPLAY_BACKUP_SECRET || env.DANDANPLAY_APP_SECRET2;
    if ((response.status === 401 || response.status === 403) && backup && backup !== secret) {
      await response.body?.cancel();
      response = await signedRequest(request.method, target, body, appId, backup, signal);
    }
    const delivery = commentDelivery(response, target);
    if (delivery) {
      await response.body?.cancel();
      // When client requests direct CDN delivery, hand back only the validated official China CDN URL (~120 bytes)
      // so heavy comment JSON downloads straight from cas2.dandanplay.net without burning relay egress quota.
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
      response = await fetch(delivery, { redirect: "manual", signal, headers: { Accept: "application/json" } });
    }
    if (!response.ok) {
      await response.body?.cancel();
      return reply(response.status === 429 ? 429 : 502, "弹弹play 服务暂不可用。", {
        "X-Relay-Outcome": "upstream-" + response.status,
      });
    }
    if (!response.body || !/^application\/json(?:\s*;|$)/i.test(response.headers.get("Content-Type") ?? "") ||
        Number(response.headers.get("Content-Length")) > maxResponseBytes) {
      await response.body?.cancel();
      return reply(502, "弹弹play 返回了无法读取的响应。");
    }
    let size = 0;
    const bounded = response.body.pipeThrough(new TransformStream({
      transform(chunk, controller) {
        size += chunk.byteLength;
        if (size > maxResponseBytes) controller.error(new Error("Response exceeded the size limit."));
        else controller.enqueue(chunk);
      },
    }));
    return new Response(bounded, {
      headers: { "Content-Type": "application/json", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" },
    });
  } catch (error) {
    return reply(502, "弹弹play 服务暂不可用。", {
      "X-Relay-Outcome": error?.name === "TimeoutError" ? "timeout" : "upstream-unreachable",
    });
  }
}
