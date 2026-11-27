import { afterEach, test } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import worker from "./worker.mjs";
import { resetMemoryRateLimits } from "./relay-core.mjs";
import { onRequest as edgeOneRequest } from "./edgeone/functions/[[default]].js";
import esaWorker from "./esa/index.mjs";
import esaStandalone from "./esa/standalone.mjs";
import { createRelayServer, handleNodeRequest } from "./node-server.mjs";

const realFetch = globalThis.fetch;
afterEach(() => {
  globalThis.fetch = realFetch;
  resetMemoryRateLimits();
});
const allow = { limit: async () => ({ success: true }) };
const credentials = {
  DANDANPLAY_APP_ID: "fixture-app",
  DANDANPLAY_APP_SECRET: "fixture-primary",
  DANDANPLAY_BACKUP_SECRET: "fixture-backup",
  CLIENT_LIMIT: allow,
  SERVICE_LIMIT: allow,
};
const bareCredentials = {
  DANDANPLAY_APP_ID: "fixture-app",
  DANDANPLAY_APP_SECRET: "fixture-primary",
  DANDANPLAY_BACKUP_SECRET: "fixture-backup",
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

test("returns validated China CDN deliveryUrl directly when X-Relay-Delivery: direct is requested", async () => {
  const delivery = "https://cas2.dandanplay.net/api/comment/42?sig=ok";
  let calls = 0;
  globalThis.fetch = async () => {
    calls++;
    return Response.redirect(delivery, 302);
  };
  const response = await worker.fetch(
    request("/api/v2/comment/42?withRelated=true&chConvert=1", { headers: { "X-Relay-Delivery": "direct" } }),
    credentials,
  );
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { success: true, deliveryUrl: delivery });
  assert.equal(calls, 1);
});

test("EdgeOne Pages adapter works without Cloudflare bindings and enforces in-memory rate limits", async () => {
  globalThis.fetch = async () => Response.json({ success: true, animes: [] });
  const env = { ...bareCredentials, CLIENT_RATE_LIMIT: "2" };
  const makeReq = () => new Request("https://edgeone.example/api/v2/search/episodes?anime=test", {
    headers: { "EO-Connecting-IP": "198.51.100.7" },
  });
  assert.equal((await edgeOneRequest({ request: makeReq(), env })).status, 200);
  assert.equal((await edgeOneRequest({ request: makeReq(), env })).status, 200);
  assert.equal((await edgeOneRequest({ request: makeReq(), env })).status, 429);
});

test("Alibaba Cloud ESA Pages adapter and standalone Edge Routine work cleanly", async () => {
  globalThis.fetch = async (url) => {
    const href = typeof url === "string" ? url : url.href;
    if (href.includes("/api/v2/comment/77")) {
      return Response.redirect("https://cas2.dandanplay.net/api/comment/77?ok=1", 302);
    }
    return Response.json({ success: true, animes: [] });
  };
  const health = await esaWorker.fetch(new Request("https://esa.example/health"), bareCredentials);
  assert.equal(health.status, 200);
  const res = await esaWorker.fetch(
    new Request("https://esa.example/api/v2/search/episodes?anime=esa", { headers: { "Ali-CDN-Real-IP": "203.0.113.9" } }),
    bareCredentials,
  );
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), { success: true, animes: [] });

  const standaloneHealth = await esaStandalone.fetch(new Request("https://esa.example/health"), bareCredentials);
  assert.equal(standaloneHealth.status, 200);
  const nonEnumEnv = {};
  Object.defineProperties(nonEnumEnv, {
    DANDANPLAY_APP_ID: { value: "fixture-app", enumerable: false },
    DANDANPLAY_APP_SECRET: { value: "fixture-primary", enumerable: false },
    DANDANPLAY_APP_SECRET2: { value: "fixture-backup", enumerable: false },
  });
  const nonEnumHealth = await esaStandalone.fetch(new Request("https://esa.example/health"), {}, nonEnumEnv);
  assert.equal(nonEnumHealth.status, 200);
  const esa3ArgHealth = await esaWorker.fetch(new Request("https://esa.example/health"), {}, nonEnumEnv);
  assert.equal(esa3ArgHealth.status, 200);
  const standaloneDirect = await esaStandalone.fetch(
    new Request("https://esa.example/api/v2/comment/77?withRelated=true&chConvert=1", { headers: { "X-Relay-Delivery": "direct" } }),
    {},
    nonEnumEnv,
  );
  assert.equal(standaloneDirect.status, 200);
  assert.deepEqual(await standaloneDirect.json(), { success: true, deliveryUrl: "https://cas2.dandanplay.net/api/comment/77?ok=1" });
});

test("Node.js standalone server adapter serves /health and signed API routes cleanly", async () => {
  assert.equal(typeof createRelayServer(bareCredentials).listen, "function");
  globalThis.fetch = async () => Response.json({ success: true, animes: [{ animeId: 9, animeTitle: "NodeTest" }] });

  async function invoke(method, url) {
    let status = 0;
    let headers = {};
    const chunks = [];
    const req = { method, url, headers: { host: "relay.local" }, socket: { remoteAddress: "198.51.100.22" } };
    const res = {
      headersSent: false,
      writeHead(code, hdrs) { status = code; headers = hdrs; this.headersSent = true; },
      write(chunk) { chunks.push(Buffer.from(chunk)); },
      end(chunk) { if (chunk) chunks.push(Buffer.from(chunk)); },
    };
    await handleNodeRequest(req, res, bareCredentials);
    return { status, headers, body: JSON.parse(Buffer.concat(chunks).toString("utf8")) };
  }

  const health = await invoke("GET", "/health");
  assert.equal(health.status, 200);
  assert.equal(health.body.success, true);

  const search = await invoke("GET", "/api/v2/search/episodes?anime=NodeTest");
  assert.equal(search.status, 200);
  assert.equal(search.body.animes[0].animeId, 9);
  assert.equal(search.headers["cache-control"], "no-store");
});
