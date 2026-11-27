# 流光影院 · 动效工程规范

> 本应用核心转场、导航指示与光学玻璃交互全部由 WinUI Composition API 在 DWM Compositor 线程驱动（实现于 `Views/MotionService.cs`、`Controls/NavigationMotion.cs` 与 `Controls/GlassOpticsEngine.cs`）。

---

## 1. 设计原则

1. **物理弹簧与可打断转向**：位置与缩放由 `SpringVector3NaturalMotionAnimation` 驱动，过渡中途改向自然衔接当前速度。
2. **近临界阻尼**：默认阻尼比 `0.72–0.92`，避免多余回弹拖慢节奏。
3. **版本令牌防竞态**：延迟 `Visibility.Collapsed` 的退场回调绑定单调递增 `Token`，过期回调自动忽略。
4. **系统偏好与零空闲消耗**：统一遵循 `MotionService.CanAnimate`（`MotionService.Enabled && UISettings.AnimationsEnabled`）；关闭动效即刻静态落位；进入播放器、离页或窗口最小化时挂起循环动效，交互光学层静止时 0 动画、0% GPU 开销。

---

## 2. 弹簧、缓动与光学参数

| 场景类别 | 驱动类型 | 参数范围 | 对应实现 |
| --- | --- | --- | --- |
| 按压微缩 / 光学脉冲 | Spring | `Damping 0.72–0.86`, `Period 28–30ms` | `SetCardPressed`、`PulseElement` |
| 通用触感按钮 | Spring | `Damping 0.82–0.84`, `Period 28–34ms` | `AttachTactile` |
| 海报悬停 | Spring | 卡片 `0.80–0.82 / 36ms` · 内图 `0.86 / 44ms` · 播放徽章 `0.74 / 32ms` | `SetCardHover`、`ResetCard` |
| 侧栏 / 分段滑轨 | Spring | 垂直 `0.76 / 36ms` · 水平胶囊 `0.80 / 34ms` · 水平光轨 `0.76 / 36ms` | `GlideVerticalIndicator`、`GlideHorizontalPill`、`GlideHorizontalBar` |
| 浮层与 OSD | Spring | `Damping 0.78–0.86`, `Period 28–34ms` | `SetFloatingVisible`、`SetOverlayVisible`、`ShowOsdToast` |
| 页面转场 / 级联入场 | Spring | `EnterPage` / `EnterSection` (`0.89 / 40ms`) / `StaggerEntrance` (`0.90 / 42ms`) | `EnterPage`、`EnterSection`、`Reveal`、`StaggerEntrance` |
| 播放器控制栏 | Spring + CubicBezier | 展开 `0.86–0.88 / 38–40ms` · 收起 `200–210ms EaseInCubic` | `SetPlayerChromeVisible` |
| **玻璃光学交互** | Compositor Scalar / Vector2 | 悬停淡入 `160ms` / 淡出 `220ms` · 指针追踪 `≥8ms` (120Hz) 节流 · 点击焦散波纹 `380ms` | `GlassOpticsEngine`（透射/折射/Fresnel/波纹） |
| 缓动曲线 | CubicBezier | `EaseOutExpo (0.16,1,0.30,1)` · `EaseOutQuint (0.22,1,0.36,1)` · `EaseInCubic (0.32,0,0.67,0)` | 透明度、呼吸灯与退场 |

---

## 3. 核心动效编排

### 3.1 页面转场与级联入场
- `TopLevel`：下方 `+14px`、微缩 `0.992` 平滑上浮；`DrillIn / DrillOut`：`+30px / -24px` 水平进出；`Immersive`：播放器自中心 `0.968 → 1.000` 展开。
- `StaggerEntrance` 覆盖全部 8 个业务页面（默认 `35ms` 错峰，总上限 `≤105ms`）；云盘目录进出与搜索站点切换使用方向性 `EnterSection`。

### 3.2 滚动联动（`BindCollapsibleHeader` / `BindCoverParallax`）
- **点播页吸顶筛选坞**：`ScrollViewerManipulationPropertySet` 在 Compositor 线程驱动顶部画廊 `0.48x` 视差上移、微缩（`1.0 → 0.955`）并淡出，筛选栏平滑吸顶。
- **详情页背景视差**：底图随纵滑产生 `0.22x`（上限 `56px`）位移，离页通过 `UnbindCoverParallax` 释放。

### 3.3 统一导航指示器（`Controls/NavigationMotion.cs`）
- 主侧栏、设置分区、搜索站点、直播分组、点播分类、媒体库分段与云盘账号卡共 7 处单选导航统一接入，自动响应滚动、虚拟化回收、`SizeChanged` 与主题切换；直播频道长列表保持行内指示条。

### 3.4 卡片悬停、按钮微反馈与光学玻璃交互（`GlassOpticsEngine`）
- 点播、搜索、详情推荐、媒体库继续观看卡片与选集矩阵在容器回收时经 `ResetCard` 复位，杜绝悬停串位；工具栏与核心按钮挂载 `AttachTactile` 与 `PulseElement`。
- **光学玻璃实时反馈（`GlassOpticsEngine`）**：
  - 指针移入时 Compositor 线程淡入体积透射光斑、Fresnel 倒角色散高光与顶部全内反射游走丝；
  - 指针移动按 `≥8ms` 节流更新常驻径向画刷中心与 Snell 反向折射偏移（不重建画刷、不触发布局）；
  - 按下时在落点释放 `380ms` 扩散衰减的焦散折射波纹；指针离开且波纹结束后归零静默。

### 3.5 浮层、OSD 与播放器控制层
- **搜索预选浮层**：顶锚 `-8px`、`scale (0.975, 0.96)` 展开，退场中途继续输入直接打断回弹。
- **OSD 气囊**：连续调音量/进度/字幕延迟仅触发 `1.045 → 1.0` 微弹。
- **播放器控制层**：顶部浮岛 `-20px`、底部控制坞 `+26px` 展开/收起，退场完成后折叠 `ControlsLayer`；静音播控零残留合成开销。

### 3.6 状态呼吸灯
- 直播 `ON AIR` 与投屏指示灯使用 `2.2s` 循环呼吸，投屏待机同心圆环使用 `2.8s` 扩散；开始播放、离页或窗口最小化时自动暂停。

---

## 4. 性能与资源管理约束

- **每 Compositor 缓存（`CompositorCache`）**：缓动与锚点表达按 `Compositor` 弱引用复用，减少 WinRT 对象分配。
- **ABI 缓存与早退**：已处于 `Visibility.Collapsed` 的元素重复隐藏直接返回；`GlassOpticsEngine` 仅在指针交互期间运行，空闲 0 动画。
- **弱引用托管与显式释放**：动效与光学状态均托管于 `ConditionalWeakTable<TElement, TState>`，`CompositionScopedBatch` 在完成回调即刻释放；进入播放页同步隐藏 `LiquidBackdrop`。
