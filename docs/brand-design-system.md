# 流光影院 · 品牌设计语言规范（SHIRO · MA · OPTICAL GLASS）

> 参考**原研哉（Kenya Hara）的「白（Shiro）与余白（Ma）」美学**，融合**国际现代平面设计（Swiss / Editorial Graphic Design）的秩序排版**与**五层光学磨砂玻璃（WinUI 3 GPU `AcrylicBrush` × 菲涅尔高光描边 × 顶部焦散反光 × 立体光影透视）材质语言**。
>
> 本文是视觉令牌、光学玻璃结构与品牌资产的唯一权威来源；交互规则与 UIA 契约见 [设计契约](design-guide.md)，物理动效参数见 [动效工程规范](motion-spec.md)。

---

## 1. 品牌主张与三大支柱

**流光影院（LIUGUANG CINEMA）**是一座克制、通透、以影像为绝对主角的**现代美术馆式 Windows 原生桌面放映厅**。

1. **余白即银幕（Emptiness as Cinema Canvas）**
   界面退居幕后，成为承载海报、片名、光影与字幕的安静器皿。以呼吸留白（Ma）、`1px` 极细分割线（Hairline）与清晰字阶建立层级，克制嵌套卡片。
2. **现代平面秩序（Modern Graphic Editorial）**
   采用非对称画廊页眉、等宽画册索引标（`01`–`06`、`01 · ARCHIVE`）、等宽数字时间码与评分（`Tabular Numerals`），以**墨白高反差（Ink & Paper）**区分主次操作，仅用单一**朱砂落款红（Vermilion Seal）**承载品牌信号与焦点指引。
3. **五层光学磨砂玻璃与光影透视（Optical Frosted Glass & Light Perspective）**
   将和纸障子（Shoji）的柔光漫射与光学棱镜的折射层次结合，通过自研轻量复合控件 `Controls/GlassSurface` 与全站定向高光令牌，用 **5 层极轻 GPU 原生光影**呈现真实玻璃厚度与悬浮纵深——同时严格遵循**性能流畅优先的 GPU 模糊预算**，保证 120Hz 零掉帧。

---

## 2. 五层光学玻璃架构与 GPU 性能预算

### 2.1 五层物理光学结构（`Controls/GlassSurface`）

所有核心玻璃控件（播放器悬浮坞、顶部双浮岛、暂停圆镜、OSD / HUD / AI 字幕浮条、投屏预览坞及关键页浮层）遵循统一的五层光学模型：

| 层级 | 光学角色 | 实现机制（100% 原生 DirectComposition / XAML） | 视觉效果 |
| --- | --- | --- | --- |
| **L1 磨砂折射基底** | 背景折射与磨砂通透 | 普通页面宏观面采用校准后的 WinUI 3 GPU `AcrylicBrush`（大幅降低 `TintLuminosityOpacity`）；播放器叠于 LibVLC `SwapChainPanel` 之上，因 WinUI 3 `CompositionBackdropBrush` 无法采样硬件 DXGI SwapChain 会输出不透明黑块，故采用半透明烟熏晶透渐变基底 | 普通页面呈现真 GPU 高斯模糊漫射；播放器 100% 透光不发黑、零回读，视频色彩与光斑直接透射 |
| **L2 体积光影与乳白漫射** | 顶左天光 + 磨砂柔散 + 底部厚度 | 对角线体积渐变 `LinearGradientBrush`（顶左柔和乳白磨砂漫射 → 中部晶透 → 底部微沉厚度） | 赋予玻璃真实的物理体积与顶左主光方向，消除平面塑料感并保护高亮画面图标可读性 |
| **L3 酸蚀微晶磨砂粒** | 真实磨砂表面肌理 | `GlassSurface` 静态懒加载共享的 1:1 程序化微晶磨砂纹理（仅播放器 `SwapChainPanel` 启用，一次性生成后零 CPU 开销） | 打破纯平滑半透明的“塑料膜感”，还原真实细砂玻璃漫射质感 |
| **L4 双层菲涅尔倒角框** | 视角边缘全反射 + 玻璃厚度 | 外 `1px` 四段菲涅尔高光框 + 内 `1px` 玻璃厚度倒角边（顶内折射微亮 + 底内微暗） | 精准界定 2–3mm 实心光学玻璃倒角厚度与空间透视 |
| **L5 焦散高光与空间投影** | 折面晶光 + 悬浮透视 | 顶部 `1px` 峰值居中焦散高光丝 + 底部折返光唇；悬浮宏观层施加克制 `ThemeShadow`（`Z = 10–16 DIP`） | 一眼呈现晶莹立体悬浮感，不向画面投射脏重黑晕 |

