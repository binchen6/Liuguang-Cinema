# 流光影院 1.9.1 发行说明

发布日期：2026-11-24

## 核心更新

### 1. 全站导航栏选中状态复位修复
- 根治 WinUI 3 `ListViewItem` 与 `ToggleButton` 视觉状态分组命名不匹配导致的历史点击项高亮/朱砂指示条残留问题（修复搜索页左侧站点列表、我的云盘账号卡、直播分组与频道、设置页左侧分区及主侧栏连续切换后多项同时高亮的问题）。
- 为主侧栏拆分独立无静态指示条模板 `MainNavRailItemStyle`，杜绝与全局弹簧指示条 `FloatingNavIndicator` 产生悬停/按压重影。

### 2. 阿里云盘目录读取修复（解决“未返回默认网盘 ID”）
- 将阿里云盘用户信息与网盘信息接口从错误返回 HTTP 400/405 的 `GET` 修正为 `POST {}`，并优先对接官方可用端点 `https://api.alipan.com/v2/user/get` 与 `POST /v2/account/token` 返回的 `default_drive_id` / `resource_drive_id` / `backup_drive_id`。
- 非 OpenAPI 网页版登录凭据改走真实的 `POST /adrive/v3/file/list`、`POST /adrive/v3/file/search` 与 `POST /v2/file/get_download_url`，并内置零依赖纯 C# secp256k1 ECDSA 设备会话签名（`POST /users/v1/users/device/create_session` + `x-device-id` / `x-signature`）。
- 当账号同时拥有独立的「资源库」与「备份盘」时，云盘根目录自动呈现双虚拟目录；播放直链不再向 OSS CDN 透传 `Authorization: Bearer`，并支持 401 失效自动刷新重试与重登后立即清缓存。

### 3. 增量更新包全量覆盖与软件图标实时刷新
- 修复上个版本增量包未更新桌面/开始菜单/任务栏图标及注册表关联图标的问题：
  - 将 `Assets\App.ico` 随程序目录分发，并在主窗口启动时显式绑定 `AppWindow.SetIcon`；
  - `TV-Update.iss` 全面对齐完整安装器能力：同步刷新 `[Registry]` 文件关联与 `DefaultIcon`、按需覆盖已存在的开始菜单/桌面快捷方式（不强行新建用户未勾选的快捷方式）、更新已安装 HKCU 卸载项中的 `DisplayVersion` / `DisplayName` / `DisplayIcon` 元数据、支持安装前等待 `TV.exe` 文件句柄释放，以及安装完成后自动触发 `SHChangeNotify`（`SHCNE_UPDATEITEM` + `SHCNE_ASSOCCHANGED`）即时刷新 Windows 外壳图标缓存；
  - `build-installer.ps1` 升级为双向差异计算，自动为旧版本中被移除的废弃文件与空目录生成 `[InstallDelete]` 清理段，确保增量升级后的目录与完整安装 100% 逐字节一致。

### 4. 国内 GitHub 更新下载大幅提速与限流兜底
- 内置 5 个实测可用的国内 HTTPS GitHub Release 加速节点，下载前自动并行发起 64 KB Range 测速，择优排序节点与 GitHub 官方直连。
- 大于 2 MB 的更新包启用 **4–6 路并发 2 MB Range 分片下载**（`RandomAccess.WriteAsync` + `.partial.plan` 断点续传位图 + 15 秒读取卡顿超时自动切线路），弹窗实时展示下载速率（MB/s）与当前加速线路。
- 严格坚守安全边界：所有下载字节必须通过 GitHub 官方 Release 的 SHA-256 摘要校验，若任一镜像校验不一致立即删除并自动切回 GitHub 官方直连重试。
- 新增 `api.github.com` 匿名 403 限流/超时自动兜底：无缝切换至官方 `github.com/.../releases/latest` + `expanded_assets` 解析版本与官方 SHA-256 摘要，解决国内共享公网 IP 无法检查更新的问题。

