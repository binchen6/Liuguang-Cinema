# 流光影院 1.9.4 发行说明

发布日期：2026-11-24

## 主要更新

### 1. 播放器指针流畅度与多环境兼容性优化
- **消除播放器内鼠标移动卡顿**：
  - 重构 `PlayerPage.OnPointerMoved`：缓存窗口句柄（`_windowHwnd`），控制层已显示时对物理指针位移采样做 `200ms` 节流并懒挂隐藏定时器，不再在每次鼠标移动事件上遍历可视化树、调用 5 次 `TransformToVisual` 或频繁启停 `DispatcherQueueTimer`；仅在空闲满 3 秒、尺寸变化或窗口激活时检查指针是否落在控件上。触控与触控笔输入直接唤醒控制层。
  - 修复 `InputCursorHelper.RestoreCursor()` 在鼠标移动唤醒时调用 `SetCursorPos(pt.X, pt.Y)` 造成的指针回拉顿挫，新增 `fromPointerMove` 分支跳过静止光标位置重设，并缓存默认系统箭头光标句柄；对异常 DPI / 远程桌面环境下的光标尺寸做 `[16, 128]` 钳位与 `32×32` 兜底。
  - 移除播放器叠加在高频 `SwapChainPanel` 上的 8 组 `ThemeShadow`（保留 `GlassSurface` 自身光学层次），降低核显与双显卡设备上的 DWM 合成负载。
- **跨设备与多系统环境适配**：
  - `PlayerService` 开启硬件解码时改为传递 `:avcodec-hw=any`（替代固定 `d3d11va`），在支持的现代显卡上优先硬件加速，在不支持 D3D11VA 的老旧显卡、虚拟机或远程桌面中自动回退 DXVA2 或软解。
  - `MainWindow` 为 `AppWindow.TitleBar` 标题栏按钮配色与 `AppWindow.SetPresenter` 补充异常兜底，避免在不支持紧凑覆盖或自定义标题栏的系统环境下抛出异常。

### 2. 内置站点修复与搜索报错治理
- **修复 `❤️AI剧┃弹幕`（`csp_AiDjGuard`）**：原 Java `ShortDrama` 适配器指向已失效域名 `zzdj.cc`，新增原生 C# `ShortDramaVodProvider`（MacCMS V10 短剧接口，主备源自动故障转移，直链 HLS 播放）。
- **修复 `🎈盘搜┃四盘`（`ZPan`）与 `🦋易搜┃四盘`（`JPan`）超时截断**：为 `SourceHttpClient.GetBytesAsync` 新增可选 `requestTimeout`，`PanSouVodProvider` 按站点配置透传 `25–35s` 超时并使用浏览器 `User-Agent`，避免被固定 20 秒超时或默认 `okhttp` UA 卡断。
- **修复 `🏝奥特┃多线`（`csp_AueteGuard`）搜索 404**：绕过站点首页失效表单 `/aeete5so.php`，改用有效搜索路径 `/so.php?searchword=` 并保留多候选 404 回退，准确触发应用内图片验证码弹窗。
- **站点路由与报错明确化**：
  - `HttpSpiderProvider` 新增支持 `ext` 为明文 HTTP/HTTPS URL 的 `csp_T4` / `csp_T4Guard` 站点；
  - 将内置防骗占位项 `cc`（`请勿信视频中任何广告`）保持由 `PushVodProvider` 降级为空目录，同时从全站搜索与联想站点列表中排除；
  - 优化 `SearchViewModel` 对 HTTP 404（`站点接口变更（404）`）与 502/503/504（`站点服务暂不可用`）的错误提示与诊断日志；
  - 内置配置中 5 个依赖外部闭源混淆 DEX 或加密私有参数的站点（`光影`、`奶酪`、`粤迷`、`花生`、`风云`）无公开可实现接口，继续如实标注为 `（无公开实现）`。

### 3. 性能细节优化与死代码清理
- 优化 `DanmakuOverlay` 逐帧渲染：时间步进 `≤ 0` 且无前跳时早退；未开启描边时跳过 `Shadow.Text` 写入；在复用弹幕节点上缓存已应用的字号/粗体/描边状态，减少重复 DependencyProperty 写入。
- 删除已由 `CloudBlockCache` 替代且无引用的 `Services/Cloud/PrefetchStream.cs`（221 行）；清理 `Styles/Navigation.xaml` 与 `Styles/Controls.xaml` 中 6 个未使用的旧样式；移除 `ImageCacheService` 未使用的构造函数参数；补齐 `SpiderVodClient` 与 `CloudModels` 的 `CultureInfo.InvariantCulture` 格式化。

### 4. 文档精简与事实核对
- 全面核对并精简根目录 `README.md` 及 `docs/` 下全部现行文档（`showcase.md`、`development.md`、`brand-design-system.md`、`motion-spec.md`、`design-guide.md`、`site-verification.md`、`site-matrix.csv`、`docs/README.md`），去除夸大表述，修正构建输出路径、令牌色值与内置站点适配边界说明。

## 验证记录

- **编译**：`dotnet build src/TV/TV.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 --no-restore` 0 错误。
- **内置站点实网探测**：
  - `AiDj`（`ShortDramaVodProvider`）：首页 2 分类 20 条、分类 1398 页、5 组搜索关键词全中（山海 20 / 庆余年 3 / 三体 6 / 繁花 18 / 流浪地球 3）、详情 495 集、360zy 直链后台解码 4307 ms（254 视频帧、251 音频缓冲）；
  - `ZPan`：搜索「流浪地球」返回 38 条、「庆余年」返回 5 条（冷查或高频连查时上游仍有偶发超时）；
  - `奥特`：5 组搜索关键词均准确返回「需要验证码」（不再报 HTTP 404）；
  - `cc`：由 `PushVodProvider` 返回空目录且不再出现在搜索站点列表中。
- **桌面 UIA 回归**：`popup_regression`（19 处弹层与弹幕锚点）、`cast_regression`、`workspace_layout_regression`、`library_regression` 及播放器 3 秒自动隐藏与透明光标验证全部通过。
- **发行产物与增量升级核验**：
  - 完整安装包 `TV-Setup-1.9.4-x64.exe`（138.22 MB）：首次冷扫后三次启动实测 **258 / 217 / 248 ms**（通过 `.iss` 配置守卫与启动耗时门限）；
  - 增量更新包 `TV-Update-1.9.4-from-1.9.3-x64.exe`（9.55 MB，8 个变更文件，8.17 MB 净载荷）：两次启动实测 **1066 / 221 ms**，实机静默升级 `1.9.3 → 1.9.4` 后全部 **962 / 962 文件**与 1.9.4 便携包逐文件 SHA-256 100% 一致；
  - 便携包 `TV-1.9.4-win-x64-portable.zip`（188.49 MB）及全部 3 个 `.sha256` 文件校验通过。
