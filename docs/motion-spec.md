# 流光影院 · 动效工程规范（Fluid Kinetic Grammar）

> 全链路 **WinUI Composition GPU 物理动效体系**（实现于 `src/TV/Views/MotionService.cs`）。遵循**动量守恒、随时可打断、克制服务内容、零阻塞 UI 线程**四项原则。

---

## 1. 核心设计原则

1. **物理弹簧与动量继承（Velocity Retargeting）**
   位置、缩放与形变均由 `SpringVector3NaturalMotionAnimation` 在 Compositor 线程驱动。连点导航、掠过海报或控制栏收起中途移动鼠标时，视觉原生继承当前瞬时速度平滑转向，零跳帧、零倒带闪烁。
2. **克制节律（近临界阻尼）**
   默认阻尼比落在 `0.74–0.92`，呈现和纸翻页与光学快门般的利落手感，避免廉价弹性晃动。
3. **版本令牌防竞态（Token-Guarded Interruptibility）**
   所有涉及延迟折叠（`Visibility.Collapsed`）或错峰级联的动画均绑定单调递增 `Token`；旧动画 `CompositionScopedBatch.Completed` 若检测到过期令牌即静默放弃，杜绝闪烁与元素消失。
4. **无障碍与帧预算守卫**
   统一判断 `MotionService.CanAnimate`（`MotionService.Enabled && UISettings.AnimationsEnabled`）。关闭动画时瞬时落位并清空残留表达式；进入播放器或离开页面时停止装饰性循环动画，把帧预算留给解码与 VSync 弹幕。

---

## 2. 物理弹簧与缓动参数（同步自 `MotionService.cs`）

| 场景类别 | 驱动类型 | 核心参数 | 对应方法 |
| --- | --- | --- | --- |
| 按压微缩 / 光学脉冲 | Spring | `Damping 0.72–0.86`, `Period 28–30ms` | `SetCardPressed`、`PulseElement` |
| 通用触感按钮 | Spring | `Damping 0.82–0.84`, `Period 28–34ms` | `AttachTactile` |
| 海报悬停双层景深 | Spring | 卡片 `0.80–0.82 / 36ms` · 内图 `0.86 / 44ms` · 播放徽章 `0.74 / 32ms` | `SetCardHover`、`ResetCard` |
| 侧栏 / 分段弹性滑轨 | Spring | 垂直 `0.76 / 36ms`（拉伸 `0.74 / 34ms`）· 水平胶囊 `0.80 / 34ms` · 水平光轨 `0.76 / 36ms` | `GlideVerticalIndicator`、`GlideHorizontalPill`、`GlideHorizontalBar`、`HideNavIndicator` |
| 浮层与 OSD 气囊 | Spring | `Damping 0.78–0.86`, `Period 28–34ms` | `SetFloatingVisible`、`SetOverlayVisible`、`ShowOsdToast` |
| 页面转场 / 级联入场 | Spring | `EnterPage` / `EnterSection` (`0.89 / 40ms`) / `StaggerEntrance` (`0.90 / 42ms`) | `EnterPage`、`EnterSection`、`Reveal`、`StaggerEntrance` |
| 播放器悬浮控制坞 | Spring + CubicBezier | 展开 `0.86–0.88 / 38–40ms` · 收起 `200–210ms EaseInCubic` | `SetPlayerChromeVisible` |
| 缓动曲线 | CubicBezier | `EaseOutExpo (0.16,1,0.30,1)` · `EaseOutQuint (0.22,1,0.36,1)` · `EaseInOutSine (0.37,0,0.63,1)` · `EaseInCubic (0.32,0,0.67,0)` | 透明度淡入淡出、呼吸光点与退场 |

---

## 3. 全链路动效编排目录

### 3.1 页面空间转场与画册级联（`EnterPage` / `EnterSection` / `StaggerEntrance`）
- `TopLevel`：下方 `+14px`、微缩 `0.992` 平滑上浮落定。
- `DrillIn` / `DrillOut`：右侧 `+30px` 推进 / 左侧 `-24px` 轻柔回归，建立清晰空间纵深。
- `Immersive`：播放器以中心为锚点由 `0.968 → 1.000` 暗室展开。
- `StaggerEntrance`：已全覆盖点播、详情、搜索、直播、媒体库、我的云盘、投屏、设置全部一级与二级页面；按默认 `35ms` 错峰（上限 `≤105ms`）引导页眉、工具坞与主体区依次浮现，云盘目录钻取/返回与搜索站点切换均配方向性 `EnterSection`。

### 3.2 GPU 1:1 滚动联动与可打断吸顶收起（`BindCollapsibleHeader` / `BindCoverParallax` / `BindProgressiveBlur`）
- **`BindCollapsibleHeader` / `UnbindCollapsibleHeader`（点播页）**：直接绑定 `ScrollViewerManipulationPropertySet`，在 GPU 线程以 120Hz+ 零布局抖动驱动整套收起编排：
  - 顶部画册报头与展厅卡片随下滑以 `0.48x` 视差上移、景深微缩（`1.0 → 0.955`）并平滑淡出，父级容器自动附加 GPU `InsetClip` 防止越界进入标题栏；
  - 筛选栏 1:1 跟手上移并在 `Y = 0` 处齐平吸顶，五层光学磨砂底板同步渐显、底部分割线平滑淡出；
  - 任意时刻反向回滑或点击回顶均 100% 连续可打断；关闭动效时由 `UnbindCollapsibleHeader` 静默复位。
- **`BindCoverParallax` / `UnbindCoverParallax`（详情页）**：环境柔光底图随页面向下滚动产生 `0.22x`（上限 `56px`）柔和视差；监听 `MotionService.Changed` 实时切换，离页由 `UnbindCoverParallax` 彻底释放表达式。
- **`BindProgressiveBlur`**：支持头部透明与轻视差随滚动平滑衰减。