### 2.2 性能流畅优先原则（GPU 模糊预算红线）

> **性能流畅为首要目标**：WinUI 3 的 `AcrylicBrush` 每次呈现都会触发一次 GPU 背景采样与高斯模糊。全站严格区分**宏观建筑磨砂面**与**高频虚拟化微元素**，在视觉升维的同时反而大幅降低滚动期 GPU 负载：

1. **宏观建筑磨砂面（允许使用真实 `AcrylicBrush`）**：
   - 每屏同时可见的真实 `AcrylicBrush` 宏观层严格控制在 **≤ 4 块**（主侧栏、吸顶筛选坞、命令搜索条/建议浮层、播放器悬浮控制层、检视工作台）。
   - 播放器控制层、OSD、HUD、暂停圆镜与搜索建议浮层在离场动画结束时立即通过令牌守卫 `CompositionScopedBatch` 设为 `Visibility.Collapsed`，实现**静默播放期间 0.0% 模糊开销**。
2. **虚拟化微元素禁用 `AcrylicBrush`（零额外模糊 Pass）**：
   - 海报角标（`GlassDarkBadgeBrush`）、虚拟化列表行图标底托（`SurfaceChipBrush`）、侧栏与列表选中底板（`GlassActivePlateBrush`）严禁使用 `AcrylicBrush`——统一采用零模糊的**透明晶体渐变 + 菲涅尔细边**。此举在点播/搜索页滚动时直接消除 **30–40 个并发背景模糊 Pass**，显著提升滚动帧率与低配核显流畅度。
3. **禁止指针移动期修改 XAML Brush**：
   - 严禁在 `PointerMoved` 中实时重算或写入 `RadialGradientBrush`；所有悬停提亮与微抬升仅通过 `VisualStateManager` 与 GPU Compositor `Scale / Translation / Opacity` 弹簧完成，保持 **0 UI 线程重绘**。
4. **优雅降级**：
   - `FallbackColor` 统一校准为高透光半透明晶色，窗口失焦或低配/RDP 降级时仍保持克制通透，不突兀跳变为实心黑块；高对比度模式自动退化为系统语义色。

---

## 3. 品牌标识与图标系统

### 3.1 品牌徽标：「光隙 · Aperture」

- **图形语义**：素墨暗室单体（Monolith）中以极细透光折面划开纯净光隙，竖柱与几何播放棱镜构成负空间隐喻，右上角点缀一颗**朱砂光学落款点**，象征放映机光斑、画册印章与 `ON AIR` 信号。
- **资产与组件**：
  - 生成脚本：`scripts/New-AppIcon.ps1`
  - 应用图标：`src/TV/Assets/App.png`（512×512）、`src/TV/Assets/App.ico`（含 `16 / 20 / 24 / 32 / 40 / 48 / 64 / 128 / 256` 九档无损位图，随程序目录分发并由 `AppWindow.SetIcon` 显式绑定）
  - 应用内矢量徽标：`src/TV/Controls/BrandMark.xaml`（纯 XAML 矢量，任意 DPI 像素级锐利）

### 3.2 定制建筑几何图标库（`Styles/Icons.xaml` + `Controls/LineIcon.cs`）

- 统一 **24×24 光学网格、1.65px 默认线宽、圆润开放端点**（`PenLineCap.Round` / `PenLineJoin.Round`）。
- 覆盖全部一级导航、全局操作、设置分区、云盘文件与播放器全部控制按钮（含实心播放/暂停、几何音量/静音、字幕、弹幕、投屏、全屏），彻底消除不同字体图标线宽割裂；`LineIcon` 按实例独立创建 `Path`，安全适配虚拟化模板。
- 禁止使用 Emoji 充当界面功能图标。

---

