import http from "node:http";
import { handleRelayRequest } from "./relay-core.mjs";

const port = Number(process.env.PORT || 8787);
const host = process.env.HOST || "0.0.0.0";

/**
 * Zero-dependency Node.js 20+ HTTP server adapter for mainland China free/low-cost platforms:
 * Sealos App Launchpad, Alibaba Cloud FC 3.0 Custom Runtime, Tencent Cloud SCF Web Function, or Docker.
 * Secrets are read exclusively from process.env (DANDANPLAY_APP_ID, DANDANPLAY_APP_SECRET, DANDANPLAY_BACKUP_SECRET).
 */
export async function handleNodeRequest(req, res, env = process.env) {
  try {
    const origin = `http://${req.headers.host || "127.0.0.1"}`;
    const url = new URL(req.url || "/", origin);
    const headers = new Headers();
    for (const [k, v] of Object.entries(req.headers)) {
      if (Array.isArray(v)) headers.set(k, v.join(", "));
      else if (v !== undefined) headers.set(k, v);
    }
    const hasBody = req.method !== "GET" && req.method !== "HEAD";
    const chunks = [];
    if (hasBody) {
      let total = 0;
      for await (const chunk of req) {
        total += chunk.length;
        if (total > 32 * 1024) {
          res.writeHead(413, { "Content-Type": "application/json", "Cache-Control": "no-store" });
          res.end(JSON.stringify({ success: false, errorMessage: "请求体过大。" }));
          return;
        }
        chunks.push(chunk);
      }
    }
    const webReq = new Request(url, {
      method: req.method,
      headers,
      body: hasBody ? Buffer.concat(chunks) : undefined,
    });
    const webRes = await handleRelayRequest(webReq, env, { clientIp: req.socket?.remoteAddress || "127.0.0.1" });
    const outHeaders = {};
    webRes.headers.forEach((v, k) => { outHeaders[k] = v; });
    res.writeHead(webRes.status, outHeaders);
    if (webRes.body) {
      const reader = webRes.body.getReader();
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        res.write(value);
      }
    }
    res.end();
  } catch {
    if (!res.headersSent) {
      res.writeHead(502, { "Content-Type": "application/json", "Cache-Control": "no-store" });
    }
    res.end(JSON.stringify({ success: false, errorMessage: "弹弹play 服务暂不可用。" }));
  }
}

export function createRelayServer(env = process.env) {
  return http.createServer((req, res) => { void handleNodeRequest(req, res, env); });
}

if (import.meta.url === `file://${process.argv[1]?.replace(/\\/g, "/")}`) {
  const server = createRelayServer(process.env);
  server.listen(port, host, () => {
    console.log(`Liuguang DanDanPlay relay listening on http://${host}:${port}`);
  });
}