### 3.3 统一弹性形变导航控制器（`Controls/NavigationMotion.cs` + `GlideVerticalIndicator` / `GlideHorizontalPill` / `GlideHorizontalBar`）
- 全站 7 处导航统一接入 `NavigationMotion`（`AttachVerticalList` / `AttachVerticalCustom` / `AttachHorizontalList`），自动监听 `ScrollViewer.ViewChanged`、容器虚拟化复用、`SizeChanged` 与 `ActualThemeChanged` 重锚定，选中项移出视口时经 `HideNavIndicator` 洁净淡隐，按用途差异化配置合理细节：
  - **竖向导航（主侧栏 / 设置分区 / 搜索站点 / 直播分组）**：`GlideVerticalIndicator` 依据位移 `Δy` 驱动 `Translation.Y` 并触发瞬时纵向拉伸回弹；高频百项的直播频道列表克制使用 `VerticalNavInlineItemStyle` 行内指示，不做跨屏长跳。
  - **横向胶囊 / 分段（点播分类 / 媒体库）**：外层共享 `ScrollViewer` + `GlideHorizontalPill`（`PillMatchWidth`），滑行胶囊与选项同帧跟随无滚动延迟；媒体库附加水平方向性 `EnterSection`。
  - **横向状态卡（我的云盘）**：`GlideHorizontalBar`（`BottomAccentBar`）在四张网盘卡底缘居中弹性滑行拉伸，并配合账号切换方向性 `EnterSection`。

### 3.4 双层深度海报与触感微交互（`SetCardHover` / `SetCardPressed` / `ResetCard` / `AttachTactile` / `PulseElement`）
- **海报悬停**：卡片整体抬升并微放大，裁切框内海报图片独立微缩放营造画框景深，中央磨砂圆镜播放徽章弹簧弹出；点播、搜索、详情推荐、媒体库继续观看与详情选集矩阵在虚拟化容器复用时均通过 `ResetCard` 瞬间复位，杜绝悬停态残留。
- **全站高频触感**：`AttachTactile` 完整覆盖主导航、点播、详情（含全部选集按钮）、搜索、直播、媒体库、云盘、投屏预览与播放器全部 15 个控制按钮；`PulseElement` 为播放/暂停、弹幕开关、音量静音、收藏按钮与分类胶囊提供干脆的光学阻尼脉冲（`0.88–0.93 → 1.0`）。

### 3.5 浮层、OSD 与播放器编排（`SetFloatingVisible` / `SetOverlayVisible` / `ShowOsdToast` / `SetPlayerChromeVisible`）
- **非抢焦预选浮层（`SetOverlayVisible`）**：以顶部为锚点从 `-8px`、`scale (0.975, 0.96)` 弹性展开；退场中途继续键入立即中断并无缝回弹。
- **OSD 气囊（`ShowOsdToast` / `HideOsdToast`）**：首次出现从 `-10px` 弹簧轻落；连续按键调节音量/进度/字幕延迟时仅触发 `1.045 → 1.0` 呼吸微弹，不闪烁重启。
- **播放器控制坞与浮层（`SetPlayerChromeVisible` / `SetFloatingVisible`）**：顶部分体浮岛从 `-20px`、底部悬浮坞从 `+26px`（`scale 0.982`）平滑沉降或弹簧浮起，离场动画完成后经令牌守卫 `CompositionScopedBatch` 自动折叠 `ControlsLayer`（静默播放期 0% 合成开销，任意时刻唤醒即时中断并继承动量）；中央暂停圆镜、媒体信息 HUD、AI 字幕进度条与实时双语字幕均统一接入 `SetFloatingVisible` 可打断进退场。

### 3.6 呼吸光点与同心雷达波纹（`SetBeaconPulsing` / `SetRadarRingPulsing`）
- 直播 `ON AIR` 朱砂指示点与投屏状态灯采用 `2.2s` Compositor 循环呼吸（透明度 `0.38 ↔ 1.0`、缩放 `0.88 ↔ 1.08`）。
- 投屏待机舞台同心光环以 `2.8s` 周期向外扩散淡出（`0.92 → 1.55`），开始播放、离页或窗口最小化（`SetWindowMinimized`）时即刻挂起，恢复窗口自动续播。

---

## 4. 内存与 GPU 性能架构约束

- **每 Compositor 实例缓存（`CompositorCache`）**：4 条缓动曲线（`CubicBezierEasingFunction`）与 2 条中心/顶中锚点 GPU 表达式（`Vector3(this.Target.Size.X * 0.5, ...)`）按 `Compositor` 弱引用单例缓存，彻底消除每次悬停/转场的 WinRT COM 对象分配与所有 UI 线程 `SizeChanged` 枢轴重算，且新展开浮层第 0 帧即精准对齐几何中心。
- **ABI 缓存与零冗余批处理**：`MotionState.TranslationEnabled` 缓存后每个元素全生命周期仅跨一次 `SetIsTranslationEnabled` WinRT ABI；已处于 `Visibility.Collapsed` 的元素再次请求隐藏时直接短路，零 `ScopedBatch` 分配。
- **沉浸播放与最小化零闲置开销**：进入播放页时 `MainWindow` 自动折叠底层 `LiquidBackdrop`；播放控制层自动隐藏后折叠；窗口最小化至任务栏/托盘时自动挂起全部无限循环 Compositor 动画。
- **内存安全与无障碍兜底**：所有视觉状态通过 `ConditionalWeakTable<UIElement, MotionState>` 弱引用托管，零内存泄漏；所有 `CompositionScopedBatch` 显式释放；`MotionService.CanAnimate == false` 时同步停止全部循环与过渡动画并瞬时落位。
