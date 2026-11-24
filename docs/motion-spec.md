# 流光影院 · 动效工程规范

> 本应用的核心转场与微交互基于 WinUI Composition API 在 Compositor 线程执行（实现于 `src/TV/Views/MotionService.cs` 与 `src/TV/Controls/NavigationMotion.cs`）。

---

## 1. 设计原则

1. **物理弹簧与可打断转向**：位置与缩放动画由 `SpringVector3NaturalMotionAnimation` 驱动，支持在过渡中途重新设定目标并自然延续当前速度。
2. **近临界阻尼**：默认阻尼比在 `0.72–0.92` 区间，避免明显往复回弹拖慢操作节奏。
3. **版本令牌防竞态**：涉及延迟 `Visibility.Collapsed` 的退场动画绑定单调递增 `Token`，旧 `CompositionScopedBatch.Completed` 回调检测到令牌过期即直接忽略，避免快速来回切换时元素被误隐藏。
4. **系统偏好与空闲节流**：统一检查 `MotionService.CanAnimate`（`MotionService.Enabled && UISettings.AnimationsEnabled`）；关闭动效时瞬时落位并清除表达式绑定；进入播放器、离开页面或窗口最小化时暂停装饰性循环动画。

---

## 2. 弹簧与缓动参数（同步自 `MotionService.cs`）

| 场景类别 | 驱动类型 | 参数范围 | 对应方法 |
| --- | --- | --- | --- |
| 按压微缩 / 光学脉冲 | Spring | `Damping 0.72–0.86`, `Period 28–30ms` | `SetCardPressed`、`PulseElement` |
| 通用触感按钮 | Spring | `Damping 0.82–0.84`, `Period 28–34ms` | `AttachTactile` |
| 海报悬停 | Spring | 卡片 `0.80–0.82 / 36ms` · 内图 `0.86 / 44ms` · 播放徽章 `0.74 / 32ms` | `SetCardHover`、`ResetCard` |
| 侧栏 / 分段滑轨 | Spring | 垂直 `0.76 / 36ms`（拉伸 `0.74 / 34ms`）· 水平胶囊 `0.80 / 34ms` · 水平光轨 `0.76 / 36ms` | `GlideVerticalIndicator`、`GlideHorizontalPill`、`GlideHorizontalBar`、`HideNavIndicator` |
| 浮层与 OSD | Spring | `Damping 0.78–0.86`, `Period 28–34ms` | `SetFloatingVisible`、`SetOverlayVisible`、`ShowOsdToast` |
| 页面转场 / 级联入场 | Spring | `EnterPage` / `EnterSection` (`0.89 / 40ms`) / `StaggerEntrance` (`0.90 / 42ms`) | `EnterPage`、`EnterSection`、`Reveal`、`StaggerEntrance` |
| 播放器控制栏 | Spring + CubicBezier | 展开 `0.86–0.88 / 38–40ms` · 收起 `200–210ms EaseInCubic` | `SetPlayerChromeVisible` |
| 缓动曲线 | CubicBezier | `EaseOutExpo (0.16,1,0.30,1)` · `EaseOutQuint (0.22,1,0.36,1)` · `EaseInOutSine (0.37,0,0.63,1)` · `EaseInCubic (0.32,0,0.67,0)` | 透明度过渡、呼吸光点与退场 |

---

## 3. 主要动效编排

### 3.1 页面转场与级联入场
- `TopLevel`：下方 `+14px`、微缩 `0.992` 平滑上浮。
- `DrillIn` / `DrillOut`：右侧 `+30px` 切入 / 左侧 `-24px` 返回。
- `Immersive`：播放器以中心为锚点由 `0.968 → 1.000` 展开。
- `StaggerEntrance`：覆盖点播、详情、搜索、直播、媒体库、我的云盘、投屏、设置页面，默认 `35ms` 错峰（上限 `≤105ms`）；云盘目录进入/返回与搜索站点切换配合方向性 `EnterSection`。

### 3.2 滚动联动（`BindCollapsibleHeader` / `BindCoverParallax`）
- **点播页吸顶筛选坞（`BindCollapsibleHeader`）**：绑定 `ScrollViewerManipulationPropertySet` 表达式在 Compositor 线程驱动顶部页眉与推荐画廊视差上移（`0.48x`）、微缩（`1.0 → 0.955`）并淡出，筛选栏跟随滚动并在顶部吸顶；反向回滑或回顶可随时打断；关闭动效时由 `UnbindCollapsibleHeader` 复位。
- **详情页背景视差（`BindCoverParallax`）**：详情底图随纵滑产生 `0.22x`（上限 `56px`）位移，离页时由 `UnbindCoverParallax` 释放表达式。

### 3.3 统一导航指示器（`Controls/NavigationMotion.cs`）
- 主侧栏、设置分区、搜索站点、直播分组、点播分类、媒体库分段、我的云盘账号卡共 7 处单选导航接入 `NavigationMotion`，自动响应 `ScrollViewer.ViewChanged`、虚拟化容器复用、`SizeChanged` 与主题切换，选中项滚出视口时经 `HideNavIndicator` 淡隐。
- 直播频道长列表保持行内指示条，不做跨百行长距离滑行。

### 3.4 卡片悬停与按钮微反馈
- 点播、搜索、详情推荐、媒体库继续观看与选集矩阵均在容器回收/复用时通过 `ResetCard` 复位缩放与位移，防止悬停状态串位。
- 主导航、主要工具栏、详情选集与播放器控制按钮接入 `AttachTactile` 按压微缩；播放/暂停、弹幕开关、静音、收藏与分类切换接入 `PulseElement`。

### 3.5 浮层、OSD 与播放器控制层
- **搜索预选浮层（`SetOverlayVisible`）**：顶部锚点 `-8px`、`scale (0.975, 0.96)` 展开，退场中途继续输入直接打断回弹。
- **OSD 气囊（`ShowOsdToast`）**：首次出现轻落；连续调节音量/进度/字幕延迟时仅触发 `1.045 → 1.0` 微弹，不重启进场动画。
- **播放器控制层（`SetPlayerChromeVisible`）**：顶部浮岛从 `-20px`、底部控制坞从 `+26px`（`scale 0.982`）展开/收起，退场完成后折叠 `ControlsLayer`；中央暂停键、媒体 HUD、AI 字幕状态条与实时字幕均接入 `SetFloatingVisible`。

### 3.6 状态指示循环动画
- 直播 `ON AIR` 与投屏状态灯使用 `2.2s` 循环呼吸；投屏待机同心圆环使用 `2.8s` 循环扩散。开始播放、离开页面或窗口最小化（`SetWindowMinimized`）时暂停，恢复前台后继续。

---

## 4. 性能与资源管理约束

- **每 Compositor 缓存（`CompositorCache`）**：缓动函数与中心/顶中锚点表达按 `Compositor` 弱引用缓存复用，减少频繁创建 WinRT 对象与 UI 线程枢轴重算。
- **ABI 调用缓存与早退**：`MotionState.TranslationEnabled` 避免重复调用 `SetIsTranslationEnabled`；已处于 `Visibility.Collapsed` 的元素再次请求隐藏时直接返回，不分配新的 `CompositionScopedBatch`。
- **弱引用状态与显式释放**：元素动画状态托管在 `ConditionalWeakTable<UIElement, MotionState>`，避免强引用悬挂；每次 `CompositionScopedBatch` 在 `Completed` 回调中显式释放。
- **播放与最小化节流**：进入播放页时 `MainWindow` 隐藏底层 `LiquidBackdrop`；控制层隐藏后折叠；窗口最小化时挂起循环 Compositor 动画。
