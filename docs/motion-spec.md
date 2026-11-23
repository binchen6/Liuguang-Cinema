# 流光影院 · 动效工程规范（Fluid Kinetic Grammar）

> 以顶级动效师水准设计的全链路 **WinUI Composition 物理动效体系**。所有核心动效均运行在 GPU Compositor 线程，遵循**动量守恒、随时可打断、克制服务内容、零阻塞主线程**四大铁律。

---

## 1. 动效核心哲学

1. **真实物理，而非时间轴死板插值**
   - 位置、缩放、形变优先使用 `SpringVector3NaturalMotionAnimation` 与 `SpringScalarNaturalMotionAnimation`。
   - 任何时候用户快速连点导航、连续悬停掠过海报、在控制栏收起途中重新晃动鼠标，Compositor 原生继承当前瞬时速度并平滑转向新目标（Velocity Retargeting），**绝不跳帧、绝不倒带闪烁**。
2. **克制的戏剧张力（原研哉式节律）**
   - 像和纸翻页与光学快门一样利落：无多余回弹抖动（默认临界近阻尼 `0.78–0.92`），入场轻盈有呼吸感，退场干脆不拖泥带水。
3. **版本令牌防竞态（Token-Guarded Interruptibility）**
   - 所有涉及延迟隐藏（`Visibility.Collapsed`）或级联入场的动效，均绑定 per-element 单调递增 `Token`；旧动画的 `ScopedBatch.Completed` 回调若检测到 `Token` 过期，立即静默放弃，彻底杜绝闪烁与元素消失 Bug。
4. **严格无障碍与节能守卫**
   - 统一读取 `MotionService.CanAnimate`（`MotionService.Enabled && UISettings.AnimationsEnabled`）。
   - 关闭动画时所有 API 瞬时落位并清空残留动画；进入播放器沉浸播放时自动停止页面装饰性循环动画，把全部帧预算留给视频解码与弹幕 VSync。

---

## 2. 物理弹簧与缓动令牌（`src/TV/Views/MotionService.cs`）

| 令牌名称 | 类型 | 参数 | 典型场景 |
| --- | --- | --- | --- |
| `SnappySpring` | Spring | `Damping = 0.84`, `Period = 30ms` | 按钮按压回弹、播放键脉冲、徽章弹出 |
| `TactileSpring` | Spring | `Damping = 0.80`, `Period = 36ms` | 海报悬停抬升、卡片内图片微缩放、浮层按钮 |
| `ElasticGlideSpring` | Spring | `Damping = 0.76`, `Period = 36ms` | 侧栏导航条、分段控件胶囊、设置分区滑轨 |
| `FluidPageSpring` | Spring | `Damping = 0.88`, `Period = 44ms` | 页面转场、播放器悬浮坞显隐、搜索预选展开 |
| `GentleEditorialSpring` | Spring | `Damping = 0.92`, `Period = 50ms` | 详情英雄区切换、列表级联入场、沉浸页入场 |
| `EaseOutExpo` | CubicBezier | `(0.16, 1.00, 0.30, 1.00)` | 透明度淡入、遮罩展开 |
| `EaseOutQuint` | CubicBezier | `(0.22, 1.00, 0.36, 1.00)` | 章节淡入、播放器控制层唤醒 |
| `EaseInCubic` | CubicBezier | `(0.32, 0.00, 0.67, 0.00)` | 浮层退场、控制层收起 |

---

## 3. 全链路动效编排目录

### 3.1 页面级空间编排（`EnterPage` + `StaggerEntrance`）
- **TopLevel（一级平级切换）**：内容区从下方 `+14px`、微缩 `0.992` 平滑上浮落定，透明度 `210ms EaseOutQuint`。
- **DrillIn（进入详情 / 搜索）**：内容区从右侧 `+30px`、微缩 `0.984` 顺滑推进，建立明确的空间纵深。
- **DrillOut（返回上级）**：内容区从左侧 `-24px`、微放大 `1.012` 轻柔回归，还原空间记忆。
- **Immersive（进入播放器）**：以中心为锚点从 `0.968 → 1.000` 暗室展开，透明度 `250ms`。
- **画册级联（`StaggerEntrance`）**：页眉 → 画廊/工具坞 → 主体区按 `0 / 35 / 70ms` 微错峰入场，总延迟上限 `≤105ms`，保证响应极快且层次分明；快速切页时自动作废未触发的级联令牌。

