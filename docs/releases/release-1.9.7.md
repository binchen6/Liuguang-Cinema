# 流光影院 1.9.7 发行说明

**发布日期**：2026-11-27

## 核心更新

### 1. 通用安卓 TVBox 站点自动适配与原生适配器公共抽取
- **抽取可复用原生适配工具集**：新建 `NativeSiteToolkit.cs`，统一沉淀多域名探测、浏览器安全验证（Cloudflare/WAF）识别、MacCMS `player_aaaa` 解码（支持 `encrypt=0/1/2` 与分号尾缀变体）、Stui 播放列表抽取、网盘分享线路挂载与 URL 规范化，重构 11 个原生站点适配器消除约 350+ 行重复逻辑。
- **动态加载外部安卓 DEX / JAR 爬虫**：Java 17 CatVod 引擎新增 `ExternalJarLoader`（集成 Apache-2.0 `dex2jar` 与 `BouncyCastle`），自动下载外部仓库提供的 `spider` / `jar`（支持 GitHub 镜像回退与本地持久化缓存）、将 Android DEX 实时转换为 JVM 字节码并在隔离 `ClassLoader` 中实例化，支持注册外部 `Proxy` 与持久化 `SharedPreferences`，大幅拓展对第三方安卓 TVBox 仓库站点的直接适配能力。
- **补全常见 TVBox 脚本与协议适配器**：
  - 新增 `JsSpiderClient`（基于 BSD-2-Clause `Jint` + `AngleSharp` 沙盒），支持常见 `drpy2` 声明式规则（`pdfh`/`pdfa`/`pd`、`:eq(n)`、`--` 排除、`||` 回退与 MacCMS `player_aaaa` 直链解析）以及 `CatVod-JS / T3` 模块脚本。
  - 新增 `UniversalProtocolVodProvider` 原生支持 `AppYsV2` / `AppV2` / `AppGet` / `AppQi` / `AppRJ` 与 `AList v3`（`csp_AList`）。
  - 新增 `PySpiderClient` 自动检测本机 Python 3 以标准 JSON stdio IPC 运行常见 `py_*` 爬虫脚本。
- **全面兼容 TVBox 加密与相对路径配置**：`SourceConfigService` 新增对含注释/尾逗号 JSON、`8位前缀**` / 尾部 `**` 图片隐写 Base64、以及 AES-128-CBC 十六进制（`$#key#$` / `2423...2324`）配置的自动解码，并自动规范化 `spider`、`jar`、`api`、`ext`、`lives` 与 `parses` 中的相对路径。

### 2. 多仓库切换与按仓库隔离展示
- **点播页顶部快捷入口**：点播标题旁的磨砂胶囊升级为交互式「切换仓库」下拉按钮，一键在内置仓库与全部已导入单仓/多仓子仓库之间切换，底部附「管理与导入仓库…」直达设置页。
- **设置页仓库管理**：在「设置 → 片源」新增「当前仓库」选择器、「切回内置」快捷按钮及已导入仓库逐项「使用」按钮；多仓导入保留每个子仓独立的 `spider` JAR 与站点列表。
- **严格隔离且不断链**：切换仓库后，点播站点列表、全站搜索范围与直播源列表即时切换并仅展示当前仓库内容，每个仓库独立记住默认首页站点；收藏与播放历史仍通过跨仓索引 `FindSite` 正常回放。

### 3. 拖动进度条流畅性与拖动后加载深度优化
- **消除暂停/播放反复与滑块回弹**：新增确定性状态机 `PlaybackSeekCoordinator`，彻底移除旧版在缓冲期反复调用 `SetPause(true/false)` 的侵入式循环；引入带代际校验的 2600ms 目标位置锁定、220ms 缓冲指示防抖与后退跳转进度基线重置，根治拖动回弹、误判卡顿与暂停/播放状态抖动。
- **极速响应拖拽与网盘跳转**：
  - `CloudBlockCache` 与 `CloudMediaProxy` 新增跳转纪元感知：远距离拖拽（超过 3 块）立即取消过期的顺序预读并保留至少 1 个高优通道专供实时按需块；HLS 分片代理同步取消过期预读并提前检测客户端断连。
  - 回环代理地址（`127.0.0.1`）启用轻量化 `:network-caching=650` + `:clock-jitter=0`，普通点播默认缓存由 3200ms 降至 2000ms，拖拽后起播显著提速。
  - 进度条细化 `StepFrequency="100"` 并新增 GPU 合成悬浮时间预览胶囊（`SeekPreviewPill`），支持悬停与拖拽实时预览目标时间点。

### 4. 国内直连友好的弹弹play 默认 AppSecret 安全存储与中继架构
- **零密钥入仓、零明文编入客户端**：默认 AppId / AppSecret 继续严格隔离在服务端加密环境变量中，桌面程序集保持零 `DanDanPlayApp*` 元数据。
- **多平台免费国内直连中继适配器**：将 `server/dandanplay-relay/` 抽离为跨平台核心 `relay-core.mjs`，新增 **阿里云 ESA Pages 边缘函数**（`esa/standalone.mjs` / `esa/index.mjs`，官方默认中继部署于独立子域名 CNAME 接入，兼顾国内直连可用性与零费用加密保管）、**腾讯云 EdgeOne Pages Functions**（`edgeone/`）、**Laf**（`laf/`）与通用 **Node.js 服务**（`node-server.mjs`），保留 Cloudflare Workers（`worker.mjs`）作海外兜底。
- **多中继故障转移 + 国内 CDN 直连弹幕省流模式**：`DANDANPLAY_RELAY_URL` 与「设置 → 弹幕与 AI → 签名中继地址」均支持逗号分隔多个 HTTPS 地址自动故障转移并记忆健康节点；拉取弹幕时通过 `X-Relay-Delivery: direct` 让中继仅返回校验后的官方国内 CDN 地址（`https://cas2.dandanplay.net/api/comment/...`，约 120 字节），客户端直连国内 CDN 下载弹幕主体，节省 95% 以上中继出站流量。

### 5. 代码结构清理与质量验证
- 移除已废弃的 `PillBrushConverter`、`PlayerViewModel.StopAsync`、`MainWindow._narrowPane` / `OnTitleControlsLayoutUpdated`、`SettingsPage.OnCloseCloudQrClick` 及冗余 `[RelayCommand]` 标记，修复多项静态分析警告。
- 自动化回归扩展至 **59 项 C# 协议与状态机回归** + **10 项 Node.js 多平台弹幕中继单测** + **Java CatVod 引擎 IPC 冒烟测试**，全部通过。