## 4. 色彩与光学材质令牌（同步自 `Styles/Tokens.xaml` 与 `App.xaml`）

### 4.1 播放器专属光学磨砂玻璃令牌（`App.xaml`）

| 语义角色 | 资源键 | 参数 / 结构 | 典型用途 |
| --- | --- | --- | --- |
| 主控制坞晶透基底 | `PlayerGlassBrush` | 对角线半透明烟熏晶色 (`#4C1B2130 → #5E131722 → #720B0E16`) + `FrostGrainOpacity="0.085"` | 底部悬浮控制坞、顶部左右分体浮岛、投屏预览坞（安全叠于 `SwapChainPanel`） |
| 悬浮胶囊/圆镜基底 | `PlayerGlassPillBrush` | 对角线半透明烟熏晶色 (`#5E1C2232 → #70131722 → #840B0E16`) + `FrostGrainOpacity="0.10"` | 暂停中央圆镜、OSD 气囊、HUD、AI 字幕条、状态卡 |
| 菜单与设置浮层基底 | `PlayerGlassFlyoutBrush` | 清晰半透明烟熏晶色 (`#D6161B28 → #E40E111A`) | 播放选项、倍速、画质、音轨、字幕与弹幕弹层（保证长列表清晰可读且不发黑） |
| 体积厚度与乳白漫射层 | `PlayerGlassVolumeBrush` | 顶左乳白磨砂光晕 → 底右微沉 (`#30F4F8FF → #16E4ECFF → #0AE0E8FF → #2C04060D`) | `GlassSurface` L2 体积透视与底部图标对比度托底 |
| 内倒角厚度边 | `PlayerGlassInnerBevelBrush` | 顶亮底暗双折射边 (`#2EFFFFFF → #0AFFFFFF → #30000000`) | `GlassSurface` L4b 实心玻璃厚度倒角 |
| 顶部焦散高光丝 | `PlayerGlassCausticBrush` | 水平峰值渐变 (`#00FFFFFF → #96FFFFFF → #58FFFFFF → #00FFFFFF`) | `GlassSurface` L5a 顶部 1px 折面晶光丝 |
| 菲涅尔定向外框 | `PlayerGlassBorderBrush` | 垂直四段定向高光 (`#8CFFFFFF → #38FFFFFF → #1CFFFFFF → #4CFFFFFF`) | `GlassSurface` L4a 玻璃外沿高光勾边 |
| 按钮玻璃交互态 | `PlayerGlassHoverBrush` / `Pressed` / `RimHover` | `#2CFFFFFF` / `#42FFFFFF` / `#62FFFFFF` | 播放器圆形按钮与下拉胶囊悬停/按压折射反馈 |

### 4.2 暗室素墨（Dark / Default — 默认主题）

| 语义角色 | 资源键 | 色值 / 材质 | 典型用途 |
| --- | --- | --- | --- |
| 暗室画布 | `AppBackgroundBrush` | `#0B0B0D` | 窗口底层画布（配合 `LiquidBackdrop` 极轻美术馆环境光场） |
| 磨砂导轨 | `GlassRailBrush` | `AcrylicBrush` (`#12141B`, Opacity `0.60`, Luminosity `0.74`, Fallback `#EB12141B`) | 主侧栏、设置侧轨 |
| 磨砂面板 | `GlassPanelBrush` | `AcrylicBrush` (`#161820`, Opacity `0.56`, Luminosity `0.70`, Fallback `#E615171F`) | 详情工作台、吸顶筛选坞、展陈铭牌、直播左右栏、投屏控制台 |
| 磨砂浮层 | `GlassOverlayBrush` | `AcrylicBrush` (`#1B1E28`, Opacity `0.74`, Luminosity `0.82`, Fallback `#F2181B24`) | 搜索预选浮层、悬浮回顶圆镜、菜单、对话框 |
| 轻量晶体微件（零模糊） | `GlassChipBrush` / `GlassDarkBadgeBrush` / `GlassActivePlateBrush` | 定向透明晶体渐变（0 背景模糊 Pass） | 筛选胶囊、列表行图标托、海报角标、导航/分段选中底板 |
| 菲涅尔定向高光描边 | `GlassEdgeBrush` | 定向渐变 (`#38FFFFFF → #1AFFFFFF → #0EFFFFFF → #20FFFFFF`) | 全站玻璃面板 `1px` 立体受光外框 |
| 顶部内高光晕 | `GlassTopSheenBrush` | 垂直柔光 (`#16FFFFFF → #04FFFFFF → #00FFFFFF`) | 玻璃面板与命令栏上缘内反射光 |
| 实色 / 抬升 / 悬停表面 | `SurfaceBrush` / `SurfaceAltBrush` / `SurfaceHoverBrush` | `#13141A` / `#1A1C24` / `#232631` | 输入框、次级容器、悬停反馈 |
| 主 / 次 / 辅文字 | `PrimaryTextBrush` / `SecondaryTextBrush` / `TertiaryTextBrush` | `#F4F3EE` / `#A2A3AE` / `#727380` | 宣纸白主字、温润灰副字、石墨灰索引 |
| 墨白主操作 | `AccentFillBrush` / `AccentInkBrush` | `#F4F3EE` / `#0D0D10` | 主 CTA（宣纸白底 + 素墨黑字） |
| 朱砂落款强调 | `VermilionBrush` / `VermilionSoftBrush` | `#E8503E` / `#22E8503E` | 品牌光点、滑轨指示条、进度光束、LIVE 呼吸灯 |
| 语义成功 / 警示 | `SuccessBrush` / `DangerBrush` | `#4CC38A` / `#F26B5E` | 连通状态、高风险清理警示 |

