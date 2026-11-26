import { afterEach, test } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import worker from "./worker.mjs";

const realFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = realFetch; });
const allow = { limit: async () => ({ success: true }) };
const credentials = {
  DANDANPLAY_APP_ID: "fixture-app",
  DANDANPLAY_APP_SECRET: "fixture-primary",
  DANDANPLAY_BACKUP_SECRET: "fixture-backup",
  CLIENT_LIMIT: allow,
  SERVICE_LIMIT: allow,
};

function request(path, options = {}) {
  return new Request("https://relay.example" + path, {
    ...options,
    headers: { "CF-Connecting-IP": "192.0.2.1", ...options.headers },
  });
}

test("keeps both legacy credentials on the server and signs only the allowed upstream request", async () => {
  const signatures = [];
  globalThis.fetch = async (url, options) => {
    assert.equal(url.origin, "https://api.dandanplay.net");
    assert.equal(url.pathname, "/api/v2/search/episodes");
    assert.equal(url.searchParams.get("anime"), "测试");
    assert.equal(url.searchParams.get("episode"), "2");
    assert.equal(url.searchParams.get("v2"), "true");
    assert.equal(options.redirect, "manual");
    assert.equal(options.headers["X-AppId"], "fixture-app");
    assert.equal(options.headers.Cookie, undefined);
    const secret = signatures.length === 0 ? "fixture-primary" : "fixture-backup";
    const digest = createHash("sha256")
      .update("fixture-app" + options.headers["X-Timestamp"] + url.pathname + secret)
      .digest("base64");
    assert.equal(options.headers["X-Signature"], digest);
    signatures.push(digest);
    return signatures.length === 1 ? new Response(null, { status: 403 }) : Response.json({ success: true, animes: [] });
  };
  const response = await worker.fetch(request("/api/v2/search/episodes?anime=%E6%B5%8B%E8%AF%95&episode=2&v2=true", { headers: { Cookie: "fixture=local" } }), credentials);
  assert.deepEqual(await response.json(), { success: true, animes: [] });
  assert.equal(signatures.length, 2);
  assert.equal(response.headers.get("X-Signature"), null);
  assert.equal(response.headers.get("Cache-Control"), "no-store");
});

test("rejects invalid match bodies, arbitrary routes, and rate-limited traffic before contacting the upstream", async () => {
  let calls = 0;
  globalThis.fetch = async () => { calls++; return Response.json({ success: true }); };
  assert.equal((await worker.fetch(request("/api/v2/comment/1"), credentials)).status, 404);
  assert.equal((await worker.fetch(request("/api/v2/search/episodes?anime=fixture&anime=extra"), credentials)).status, 404);
  assert.equal((await worker.fetch(request("/api/v2/match", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: '{"fileName":"fixture"}',
  }), credentials)).status, 400);
  assert.equal((await worker.fetch(request("/api/v2/match", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: " ".repeat(16 * 1024 + 1),
  }), credentials)).status, 413);
  assert.equal((await worker.fetch(request("/api/v2/search/episodes?anime=fixture"), {
    ...credentials, CLIENT_LIMIT: { limit: async () => ({ success: false }) },
  })).status, 429);
  assert.equal(calls, 0);
});

test("handles valid match requests and passes only the supported fields", async () => {
  const payload = { fileName: "示例", fileHash: "0".repeat(32), fileSize: 0, videoDuration: 10, matchMode: "fileNameOnly" };
  globalThis.fetch = async (url, options) => {
    assert.equal(url.href, "https://api.dandanplay.net/api/v2/match");
    assert.deepEqual(JSON.parse(options.body), payload);
    return Response.json({ success: true, matches: [] });
  };
  const result = await worker.fetch(request("/api/v2/match", { method: "POST",
    headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ...payload, extra: "ignored" }) }), credentials);
  assert.deepEqual(await result.json(), { success: true, matches: [] });
});

test("reports readiness with no credential fields", async () => {
  const query = request("/health");
  assert.equal((await worker.fetch(query, {})).status, 503);
  const response = await worker.fetch(query, credentials);
  assert.deepEqual(await response.json(), { success: true, errorMessage: "" });
});

test("does not follow a redirect from the upstream", async () => {
  let calls = 0;
  globalThis.fetch = async () => {
    calls++;
    return Response.redirect("https://example.invalid/", 302);
  };
  const response = await worker.fetch(request("/api/v2/search/episodes?anime=fixture"), credentials);
  assert.equal(response.status, 502);
  assert.equal(response.headers.get("Location"), null);
  assert.equal(calls, 1);
});

test("loads the official episode delivery URL without forwarding application headers", async () => {
  const delivery = "https://cas2.dandanplay.net/api/comment/1?fixture=delivery";
  const urls = [];
  globalThis.fetch = async (url, options) => {
    urls.push(url.href);
    assert.equal(options.redirect, "manual");
    if (urls.length === 1) return Response.redirect(delivery, 302);
    assert.deepEqual(options.headers, { Accept: "application/json" });
    return Response.json({ count: 1, comments: [{ m: "fixture" }] });
  };
  const response = await worker.fetch(request("/api/v2/comment/1?withRelated=true&chConvert=1"), credentials);
  assert.equal((await response.json()).count, 1);
  assert.equal(urls.length, 2);
  assert.equal(urls[1], delivery);
});
