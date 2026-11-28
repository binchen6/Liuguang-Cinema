# 代码书写与工程规范

本文确立「流光影院」的简洁、可执行代码书写与模块化规范，适用于 `src/TV/`、`engine/`、`scripts/` 与 `server/`。

---

## 1. 模块化与反重复原则（DRY）

- **禁止重复造轮子**：同一能力在全仓只保留一份实现，任何超过 2 处复用的逻辑必须抽离至共享模块。
- **既有共享基础设施（新增代码必须优先复用）**：
  - 视觉树遍历：`Controls/VisualTreeHelpers.cs`（`FindDescendant<T>`、`FindDescendantByName`、`FindAncestor<T>`），禁止在页面或控件中重写递归 `VisualTreeHelper` 循环。
  - 主题与资源查找：`Controls/ThemeResources.cs`（`ResolveThemeBrush`、`GetDouble`、`TryGet<T>`、`IsHighContrast`）。
  - 卡片悬停与按压：`Controls/CardHoverTracker.cs` + `Views/MotionService.cs`，禁止在各页面复制 `CardParts` 与四件套指针事件样板。
  - 导航滑轨与滚轮：`Controls/NavigationMotion.cs`、`Controls/ScrollWheelBehavior.cs`。
  - 光学玻璃：`Controls/GlassSurface.cs`、`Controls/GlassOpticsEngine.cs`。
  - 网络与镜像：`Services/Http/SourceHttpClient.cs`、`MediaRequestHeaders`、`GitHubMirrorService`。
  - 图标与样式：`Styles/Tokens.xaml`、`Styles/Icons.xaml`、`Styles/Controls.xaml`、`Styles/Navigation.xaml`。

---

## 2. C# 代码风格（.NET 10 / WinUI 3）

- **语法与空安全**：
  - 启用 `Nullable=enable` 与文件范围命名空间（`namespace TV.Controls;`）；
  - 优先使用 `sealed` 类、集合表达式 `[]`、模式匹配与 `using` 声明；
  - 禁止吞异常的空 `catch` 掩盖业务错误；仅对可预期的平台/互操作降级捕获精确异常并经 `Diagnostics.Log` 记录脱敏日志。
- **命名与职责**：
  - 类型与方法以意图命名，单一职责；页面 `.xaml.cs` 仅负责视图事件桥接、布局响应与动效绑定，业务逻辑、网络请求与持久化严格放在 `ViewModels/` 与 `Services/`。
- **异步、取消与线程安全**：
  - 所有 I/O、网络、探测与异步 UI 刷新方法接收并传递 `CancellationToken`，名称以 `Async` 结尾；
  - 切页、搜索切换或曲目切换时使用代际版本守卫或 `CancellationTokenSource` 取消旧任务，禁止过期结果回写界面；
  - 库层 `await` 使用 `.ConfigureAwait(false)`；LibVLC 调用仅经 `PlayerService` 串行队列执行。
- **零泄露与资源释放**：
  - 页面/控件在 `Unloaded` 中解绑静态事件（如 `MotionService.Changed`、`CollectionChanged`、`PropertyChanged`）、停止计时器并释放 Composition 动画与采集器。

---

## 3. WinUI 3 XAML 与视觉工程规范

- **令牌优先**：颜色、字阶（正文 `≥ 13 DIP`）、间距（`4 DIP` 网格）、圆角与控件尺寸必须引用 `Styles/Tokens.xaml`，禁止在页面内随意硬编码色值或魔数。
- **控件居中与尺寸一致**：
  - 自定义 `ControlTemplate` 内的 `ContentPresenter` 必须绑定 `HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"` 与 `VerticalAlignment="{TemplateBinding VerticalContentAlignment}"`；
  - 禁止在带 `BorderThickness="1"` 的定尺寸按钮内放入同宽高子容器（会导致 2px 溢出偏心）；
  - 同一操作行内并排的按钮、输入框与下拉框保持统一高度（如工具栏统一 `40 DIP`，主次操作组统一 `44 DIP`）；
  - 滑块统一使用 `GlassSliderStyle`，下拉按钮统一使用 `GlassDropDownButtonStyle` / `AnchoredChoice` / `PlayerDropDownButtonStyle`，矢量图标统一使用 `LineIcon`（禁止混用 `FontIcon` 或 Emoji 充当功能图标）。
- **双主题玻璃边界**：
  - 深色主题 = **月光 + 透光玻璃**（冷银月光高通透、无磨砂颗粒）；
  - 浅色主题 = **日光 + 磨砂玻璃**（暖白日光柔雾漫射、自动微晶磨砂纹理）；
  - 播放器控件（`PlayerGlass*`、`GlassOpticsProfile.Cinema`）保持独立影院光学材质，不参与深浅主题切换；
  - 严禁在 LibVLC `SwapChainPanel` 上叠加 `ThemeShadow` 或新增 `AcrylicBrush`；虚拟化列表项在交互前不预创建 Composition 光效树。
- **无障碍与 UIA 稳定**：
  - 图标按钮必须提供 `AutomationProperties.Name` 与 `ToolTipService.ToolTip`；
  - 变更既有 UIA 名称前须同步核对 [设计契约](design-guide.md) 与 `scripts/` 回归脚本。

---

## 4. 安全、文档与验收门禁

- **零明文凭据**：严禁将 Token、Cookie、API Key 或本机绝对路径提交入仓；敏感信息经 DPAPI 加密落库，日志统一经 `Diagnostics.Redact` 脱敏。
- **仓库清洁**：禁止提交临时测试产物、构建输出或临时脚本至根目录；开发运行始终使用 `TV_DATA_ROOT` 隔离数据。
- **提交前必跑**：
  1. `Release x64` 零警告零错误编译；
  2. `pwsh -NoProfile -File ./scripts/run-release-regressions.ps1` 全部通过；
  3. 涉及 UI/光学改动时，使用隔离配置启动实机截图核验深色、浅色与播放器细节。
