# 流光影院 1.9.2 发行说明

发布日期：2026-11-24

## 核心更新

### 1. 播放期间内核级防息屏与防休眠（默认开启）
- 新增 `PlaybackKeepAwakeService`（`src/TV/Services/Playback/PlaybackKeepAwakeService.cs`）：基于线程无关的 Windows 内核电源可用性请求 API（`PowerCreateRequest` / `PowerSetRequest` Display+System / `PowerClearRequest`，不支持时自动降级 `SetThreadExecutionState`），支持播放器与投屏接收双源独立引用计数，进程异常退出由内核自动回收句柄。
- 默认开启；视频开始播放立即生效，暂停、停止、离开页面或退出应用立即释放；支持在 **设置 → 播放（播放时防止息屏与休眠）** 与播放器顶部 **更多选项** 菜单随时切换，且兼容 Windows「打开方式」直达播放模式。

### 2. 播放器 3 秒悬停感知自动隐藏与同步隐藏鼠标
- 将播放器无操作自动收起时间由 5 秒缩短为 **3 秒**，并修复此前打开影片未移动鼠标时自动隐藏计时器未随播放启动而触发的问题。
- 新增全控件悬停感知（覆盖顶部双分体浮岛、底部悬浮控制坞、中央暂停圆镜、AI 字幕浮条及所有状态卡片）：鼠标停留在任一控件表面、拖拽进度条或打开菜单/对话框期间锁定控制层不收起；离开控件无操作 3 秒后控制层平滑沉降并折叠，同时通过平衡计数 Win32 `ShowCursor` + 前台窗口区域校验同步隐藏鼠标指针（移动超过 `1.5 DIP` 微抖阈值、暂停、离窗或 Alt+Tab 失焦即刻恢复指针）。

### 3. 五层光学磨砂玻璃重构（`Controls/GlassSurface`）与全站性能优先治理
- 修订 [品牌设计语言规范（SHIRO · MA · OPTICAL GLASS）](../brand-design-system.md) 与 [设计契约](../design-guide.md)，自研零每帧 CPU 开销的五层光学玻璃复合控件 `Controls/GlassSurface`：
  - **解决 WinUI 3 `SwapChainPanel` 硬件限制**：实机排查确认 WinUI 3 `AcrylicBrush` 直接叠在 LibVLC `SwapChainPanel` 之上会因无法采样硬件视频交换链而输出实心黑块；播放器全面切换至 SwapChain 安全光学磨砂方案——**半透明烟熏晶透基底 + 顶左乳白磨砂体积光影 + 1:1 共享酸蚀微晶磨砂纹理（零固有布局尺寸） + 双层菲涅尔厚玻璃倒角 + 顶部 1px 焦散高光丝 + 克制 `ThemeShadow`**，视频画面与光影清透呈现且兼具细腻磨砂厚度与悬浮透视。
  - **播放器 UI 全面重塑**：顶部由整宽深条升级为左右分体轻量玻璃浮岛（彻底敞开画面上方视野），底部控制坞修身减高至紧凑单行比例，进度条滑块消除默认深灰厚环改为珠光白环配朱砂红核，播放/暂停与音量按钮全部统一至 24×24 建筑几何矢量 `LineIcon`。
  - **全站其他页面细节升级与 GPU 模糊预算治理**：升级主侧栏（新增右侧竖向受光高光丝）、点播页吸顶筛选坞与悬浮回顶圆镜、搜索页命令栏/站点轨/预选浮层、媒体库分段滑轨与滑行胶囊、云盘路径带与紧凑空态、直播左右面板、投屏控制台/预览坞、设置分区侧轨及详情双栏工作台，并校准 `LiquidBackdrop` 环境光场与深/浅双主题定向菲涅尔描边；同时将海报角标、虚拟化列表图标托与嵌套导航选中底板改为零模糊透明晶体渐变，**消除点播/搜索滚动期 30–40 个并发背景模糊 Pass**，兼顾高级玻璃质感与 120Hz 流畅帧率。

### 4. 全站动效补全与 GPU Compositor 管线深度优化（零动效削减）
- **Compositor 单例缓存与 GPU 表达式锚点**：在 `MotionService` 引入 `CompositorCache` 弱引用缓存 4 条缓动曲线与 2 条中心/顶中锚点 GPU `ExpressionAnimation`（`this.Target.Size`），彻底消除每次悬停/转场的重复 WinRT COM 分配与全部 UI 线程 `SizeChanged` 枢轴重算，且新展开浮层第 0 帧即精准对齐几何中心。
- **静默零开销策略**：播放器控制层离场完成后经令牌守卫 `CompositionScopedBatch` 自动折叠；进入播放器折叠底层 `LiquidBackdrop`；窗口最小化至任务栏/托盘自动挂起呼吸灯与同心雷达循环动画（恢复即续播）；缓存 `SetIsTranslationEnabled` 跨 ABI 状态并短路已折叠元素重复隐藏。
- **补全缺失页面与控件动效**：为搜索、媒体库、云盘、直播、投屏、设置补齐首屏 `StaggerEntrance` 错峰入场，补齐云盘目录钻取/返回方向性 `EnterSection`、搜索站点切换过渡、媒体库继续观看虚拟化复用 `ResetCard`、详情页选集矩阵触感回弹与 `UnbindCoverParallax` 释放，以及播放器全部 15 个控制按钮触感反馈与 HUD/AI 字幕/暂停圆镜可打断浮层动效。

## 验证范围

- `dotnet build src/TV/TV.csproj -c Release --no-restore`：0 错误，全站 XAML 字号审计全部 `>= 12px`。
- `PlaybackKeepAwakeService` 实机单元测试通过（验证默认开启、真实 Windows 内核 `PowerCreateRequest` / `PowerSetRequest(Display+System)` 持锁、开关即时切换、播放器+投屏双源引用计数与 `Dispose` 干净释放）。
- 播放器与全应用深/浅双主题实机视觉与交互验收：
  - 播放直达模式实测 25s 彩色高清视频：`KeepAwake acquired (PowerRequest Display+System)` 自动持锁 → 暂停即时释放 → 恢复播放后 **精确 3.000s** 触发 `Player chrome auto-hidden after 3s idle` 并折叠控制层；验证顶部双分体光学磨砂浮岛、底部修身控制坞、76×76 暂停光学圆镜、珠光朱砂进度条滑块与菜单内「播放时防止息屏与休眠」开关；
  - 主应用实机遍历点播（含真实海报流与下滑吸顶磨砂筛选坞）、搜索、媒体库、云盘、直播、投屏与设置（验证「播放时防止息屏与休眠」默认开启）以及深色「暗室素墨」/ 浅色「素白画廊」双主题，`last-error.log` 均为空。
- 发行产物、秒开防回退验速与增量一致性核验：
  - 完整安装包 `TV-Setup-1.9.2-x64.exe`（138.21 MB）：三次启动实测 **279 / 224 / 218 ms**（通过冷启 ≤ 2500 ms、热启 ≤ 1000 ms 及 `.iss` 守卫门限）；
  - 增量更新包 `TV-Update-1.9.2-from-1.9.1-x64.exe`（9.57 MB，20 个变更文件，8.24 MB 净载荷）：两次启动实测 **993 / 235 ms**，实机静默升级 `1.9.1 → 1.9.2` 全部 **961/961 文件** SHA-256 与 1.9.2 便携包 **100% 逐字节一致**；
  - 便携免安装包 `TV-1.9.2-win-x64-portable.zip`（188.47 MB）及全部 `.sha256` 校验文件均通过核验。