### 5. 弹弹play “一个或多个参数不符合规则” 修复与跨源弹幕匹配深度重构
- 根治因 `Path.GetFileNameWithoutExtension` 误把《2.5次元的诱惑》等含小数点片名截断为单字符 `"2"` 或把综艺日期（如 `20251123期`）当成集数导致的弹弹play `errorCode=2`（“一个或多个参数不符合规则”）问题；严格校验请求参数边界并保护手动搜索输入。
- 全新 `MediaMatching.Parse` 规范化媒体标识引擎：仅剥离真实视频扩展名，智能清除字幕组标签、4K/HDR/编码/语言/总集数等噪音，支持中/英/日双语别名（优先归一至中文规范片名）、阿拉伯/中文/罗马数字/SxxExx 季数识别、SP/OVA/番外识别与精准集号提取，使不同片源/网盘/字幕组命名下的同一集稳定生成一致的规范键并命中同一条弹幕流。
- 升级 `DanDanPlayService` 与 `BilibiliVodProvider.Online`：启用 `v2=true` 搜索并增加严格片名相关度门限（拒绝将《庆余年》《绝命毒师》等真人剧错误模糊匹配到无关动画）、季数强对齐、正片/特典区分与 `/api/v2/match` 候选二次校验；无弹弹play 索引的真人剧集可平滑回退原站或 B 站弹幕。

### 6. 点播页下滑顶部卡片可打断过渡动画与 UI 细节打磨
- 点播页新增 120Hz+ GPU 线程驱动的连续可打断下滑收起动画（`MotionService.BindCollapsibleHeader`）：随下滑顶部画册报头与宽幅展陈卡片柔和视差上移、微缩并淡出（含 GPU `InsetClip` 防止越界进入标题栏），筛选栏 1:1 跟手丝滑上移并无缝化为齐平吸顶的磨砂玻璃坞，任意时刻上滑或点击回顶均可即时打断折返；完整支持关闭动效的静态回退。
- 深度打磨全站 UI 细节：
  - 我的云盘账号卡新增磨砂高光描边与绿/红/灰连接状态点，文件列表升级为 24×24 建筑线框矢量图标（文件夹 / 视频 / 通用文件）并支持窄窗自适应，统一全部网盘未登录提示的中文名称；
  - 搜索页加宽左侧站点遥测轨至 244px 并预留滚动条避让内边距（不再遮挡「验证/重试」按钮），空态图标升级为 64×64 磨砂徽章，结果卡片补齐悬停双层景深与磨砂圆镜播放浮层；
  - 直播页分组、频道与线路列表增加滚动条右侧避让留白。

## 验证范围

- `dotnet build src/TV/TV.csproj -c Release --no-restore`：0 错误，0 新增编译告警。
- 协议与功能回归全量通过：
  - `CloudRegression`（25 项，含阿里云盘 `POST /v2/user/get`、`/adrive/v3/file/list`、`/adrive/v3/file/search`、`/v2/file/get_download_url`、`create_session`、双网盘路由、不向 CDN 泄露 Bearer 及 `AliDeviceSession` secp256k1 ECDSA 自验 5/5 通过）；
  - `UpdateRegression`（13 项）+ 实网国内加速下载实测（GitHub 网页源限流兜底解析成功，9.51 MB 增量包经国内加速 4 路并发 8.2s 下载完成，峰值 3.2 MB/s，SHA-256 校验通过）；
  - `LocalMediaRegression`（24 项）+ 实网弹弹play 端到端测试（《葬送的芙莉莲》6 种不同命名全部精确命中同一 `EpisodeId=176170005` 并加载 6,631 条弹幕；《2.5次元的诱惑》精确命中 `EpisodeId=177600001`；单字符片名与综艺日期不再报错；《庆余年》正确拒绝无关动画误匹配）。
- 桌面实机 UIA 多页验收（`1587×1039`，深色 + 浅色双主题）：验证点播页展开/中段下滑/完全吸顶/可打断回滑/回顶、搜索页 4 站点连点复位、云盘页 4 网盘连点复位、直播页、媒体库 4 分段、投屏页与设置页 5 分区连点复位，`last-error.log` 为空，全站 XAML 字号审计全部 `>= 12px`。
- 发行产物与秒开防回退验速：
  - 完整安装包 `TV-Setup-1.9.1-x64.exe`（138.20 MB）：三次启动实测 **304 / 255 / 244 ms**（通过冷启 ≤ 1500 ms、热启 ≤ 600 ms 门限）；
  - 增量更新包 `TV-Update-1.9.1-from-1.9.0-x64.exe`（9.60 MB，14 个变更文件，含 `Assets\App.ico`）：两次启动实测 **1069 / 284 ms**，实机静默升级 `1.9.0 → 1.9.1` 逐文件 SHA-256 与 1.9.1 便携包 100% 逐字节一致；
  - 便携免安装包 `TV-1.9.1-win-x64-portable.zip`（188.45 MB）及全部 `.sha256` 校验文件均通过核验。
