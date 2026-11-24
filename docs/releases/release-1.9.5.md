# 流光影院 1.9.5 发行说明

发布日期：2026-11-24

## 主要更新

### 1. 阿里云盘分享目录获取与分享原画播放修复
- **根治阿里云盘分享目录获取失败**：
  - 修正 `AliCloudProvider.GetShareTokenAsync` 将已废弃且返回 HTTP 404 的 `/adrive/v3/share_link/get_share_token` 切换为当前官方有效接口 `POST /v2/share_link/get_share_token`（主备域名 `api.alipan.com` / `api.aliyundrive.com` 自动回退并补齐 `Referer`/`Origin` 请求头）。
  - 修正 `AliCloudProvider.ListFilesAsync` 切换至官方分享目录接口 `POST /adrive/v2/file/list_by_share`，并修复此前仅读取 `file_type` 而漏读阿里云盘实际返回的 `type`（`"folder"` / `"file"`）字段导致目录和文件全部被跳过的问题；根目录列表改为从 `"root"` 遍历全部顶层目录并在异常空列表时按 `file_infos` 回退，补全常见视频/音频扩展名与 `mime_type` 识别。
  - 将 `ShareLink.Cancelled` / `ShareLink.Forbidden` / `ShareLink.NotFound` / `InvalidParameter.SharePwd` 等业务错误映射为明确的「分享已失效」与「需要提取码」状态，并在 `WoggNativeProvider` 中记录详细失败原因，避免统一笼统报「目录读取失败」。
- **修复阿里云盘分享资源原画播放（应对官方 HTTP 410 下线匿名直链接口）**：
  - 阿里云盘已下线匿名 `/v2/file/get_share_link_download_url`（返回 HTTP 410 Gone）；登录阿里云盘账号后，播放分享文件会自动转存至个人盘「流光影院」目录（`POST /adrive/v4/batch` `/file/copy`，失败时兼容 `/v3/batch` 与 `/adrive/v2/file/copy`，并支持缓存失效后自动重转存），再通过个人盘 `get_download_url` 获取原画直链且不向 OSS CDN 透传 `Authorization`；若账号转存失败（如空间不足），自动尝试 `POST /v2/file/get_share_link_video_preview_play_info` 回退最高可用转码预览流；未登录时按现有流程弹出扫码登录并自动续播。

### 2. UC 网盘原画播放深度优化（解决“需开通会员或使用 UC 浏览器播放”提示视频）
- **新增持久化每账号 UC 设备令牌（`UcDeviceToken`）**：
  - 为每个 UC 账号生成并持久化独立的 24 字符 `UTDID`（18 字节随机值 Base64），按 UC 桌面客户端协议派生长效 `ut` 参数（`Base64(UInt16BE(13801) || AES-128-CBC-PKCS7(UTDID))`），在 `POST pc-api.uc.cn/1/clouddrive/file/download` 请求中携带 `ut`（严格隔离，不向夸克泄漏），并为 UC/夸克补齐 `Channel/pckk_other_ch` 官方桌面客户端 `User-Agent`。
- **消除 Cookie 轮换竞态与账号切换残留**：
  - 调整 `QuarkCloudProvider.PlayOriginalAsync` 先完成 `file/download` 并保留请求期 Cookie 快照给媒体代理，避免与可选转码查询并发争夺 `__puus` 轮换；在公开分享浏览时不混入登录态 Cookie；在 `CloudDriveService.SaveCredentialAsync` 重登夸克/UC 后清空旧会话与目录缓存；若历史转存文件被手动删除导致 404/不存在，自动清除过期缓存并重新转存一次。
- **提示视频主动识别、设备轮换重试与自动转码兜底**：
  - 对 35 MB 以上的 UC 原画视频执行轻量 `Range: bytes=0-0` 探测：一旦发现服务端返回约 14 MB 的会员/客户端引导占位视频，先轮换新 `UTDID` 并附加桌面端版本参数重试一次；若该账号仍受服务端风控替换，播放场景自动切换至最高可用 UC 转码流（新增 `file/v2/play/project` 优先请求并保留 `file/v2/play` 回退）并在播放器内给出明确提示，原画下载场景则拒绝将提示视频误存为正片。
  - `PlaybackResolver` 补齐 UC 在自动画质下原画失败回退转码与 `UseAvformatDemuxer` 适配。

### 3. 弹幕匹配 “The operation was canceled.” 根治与稳定性优化
- **修复自定义 DoH DNS 超时穿透与故障缓存污染（`DnsResolverService`）**：
  - 修复自定义 DoH 5 秒超时抛出的 `TaskCanceledException` 绕过 `ex is not OperationCanceledException` 守卫而未回退系统 DNS 的缺陷；修复失败 DNS 任务被反向缓存 30 秒导致后续请求持续立刻取消的问题。