### 4.3 素白画廊（Light — 原研哉白）

| 语义角色 | 资源键 | 色值 / 材质 | 典型用途 |
| --- | --- | --- | --- |
| 宣纸画布 | `AppBackgroundBrush` | `#F6F5F0` | 温润美术馆宣纸底 |
| 磨砂导轨 / 面板 / 浮层 | `GlassRailBrush` / `GlassPanelBrush` / `GlassOverlayBrush` | `#FAF9F5` (`0.64/0.78`) / `#FFFFFF` (`0.58/0.74`) / `#FFFFFF` (`0.78/0.86`) | 和纸透光侧栏、展板与浮层 |
| 菲涅尔定向描边 | `GlassEdgeBrush` | 定向渐变 (`#E0FFFFFF → #14000000 → #22000000`) | 上缘纯白纸光倒角 + 下缘微墨影界线，素白主题立体通透 |
| 顶部内高光晕 | `GlassTopSheenBrush` | 垂直柔光 (`#72FFFFFF → #1CFFFFFF → #00FFFFFF`) | 和纸玻璃上缘明亮折光 |
| 实色 / 抬升 / 悬停表面 | `SurfaceBrush` / `SurfaceAltBrush` / `SurfaceHoverBrush` | `#FFFFFF` / `#EFECE4` / `#E6E3DA` | 洁净纸面与浅暖石次级底 |
| 主 / 次 / 辅文字 | `PrimaryTextBrush` / `SecondaryTextBrush` / `TertiaryTextBrush` | `#141416` / `#595962` / `#84848F` | 松烟墨、青灰墨、淡墨 |
| 墨白主操作 | `AccentFillBrush` / `AccentInkBrush` | `#141416` / `#FAF9F5` | 主 CTA（松烟墨底 + 宣纸白字） |
| 印泥朱砂强调 | `VermilionBrush` / `VermilionSoftBrush` | `#CE3A2B` / `#1ACE3A2B` | 传统印泥朱红，素白纸面高对比辨识 |

### 4.4 高对比度（HighContrast）

全部画刷与磨砂资源均映射至 `SystemColorWindowColor`、`SystemColorWindowTextColor`、`SystemColorHighlightColor` 等系统语义色，自动退化为纯色高对比表现并隐藏装饰性背景光晕与焦散反光。

---

## 5. 现代平面排版与几何规范

- **字体栈**：
  - 展示/标题：`Segoe UI Variable Display, Microsoft YaHei UI, Segoe UI`
  - 正文/控件：`Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI`
  - 等宽数据（时间码、索引编号、分辨率）：`Cascadia Mono, Consolas, Segoe UI Variable Text`
