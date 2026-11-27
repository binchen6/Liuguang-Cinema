// Laf (https://laf.run) free China-direct Cloud Function adapter for Liuguang Cinema DanDanPlay Relay.
// 1. Create a free app on laf.run (mainland China direct-connect, NO custom domain / ICP required!).
// 2. Add Environment Variables in Laf Console:
//    - DANDANPLAY_APP_ID
//    - DANDANPLAY_APP_SECRET
//    - DANDANPLAY_BACKUP_SECRET (optional)
// 3. Create a cloud function named `dandanplay` (or catch-all HTTP route) and paste this handler.
// 4. Put the Laf HTTPS function base URL into GitHub Actions variable `DANDANPLAY_RELAY_URL`
//    (or into Liuguang Cinema Settings -> 弹幕与 AI -> 弹幕签名中继地址).

import crypto from "crypto";

const UPSTREAM = "https://api.dandanplay.net";
const COMMENT_ORIGIN = "https://cas2.dandanplay.net";
const RATE_BUCKETS = new Map<string, { start: number; count: number }>();

function allowRate(key: string, max: number, windowMs: number): boolean {
  const now = Date.now();
  if (RATE_BUCKETS.size > 4096) RATE_BUCKETS.clear();
  const cur = RATE_BUCKETS.get(key);
  if (!cur || now - cur.start >= windowMs) {
    RATE_BUCKETS.set(key, { start: now, count: 1 });
    return true;
  }
  if (cur.count >= max) return false;
  cur.count++;
  return true;
}