- **强化弹弹play 与弹幕加载韧性（`DanDanPlayService` / `DanmakuService` / `BilibiliVodProvider.Online` / `PlayerPage`）**：
  - 精简 `DanDanPlayService.MatchAsync` 顺序请求轮次（首轮不带严苛 `episode=` 过滤即可一次取回全季剧集），显著降低高延迟与晚高峰链路下的匹配耗时；为 `SourceHttpClient.GetExchangeAsync` / `PostJsonAsync` 增加可配置请求超时。
  - 为弹弹play 请求加入瞬态超时/5xx 自动重试一次、`/api/v2/comment` 在 `withRelated=true` 超时后自动回退直连弹幕（`withRelated=false`），并将内部超时统一转换为中文 `TimeoutException`，不再向界面裸露 `The operation was canceled.`。
  - 扩展 `PlayerPage.ReloadDanmakuAsync` 在弹弹play 网络失败/超时时自动回退原站弹幕或 B 站在线弹幕库；修复 `CancelDanmakuLoad` 过早释放 `CancellationTokenSource` 的潜在 `ObjectDisposedException` 风险；完善手动搜索弹幕弹窗的并发取消、关闭取消与超时中文提示。

### 4. 全局 Debug 排查与细节修复
- 修复 `ImageCacheService` 未捕获 8 秒请求超时 `OperationCanceledException` 导致 `PosterImage` 在 `diagnostic.log` 中大量刷屏 `封面绑定失败: TaskCanceledException` 的问题。
- 修复 `PanSearchVodProvider` 在 `pansearch.me` 超时时未触发 `panso.vip` 回退的问题，收紧主站超时至 10 秒并记录冷却，恢复 `盘她`/`抠搜`/`优汐` 等网盘搜索站点在主站波动时的可用性。
- 统一排查并修复 `AliOAuth`、`CloudDriveService.ExpandAsync`、`MyDriveVodProvider`、`CloudDriveViewModel` 与 `PlayerViewModel` 源刷新路径中误用 `ex is not OperationCanceledException` 吞掉回退逻辑的问题，并补齐个人云盘视频/音频扩展名识别。

## 验证记录

- **编译**：`dotnet build src/TV/TV.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 --no-restore` 0 错误（231 条既有分析器/AOT 警告，较上版减少 6 条）。
- **协议与核心回归**：
  - `CloudRegression`：**34 / 34 通过**（覆盖 `UcDeviceToken` OpenSSL AES-128-CBC/PKCS7 标准向量校验、UC `ut` 参数隔离与 `__puus` 请求期快照、UC 14 MB 提示视频检测/设备轮换恢复/自动转码兜底/下载拦截、阿里云盘 `/v2/share_link/get_share_token` + `/adrive/v2/file/list_by_share` + `type` 字段解析、`/adrive/v4/batch` 分享转存原画及失效错误分类）；
  - `LocalMediaRegression`：**24 / 24 通过**；
  - `UpdateRegression`：**13 / 13 通过**；
  - `LiveAndResilience`：**10 / 10 通过**（实网阿里云盘公开分享 `zPCXSoJb9rD` 目录读取成功、失效分享 `H7Y4J8EkrTo` 准确识别为已失效、弹弹play 瞬态超时自动重试与 `withRelated=false` 回退验证通过、`DnsResolverService` DoH 超时不污染缓存验证通过、实网弹弹play 匹配《葬送的芙莉莲》E05 用时 2.27s 加载 6,631 条弹幕 /《2.5次元的诱惑》E01 用时 0.30s /《庆余年》准确拒绝误匹配）。
- **内置站点实网探测**：`YpanSo` 5 组搜索关键词全中（山海 10 / 庆余年 15 / 三体 4 / 繁花 10 / 流浪地球 10，详情 50 集），`AiDj` 首页/分类/搜索/详情/直链全通。
- **发行产物与增量升级核验**：
  - 完整安装包 `TV-Setup-1.9.5-x64.exe`（138.24 MB）：首次冷扫后三次启动实测 **461 / 205 / 213 ms**（通过 `.iss` 配置守卫与冷启 ≤ 1500 ms / 热启 ≤ 600 ms 门限）；
  - 增量更新包 `TV-Update-1.9.5-from-1.9.4-x64.exe`（9.23 MB，4 个变更文件，6.55 MB 净载荷）：两次启动实测 **1153 / 333 ms**，实机静默升级 `1.9.4 → 1.9.5` 后全部 **962 / 962 文件**与 1.9.5 便携包逐文件 SHA-256 100% 一致；
  - 便携包 `TV-1.9.5-win-x64-portable.zip`（188.51 MB）及全部 3 个 `.sha256` 文件校验通过。
