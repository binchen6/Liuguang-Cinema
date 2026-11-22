# 流光影院 1.7.0 发布说明

发布日期：2026-11-22

## 本次更新

### 1. 在线弹幕接入弹弹play 开放弹幕网络
- **全新 `DanDanPlayService` 接入**：完整实现弹弹play 开放平台 v2 签名鉴权（`X-AppId` / `X-Timestamp` / `X-Signature = Base64(SHA256(AppId + Timestamp + Path + AppSecret))`），支持主备密钥自动故障转移、结果 LRU 缓存以及用户自定义 DPAPI 加密凭证覆盖。
- **多策略智能匹配**：
  - 本地视频优先计算前 16 MB MD5 哈希 + 文件名调用 `/api/v2/match` 精准匹配；
  - 在线点播自动清洗片名/集数，调用 `/api/v2/search/episodes` 结合标题归一化打分匹配分集，并提供 `/api/v2/match` 兜底；
  - 调用 `/api/v2/comment/{episodeId}?withRelated=true&chConvert=1` 拉取整合弹幕并自动转简体。
- **播放器弹幕体验升级**：新增「手动搜索弹弹play 剧集」弹窗，支持手动切换任意匹配番剧与分集；修复 DanDanPlay 4 字段 JSON 弹幕色彩解析错位 Bug（`p` 字段 `time,mode,color,userId` 索引对齐）及切集时重复触发在线弹幕匹配的问题。

### 2. 阿里云百炼 AI 自动生成字幕、字幕翻译与实时字幕
- **全新 `Services/AI` 模块**：
  - **AI 自动生成字幕**：基于内置 LibVLC 无头高速提取 16 kHz 单声道 Opus/WAV 音轨 → 阿里云百炼临时 OSS 凭证上传（`getPolicy` + `X-DashScope-OssResourceResolve: enable`）→ 提交异步语音转写任务，支持在「设置 → 弹幕与 AI」选择：
    - `paraformer-v2`（便宜·高性价比）
    - `qwen3-asr-flash-filetrans`（准确·支持字级时间戳与实时字幕）
  - **原视频时间轴自动对齐**：开启模型句级/字级时间戳输出（`enable_words` / `timestamp_alignment_enabled`），按词边界与停顿自动断句并对齐原视频时长，消除重叠、闪烁微隙与静音漂移，自动挂载并支持导出 `.srt`。
  - **AI 字幕翻译**：接入百炼 OpenAI 兼容模式专用翻译模型，支持选择：
    - `qwen-mt-flash`（便宜·极速）
    - `qwen-mt-plus`（高质量·旗舰）
    支持多语种目标语言、双语对照显示、批处理并发翻译与错行逐条自动回退；支持一键「AI 生成并翻译」或直接翻译当前已加载/本地 `.srt` / `.vtt` / `.ass` 字幕文件。
  - **实时字幕（`qwen3-asr-flash-realtime`）**：基于 Windows WASAPI Process Loopback 仅捕获本应用播放音频，通过 WebSocket 流式推送 16 kHz PCM 实时渲染双语字幕浮层，并支持导出实录 `.srt`。
  - **一站式密钥引导**：在「设置 → 弹幕与 AI」提供一键跳转阿里云百炼 API Key 生成控制台入口、零成本连通性验证及 Windows DPAPI 本机加密保存。

### 3. 播放器字幕设置全面完善
- 底部控制栏新增独立「字幕设置 / AI 字幕」入口（同时同步至右上角「更多播放选项」），打开即可统一管理：
  - **字幕前移 / 后移**：支持 ±60 秒精确微调（`-2s` / `-0.5s` / `-0.1s` / `+0.1s` / `+0.5s` / `+2s` 快捷按钮、数值框与快捷键 `Z` / `X`）；
  - **字幕速度调节**：支持 `0.80x` – `1.25x` 精细调速与常见帧率换算预设（`0.95x` / `0.96x` / `1.00x` / `1.04x` / `1.05x`），外挂/AI 字幕自动重算时间轴热重载，内嵌字幕通过动态延迟补偿无缝同步；
  - **播放器内 AI 字幕直达**：支持在播放页直接触发 AI 生成、AI 生成并翻译、AI 翻译字幕、开启/关闭实时字幕、实时进度取消条与导出 `.srt`。

### 4. 深度性能优化与全项目 Debug 修复
- **图片加载零 UI 线程阻塞**：重构 `ImageCacheService`，将磁盘读取、HTTP 下载、SkiaSharp 缩放与唯一临时文件原子写入全部移至后台线程（`ConfigureAwait(false)`），仅在最终解码 `BitmapImage` 时切回 UI 线程；内存 LRU 上限提升至 140 张 / 36M 像素。
- **修复长 HLS 代理分片 404 Bug**：修复 `CloudMediaProxy` 在注册超过 96 个会话时提前驱逐长 HLS 播放列表前序分片的严重缺陷（容量调整至 4096 并优化过期清理），并用编译期正则替换运行时正则。
- **SQLite 性能优化与句柄修复**：`DatabaseService` 全量补齐 `SqliteCommand` 释放、启用 8 MB 页缓存与内存临时表、新增主索引单行历史查询 `GetHistoryEntryAsync`（详情页不再全量扫描 200 条记录），批量本地索引改为事务内复用预编译语句。
- **页面交互与内存治理**：
  - `VodViewModel` 分页「加载更多」改为增量追加，消除列表重载与滚动回跳；修复单集/批量下载云盘代理会话未释放泄漏；
  - `DanmakuOverlay` 新增滚动轨道末节点缓存（O(lanes) 分配）与复用裁剪几何；
  - `LiveViewModel` 预聚合频道缓存，消除搜索按键逐次重算嵌套分组；
  - `MainWindow` 缓存侧栏测量宽度与透明画刷，消除缩放窗口时的重复 Measure 分配；
  - `LibraryViewModel` 并行加载数据库表并修复取消文件选择器时的误提示；
  - `SearchViewModel` 修复取消并发搜索时提前释放 `CancellationTokenSource` 竞态；
  - `MotionService` 补全 `CompositionScopedBatch` 释放；`DnsResolverService` 修复并发查询竞态并增加缓存上限；
  - `Diagnostics` 增加敏感密钥/令牌/签名自动脱敏过滤，防止凭据写入日志。

### 5. 项目结构整理与开源化去敏
- 清理已废弃的旧版安装器封装代码（`installer/SetupLauncher.cs`、`scripts/build-setup-launcher.ps1`）。
- 清除测试夹具中的本机绝对路径，改为动态发现 `ffmpeg`。
- 弹弹play 凭证全面改为构建期本地忽略配置（`Secrets.Local.props`，附带 `Secrets.Local.props.example`）+ 运行时设置页 DPAPI 加密输入，确保开源仓库零明文密钥。