### 3.2 弹性形变滑行指示器（`GlideVerticalIndicator` / `GlideHorizontalIndicator`）
- **应用侧栏**与**设置分区侧轨**：计算当前位置与目标 `Y` 的真实位移距离 `Δy`；位移期间 `Translation.Y` 按 `ElasticGlideSpring` 滑行，同时 `Scale.Y` 根据 `Δy` 产生轻微瞬时拉伸（最高 `1.42x`）再回弹至 `1.0x`，赋予指示条水滴/流光般的有机触感。
- **媒体库分段滑轨**：水平磨砂胶囊底板（`GlideHorizontalPill`）同步弹簧插值 `Translation.X` 与 `Scale.X` 宽度比例，在四个标签间如水银般丝滑穿行且随时可中途折返。

### 3.3 双层深度海报卡片动效（`SetCardHover` / `SetCardPressed`）
- **悬停态**：
  - 卡片整体轻柔抬升 `Translation.Y = -4px`、`Scale = 1.022`；
  - 卡片内部裁切框中的**海报图片独立微缩放 `Scale = 1.055`**，营造美术馆画框内的景深呼吸感；
  - 磨砂遮罩按 `150ms EaseOutQuint` 渐显，中央磨砂圆镜播放徽章以 `0.76 → 1.00` 弹簧弹出。
- **按压态**：卡片瞬时回弹至 `Scale = 0.978`、`Translation.Y = -1px`，松手或移出时无缝衔接悬停态或静止态。

### 3.4 通用触感微交互（`AttachTactileButton` / `AttachSubtleLift`）
- 为高频主操作按钮、图标按钮、选集网格按钮与快捷入口附加轻量级 Compositor 触感：`PointerEntered` 轻微抬升/高亮，`PointerPressed` 瞬时阻尼微缩（`0.965x`），`PointerReleased / Canceled` 弹簧回弹；不破坏控件原有 `VisualStateManager` 焦点与无障碍样式。

### 3.5 GPU 滚动驱动表达式动画（`BindCoverParallax` / `BindScrollFade`）
- 直接绑定 `ElementCompositionPreview.GetScrollViewerManipulationPropertySet(scrollViewer)`：
  - 详情页顶部环境光底图随页面向下滚动产生 `0.22x` 柔和视差（`ExpressionAnimation`），全程运行在 Compositor 线程，120Hz 高刷屏幕下丝滑零掉帧。
  - 点播页滚动超过阈值时，吸顶磨砂筛选坞通过平滑弹簧切入悬浮形态，并弹出磨砂圆形「回到顶部」按钮。

### 3.6 浮层与预选面板编排（`SetFloatingVisible` / `SetOverlayVisible`）
- 搜索页非抢焦预选浮层（`SuggestionsOverlay`）：以顶部为锚点，从 `offsetY: -8px`、`scale: 0.97` 弹性展开；失焦或按 `Esc` 时干脆淡出并在完成后安全折叠，若折叠中途用户继续键入则立即中断退场、无缝弹回展开。

### 3.7 影院播放器编排（`SetPlayerChromeVisible` / `PulsePlayFeedback` / `ShowOsdToast`）
- **悬浮控制层**：顶栏从 `-22px` 滑入、底栏烟熏磨砂坞从 `+26px` + `scale 0.982` 弹簧浮起；5 秒无操作自动收起时平滑沉降，任一时刻鼠标移动、点击或按键瞬间反向接管当前位置与速度。
- **播放/暂停脉冲（`PulsePlayFeedback`）**：点击或按空格切换播放态时，按钮触发一次干脆的光学阻尼脉冲（`0.88 → 1.0`），配合零延迟图标切换强化即时掌控感。
- **OSD 气囊（`ShowOsdToast`）**：调节音量、倍速、进度、字幕延迟时，顶部磨砂 OSD 气泡从 `-10px` 弹簧轻落；若连续按键微调（如连按方向键快进），已显示的 OSD 触发一次微妙的呼吸轻弹（`1.04 → 1.0`）而非闪烁重启。

### 3.8 呼吸光点与雷达波纹（`SetBeaconPulsing` / `SetRadarRingPulsing`）
- 直播页 `ON AIR` 朱砂指示点与投屏页接收状态灯：采用 `2.2s` 平滑 Compositor 循环呼吸（透明度 `0.45 ↔ 1.0`、微缩放 `0.90 ↔ 1.12`）。
- 投屏页等待投送时，舞台中央几何同心光环缓慢向外扩散淡出，开始播放或离开页面时立即释放动画。

---

## 4. 性能与可维护性约束

- 所有 Compositor 视觉状态均通过 `ConditionalWeakTable<UIElement, MotionState>` 弱引用管理，**绝不产生页面或控件内存泄漏**。
- 列表/网格虚拟化容器复用的子元素在 `PointerExited` / `PointerCanceled` / `Unloaded` 时确保复位，不将悬停缩放残留到复用的新卡片上。
- `MotionService.CanAnimate == false` 时，必须同步停止所有循环与过渡动画并强制归零 `Translation` / `Scale = 1` / 目标 `Opacity`。