- **可读字阶**（常规文本 `≥13 DIP`，辅助微标/键帽严格 `≥12 DIP`）：
  - `FontSizeHero` 34/42 · `FontSizePageTitle` 30/38 · `FontSizeSection` 20/28 · `FontSizeCardTitle` 16/22 · `FontSizeBody` 15/22 · `FontSizeSecondary` 14/20 · `FontSizeMeta` 13/18
- **画册索引眉（`EyebrowText`）**：`MonoFontFamily` 13 DIP，`CharacterSpacing="120"`，窄窗平滑收起。
- **4 DIP 网格与克制圆角**：
  - 常用间距：`4 / 8 / 12 / 16 / 24 / 32 / 48`；正文最大可读宽度 `ReadableMaxWidth = 840`、`TextParagraphMaxWidth = 800`。
  - 圆角：`RadiusTag = 4` · `RadiusControl = 8` · `RadiusCard = 10` · `RadiusPanel = 14` · `RadiusChip = 18` · `RadiusPill = 999`。
  - 命中高度：导航项 `48` · 主按钮 / 输入框 `44` · 次按钮 / 图标按钮 / 菜单项 `40` · 列表行 `≥48`；海报默认 `188×282`（2:3 画幅）。

---

## 6. 全站页面视觉构图矩阵

| 页面 | 视觉构图与光学材质运用 |
| --- | --- |
| **应用壳与侧栏** | `LiquidBackdrop` 极轻美术馆环境光场 + `GlassRailBrush` 定向受光磨砂导轨 + 「光隙」矢量徽标 + `Ctrl F` 键帽搜索按钮 + `01`–`05` 索引导航与弹性朱砂滑轨 |
| **点播（VodPage）** | 非对称画册报头 + 宽幅光学磨砂展厅铭牌画廊 + 滚动 1:1 跟手收起的光学磨砂吸顶筛选坞（带顶部焦散光丝）+ 2:3 画框海报网格（零模糊高透角标 + 双层景深微缩放 + 悬浮圆镜）+ 光学玻璃回顶圆镜 |
| **详情（DetailPage）** | 全出血柔光画报（GPU 视差底纹 + 朱砂垂直简介引线 + 墨白主操作）+ 非对称光学磨砂双栏工作台（片源线路检视与带触感回弹的选集矩阵） |
| **搜索（SearchPage）** | 一体化光学磨砂命令搜索条（顶部焦散光丝 + 底部 2px 朱砂进度光束）+ 非抢焦光学磨砂预选浮层 + 244px 左侧磨砂站点遥测轨 + 画框结果流 |
| **直播（LivePage）** | 广播控制台报头（朱砂 `ON AIR` 呼吸灯）+ 左光学磨砂源组导轨 + 中间开放频道流 + 右侧光学磨砂 EPG 节目单与多线路检视面板 |
| **媒体库（LibraryPage）** | 光学磨砂分段滑轨（顶部受光高光丝 + 弹性晶体胶囊水平滑行）+ 宽幅影院继续观看卡（3px 朱砂进度丝线）+ Hairline 个人片库与本地索引列表 |
| **我的云盘（CloudDrivePage）** | 轻透四网盘账号卡（绿/红/灰连接状态点与定向高光外框）+ 一体化光学磨砂路径面包屑与搜索带 + 24×24 建筑线框文件表 |
| **投屏（CastPage）** | 左侧光学磨砂接收端遥测控制台 + 右侧精密同心光学波纹待机舞台（连接后切换 `GlassSurface` 光学磨砂预览坞） |
| **设置（SettingsPage）** | 8 分区光学磨砂弹簧滑轨侧栏 + 极细 Hairline 表单卡片（内容区最大宽度约束）+ 「光隙」品牌关于画报与软件更新区 |
| **播放器（PlayerPage）** | 纯粹暗室画面 + 全部基于 `GlassSurface` 五层光学磨砂玻璃重构：顶部双分体轻盈浮岛（敞开上方视野）+ 修身底部悬浮控制坞（珠光朱砂精工进度滑块 + 全矢量统一线宽图标）+ 76px 光学圆镜中央播放键 + 统一五层光学 OSD / HUD / AI 实时字幕浮层；无操作 3 秒且指针不在控件上时连同鼠标同步隐藏，隐藏后零模糊 GPU 占用 |
