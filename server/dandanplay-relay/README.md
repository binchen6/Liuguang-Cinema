# 弹弹play 默认 AppSecret 安全签名中继（国内直连免费方案）

为确保开源仓库与安装包内**不暴露任何明文或可逆提取的 `AppSecret`**，同时解决 `*.workers.dev` 在中国大陆直连不稳定的问题，本目录提供**零依赖、跨平台**的服务端签名中继，支持多个免费且国内访问友好的 Serverless/边缘平台，客户端还支持配置**多个中继地址自动故障转移**与**弹幕直连国内 CDN (`cas2.dandanplay.net`) 省流模式**（节省 >95% 中继出站流量）。

## 推荐免费国内直连部署方案

| 平台 | 国内直连 | 是否需要自定义域名/备案 | 免费额度 | 对应目录 |
| --- | --- | --- | --- | --- |
| **阿里云 ESA Pages** | **极快（3200+ 边缘节点，亚太/国内直连友好）** | 绑定自有子域名（支持 **CNAME 模式** 独立接入子域名，不改主域 NS、不影响主站/博客） | 永久免费版套餐 + 边缘函数免费额度 | `esa/standalone.mjs` / `esa/index.mjs` |
| **腾讯云 EdgeOne Pages** | 极快（亚洲/国内优化节点） | 默认免费域名可用；绑自有已备案域名可走国内 BGP | 长期免费版 + 边缘函数额度 | `edgeone/` |
| **Laf (`laf.run`) / 自建 Node.js** | 极快（国内直连） | 自带免费公网 HTTPS 域名或自建反向代理 | 免费开发版 / 自有主机 | `laf/dandanplay-relay.ts` / `node-server.mjs` |
| **Cloudflare Workers** | 需自有域名或作海外兜底 | 建议绑定自有域名 | 10 万次/天 | `worker.mjs` |

## 环境变量（服务端加密保管，绝不提交到 Git）

在所选平台控制台的**加密环境变量 / Secret** 中配置：

- `DANDANPLAY_APP_ID`：弹弹play 开放平台 AppId
- `DANDANPLAY_APP_SECRET`：弹弹play 主 AppSecret
- `DANDANPLAY_BACKUP_SECRET` 或 `DANDANPLAY_APP_SECRET2`（可选）：备用 AppSecret（主密钥 401/403 时自动无缝回退）

## 快速接入客户端

1. **发版构建**：在 GitHub Actions 仓库变量 `DANDANPLAY_RELAY_URL` 中填入国内可直连的 HTTPS 中继地址；支持英文逗号分隔多个地址实现主备自动故障转移（例如 `https://primary-cn.example.com/,https://backup.example.com/`）。
2. **终端用户自建**：也可以直接在流光影院「设置 → 弹幕与 AI → 弹幕签名中继地址」中填入自建中继 HTTPS 地址，无需重新编译。

## 本地自动化测试

```bash
node --test server/dandanplay-relay/worker.test.mjs
```