export default async function (ctx: FunctionContext) {
  const req = ctx.request;
  const res = ctx.response;
  const appId = process.env.DANDANPLAY_APP_ID || "";
  const secret = process.env.DANDANPLAY_APP_SECRET || "";
  const backup = process.env.DANDANPLAY_BACKUP_SECRET || "";

  const rawPath = String(req.path || "/").replace(/^\/dandanplay(?=\/|$)/, "") || "/";
  if (req.method === "GET" && rawPath === "/health") {
    return res.status(appId && secret ? 200 : 503).json({ success: Boolean(appId && secret), errorMessage: "" });
  }
  if (!appId || !secret) {
    return res.status(503).json({ success: false, errorMessage: "默认弹幕服务暂不可用。" });
  }

  const clientIp = String(req.headers["x-real-ip"] || req.headers["x-forwarded-for"] || req.ip || "unknown").split(",")[0].trim();
  if (!allowRate("c:" + clientIp, 60, 60_000) || !allowRate("svc", 600, 60_000)) {
    return res.status(429).json({ success: false, errorMessage: "弹幕请求过于频繁，请稍后重试。" });
  }

  const allowedSearch = req.method === "GET" && rawPath === "/api/v2/search/episodes";
  const allowedComment = req.method === "GET" && /^\/api\/v2\/comment\/[1-9]\d{0,18}$/.test(rawPath);
  const allowedMatch = req.method === "POST" && rawPath === "/api/v2/match";
  if (!allowedSearch && !allowedComment && !allowedMatch) {
    return res.status(404).json({ success: false, errorMessage: "该接口不可用或参数无效。" });
  }

  const query = new URLSearchParams();
  if (allowedSearch) {
    const anime = String(ctx.query?.anime ?? "").trim();
    if (!anime || anime.length > 160) {
      return res.status(404).json({ success: false, errorMessage: "该接口不可用或参数无效。" });
    }
    query.set("anime", anime);
    if (ctx.query?.episode !== undefined) {
      const ep = String(ctx.query.episode);
      if (!/^[1-9]\d{0,3}$/.test(ep)) return res.status(404).json({ success: false, errorMessage: "该接口不可用或参数无效。" });
      query.set("episode", ep);
    }
    if (ctx.query?.v2 !== undefined) {
      const v2 = String(ctx.query.v2);
      if (v2 !== "true" && v2 !== "false") return res.status(404).json({ success: false, errorMessage: "该接口不可用或参数无效。" });
      query.set("v2", v2);
    }
  } else if (allowedComment) {
    const withRelated = String(ctx.query?.withRelated ?? "");
    const chConvert = String(ctx.query?.chConvert ?? "");
    if (!["true", "false"].includes(withRelated) || !["0", "1", "2"].includes(chConvert)) {
      return res.status(404).json({ success: false, errorMessage: "该接口不可用或参数无效。" });
    }
    query.set("withRelated", withRelated);
    query.set("chConvert", chConvert);
  }
  const target = new URL(rawPath + (query.size > 0 ? "?" + query.toString() : ""), UPSTREAM);

  let body: string | undefined;
  if (allowedMatch) {
    const payload = ctx.body && typeof ctx.body === "object" && !Array.isArray(ctx.body) ? ctx.body : null;
    if (!payload) return res.status(400).json({ success: false, errorMessage: "文件匹配请求无效。" });
    const { fileName, fileHash, fileSize, videoDuration, matchMode } = payload;
    if (typeof fileName !== "string" || fileName.trim().length < 2 || fileName.length > 240 ||
        /[\u0000-\u001f/\\]/.test(fileName) || typeof fileHash !== "string" || !/^[a-f0-9]{32}$/i.test(fileHash) ||
        !Number.isSafeInteger(fileSize) || fileSize < 0 ||
        !Number.isInteger(videoDuration) || videoDuration < 0 || videoDuration > 86_400 ||
        !["hashAndFileName", "fileNameOnly"].includes(matchMode)) {
      return res.status(400).json({ success: false, errorMessage: "文件匹配请求无效。" });
    }
    body = JSON.stringify({ fileName, fileHash, fileSize, videoDuration, matchMode });
  }

  const signal = AbortSignal.timeout(allowedComment ? 22_000 : 12_000);

  async function callUpstream(activeSecret: string) {
    const ts = Math.floor(Date.now() / 1000).toString();
    const sig = crypto.createHash("sha256").update(appId + ts + target.pathname + activeSecret).digest("base64");
    return fetch(target, {
      method: req.method,
      body,
      redirect: "manual",
      signal,
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        "User-Agent": "LiuguangCinema/1.9 (DanDanPlay-OpenAPI)",
        "X-AppId": appId,
        "X-Timestamp": ts,
        "X-Signature": sig,
      },
    });
  }

  try {
    let upstream = await callUpstream(secret);
    if ((upstream.status === 401 || upstream.status === 403) && backup && backup !== secret) {
      upstream = await callUpstream(backup);
    }
    if (upstream.status === 302 && allowedComment) {
      const loc = upstream.headers.get("Location") || "";
      const delivery = new URL(loc, target);
      if (delivery.origin === COMMENT_ORIGIN && !delivery.username && !delivery.password && !delivery.hash &&
          loc.length <= 4096 && delivery.pathname === target.pathname.replace("/api/v2/", "/api/")) {
        if (req.headers["x-relay-delivery"] === "direct") {
          res.setHeader("Cache-Control", "no-store");
          res.setHeader("X-Content-Type-Options", "nosniff");
          return res.status(200).json({ success: true, deliveryUrl: delivery.href });
        }
        upstream = await fetch(delivery, { redirect: "manual", signal, headers: { Accept: "application/json" } });
      }
    }
    if (!upstream.ok) {
      return res.status(upstream.status === 429 ? 429 : 502).json({ success: false, errorMessage: "弹弹play 服务暂不可用。" });
    }
    const text = await upstream.text();
    res.setHeader("Content-Type", "application/json");
    res.setHeader("Cache-Control", "no-store");
    res.setHeader("X-Content-Type-Options", "nosniff");
    return res.status(200).send(text);
  } catch {
    return res.status(502).json({ success: false, errorMessage: "弹弹play 服务暂不可用。" });
  }
}
