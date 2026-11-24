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
| 侧栏 / 分段弹性滑轨 | Spring | 垂直 `0.76 / 36ms`（拉伸 `0.74 / 34ms`）· 水平胶囊 `0.78 / 36ms` | `GlideVerticalIndicator`、`GlideHorizontalPill` |
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
- `StaggerEntrance`：按默认 `35ms` 错峰引导页眉、工具坞与主体区依次浮现，快速切页时自动作废未触发步骤。

### 3.2 GPU 1:1 滚动联动与可打断吸顶收起（`BindCollapsibleHeader` / `BindCoverParallax` / `BindProgressiveBlur`）
- **`BindCollapsibleHeader` / `UnbindCollapsibleHeader`（点播页）**：直接绑定 `ScrollViewerManipulationPropertySet`，在 GPU 线程以 120Hz+ 零布局抖动驱动整套收起编排：
  - 顶部画册报头与展厅卡片随下滑以 `0.48x` 视差上移、景深微缩（`1.0 → 0.955`）并平滑淡出，父级容器自动附加 GPU `InsetClip` 防止越界进入标题栏；
  - 筛选栏 1:1 跟手上移并在 `Y = 0` 处齐平吸顶，磨砂玻璃底板同步渐显、底部分割线平滑淡出；
  - 任意时刻反向回滑或点击回顶均 100% 连续可打断；关闭动效时由 `UnbindCollapsibleHeader` 静默复位。
- **`BindCoverParallax`（详情页）**：环境柔光底图随页面向下滚动产生 `0.22x`（上限 `56px`）柔和视差。
- **`BindProgressiveBlur`**：支持头部透明与轻视差随滚动平滑衰减。

### 3.3 弹性形变滑行指示器（`GlideVerticalIndicator` / `GlideHorizontalPill`）
- 主侧栏与设置侧轨根据真实位移 `Δy` 驱动 `Translation.Y`，并触发瞬时纵向拉伸回弹，赋予指示条有机流光触感。
- 媒体库分段滑轨同步弹簧插值 `Translation.X` 与宽度缩放比例，在四个分区间丝滑穿行且随时可中途折返。

### 3.4 双层深度海报与触感微交互（`SetCardHover` / `SetCardPressed` / `ResetCard` / `AttachTactile` / `PulseElement`）
- **海报悬停**：卡片整体抬升并微放大，裁切框内海报图片独立微缩放营造画框景深，中央磨砂圆镜播放徽章弹簧弹出；虚拟化容器复用时通过 `ResetCard` 瞬间复位，杜绝悬停态残留。
- **通用触感**：`AttachTactile` 为高频按钮附加悬停轻抬与按压微缩（`0.962x`），`PulseElement` 为播放器空格键/主播放键提供干脆的光学阻尼脉冲（`0.89 → 1.0`）。

### 3.5 浮层、OSD 与播放器编排（`SetFloatingVisible` / `SetOverlayVisible` / `ShowOsdToast` / `SetPlayerChromeVisible`）
- **非抢焦预选浮层（`SetOverlayVisible`）**：以顶部为锚点从 `-8px`、`scale (0.975, 0.96)` 弹性展开；退场中途继续键入立即中断并无缝回弹。
- **OSD 气囊（`ShowOsdToast` / `HideOsdToast`）**：首次出现从 `-10px` 弹簧轻落；连续按键调节音量/进度/字幕延迟时仅触发 `1.045 → 1.0` 呼吸微弹，不闪烁重启。
- **播放器控制坞（`SetPlayerChromeVisible`）**：顶栏从 `-20px`、烟熏磨砂底坞从 `+26px`（`scale 0.982`）平滑沉降或弹簧浮起，交互即时接管动量。

### 3.6 呼吸光点与同心雷达波纹（`SetBeaconPulsing` / `SetRadarRingPulsing`）
- 直播 `ON AIR` 朱砂指示点与投屏状态灯采用 `2.2s` Compositor 循环呼吸（透明度 `0.38 ↔ 1.0`、缩放 `0.88 ↔ 1.08`）。
- 投屏待机舞台同心光环以 `2.8s` 周期向外扩散淡出（`0.92 → 1.55`），开始播放或离页即刻释放动画。

---

## 4. 内存与性能约束

- 所有视觉状态均通过 `ConditionalWeakTable<UIElement, MotionState>` 弱引用托管，零内存泄漏；所有 `CompositionScopedBatch` 显式释放。
- 虚拟化列表项在 `PointerExited` / `PointerCanceled` / `Unloaded` 及容器复用时调用 `ResetCard` 复位。
- `MotionService.CanAnimate == false` 时同步停止全部循环与过渡动画并强制归零 `Translation`、`Scale = 1` 与目标 `Opacity`。
