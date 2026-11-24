# 流光影院 · 品牌设计语言规范（SHIRO · MA · GLASS）

> 参考**原研哉（Kenya Hara）的「白（Shiro）与余白（Ma）」美学**，融合**国际现代平面设计（Swiss / Editorial Graphic Design）的秩序排版**与**建筑级磨砂玻璃（WinUI `AcrylicBrush` + `1px` 高光描边）材质语言**。
>
> 本文是视觉令牌与品牌资产的唯一权威来源；交互规则与 UIA 契约见 [设计契约](design-guide.md)，物理动效参数见 [动效工程规范](motion-spec.md)。

---

## 1. 品牌主张与三大支柱

**流光影院（LIUGUANG CINEMA）**是一座克制、通透、以影像为绝对主角的**现代美术馆式 Windows 原生桌面放映厅**。

1. **余白即银幕（Emptiness as Cinema Canvas）**
   界面退居幕后，成为承载海报、片名、光影与字幕的安静器皿。以呼吸留白（Ma）、`1px` 极细分割线（Hairline）与清晰字阶建立层级，克制嵌套卡片。
2. **现代平面秩序（Modern Graphic Editorial）**
   采用非对称画廊页眉、等宽画册索引标（`01`–`06`、`01 · ARCHIVE`）、等宽数字时间码与评分（`Tabular Numerals`），以**墨白高反差（Ink & Paper）**区分主次操作，仅用单一**朱砂落款红（Vermilion Seal）**承载品牌信号与焦点指引。
3. **和纸透光与磨砂玻璃（Architectural Frosted Glass）**
   将和纸障子（Shoji）的柔光漫射转译为 WinUI 3 `AcrylicBrush` 分层，仅应用于侧栏导轨、吸顶筛选坞、浮层面板、检视工作台与播放器悬浮坞；每层配以 `1px` `GlassEdgeBrush` 高光描边界定玻璃厚度，低配/RDP 环境自动退化至 `FallbackColor`。

---

## 2. 品牌标识与图标系统

### 2.1 品牌徽标：「光隙 · Aperture」

- **图形语义**：素墨暗室单体（Monolith）中以极细透光折面划开纯净光隙，竖柱与几何播放棱镜构成负空间隐喻，右上角点缀一颗**朱砂光学落款点**，象征放映机光斑、画册印章与 `ON AIR` 信号。
- **资产与组件**：
  - 生成脚本：`scripts/New-AppIcon.ps1`
  - 应用图标：`src/TV/Assets/App.png`（512×512）、`src/TV/Assets/App.ico`（含 `16 / 20 / 24 / 32 / 40 / 48 / 64 / 128 / 256` 九档无损位图，随程序目录分发并由 `AppWindow.SetIcon` 显式绑定）
  - 应用内矢量徽标：`src/TV/Controls/BrandMark.xaml`（纯 XAML 矢量，任意 DPI 像素级锐利）

### 2.2 定制建筑几何图标库（`Styles/Icons.xaml` + `Controls/LineIcon.cs`）

- 统一 **24×24 光学网格、1.65px 默认线宽、圆润开放端点**（`PenLineCap.Round` / `PenLineJoin.Round`）。
- 覆盖全部一级导航、全局操作、设置分区、云盘文件与播放器控制；`LineIcon` 按实例独立创建 `Path`，安全适配虚拟化模板并自动响应主题与状态变色。
- 禁止使用 Emoji 充当界面功能图标。

---

## 3. 色彩与材质令牌（同步自 `Styles/Tokens.xaml` 与 `App.xaml`）

### 3.1 暗室素墨（Dark / Default — 默认主题）

| 语义角色 | 资源键 | 色值 / 材质 | 典型用途 |
| --- | --- | --- | --- |
| 暗室画布 | `AppBackgroundBrush` | `#0B0B0D` | 窗口底层画布 |
| 磨砂导轨 | `GlassRailBrush` | `AcrylicBrush` (`#131317`, Opacity `0.76`, Luminosity `0.86`) | 主侧栏、设置侧轨 |
| 磨砂面板 | `GlassPanelBrush` | `AcrylicBrush` (`#17171C`, Opacity `0.72`, Luminosity `0.84`) | 详情工作台、吸顶筛选坞、展陈铭牌 |
| 磨砂浮层 | `GlassOverlayBrush` | `AcrylicBrush` (`#1D1D24`, Opacity `0.82`, Luminosity `0.90`) | 搜索预选浮层、菜单、对话框 |
| 磨砂胶囊 / 暗徽章 | `GlassChipBrush` / `GlassDarkBadgeBrush` | `#1A1A20` (`0.65`) / `#0C0C0F` (`0.72`) | 筛选胶囊、海报角标 |
| 播放器烟熏玻璃 | `PlayerGlassBrush` / `PlayerGlassPillBrush` (`App.xaml`) | `#0D0D10` (`0.76`) / `#121216` (`0.80`) | 播放器控制坞、OSD 气泡、HUD、AI 字幕浮条 |
| 实色 / 抬升 / 悬停表面 | `SurfaceBrush` / `SurfaceAltBrush` / `SurfaceHoverBrush` | `#131317` / `#1A1A20` / `#23232B` | 列表容器、输入框、悬停反馈 |
| 高光描边 / 分割线 | `GlassEdgeBrush` / `DividerBrush` | `#24FFFFFF` / `#15FFFFFF` | 磨砂 1px 外沿高光、版面 Hairline 分隔 |
| 主 / 次 / 辅文字 | `PrimaryTextBrush` / `SecondaryTextBrush` / `TertiaryTextBrush` | `#F4F3EE` / `#9E9EA8` / `#6F6F7A` | 宣纸白主字、温润灰副字、石墨灰索引 |
| 墨白主操作 | `AccentFillBrush` / `AccentInkBrush` | `#F4F3EE` / `#0D0D10` | 主 CTA（宣纸白底 + 素墨黑字） |
| 朱砂落款强调 | `VermilionBrush` / `VermilionSoftBrush` | `#E8503E` / `#22E8503E` | 品牌光点、滑轨指示条、进度光束、LIVE 呼吸灯 |
| 语义成功 / 警示 | `SuccessBrush` / `DangerBrush` | `#4CC38A` / `#F26B5E` | 连通状态、高风险清理警示 |

### 3.2 素白画廊（Light — 原研哉白）

| 语义角色 | 资源键 | 色值 / 材质 | 典型用途 |
| --- | --- | --- | --- |
| 宣纸画布 | `AppBackgroundBrush` | `#F6F5F0` | 温润美术馆宣纸底 |
| 磨砂导轨 / 面板 / 浮层 | `GlassRailBrush` / `GlassPanelBrush` / `GlassOverlayBrush` | `#FAF9F5` (`0.74`) / `#FFFFFF` (`0.70`) / `#FFFFFF` (`0.86`) | 和纸透光侧栏、展板与浮层 |
| 实色 / 抬升 / 悬停表面 | `SurfaceBrush` / `SurfaceAltBrush` / `SurfaceHoverBrush` | `#FFFFFF` / `#EFECE4` / `#E6E3DA` | 洁净纸面与浅暖石次级底 |
| 高光描边 / 分割线 | `GlassEdgeBrush` / `DividerBrush` | `#1C000000` / `#14000000` | 素白玻璃 1px 界线与 Hairline 分割线 |
| 主 / 次 / 辅文字 | `PrimaryTextBrush` / `SecondaryTextBrush` / `TertiaryTextBrush` | `#141416` / `#595962` / `#84848F` | 松烟墨、青灰墨、淡墨 |
| 墨白主操作 | `AccentFillBrush` / `AccentInkBrush` | `#141416` / `#FAF9F5` | 主 CTA（松烟墨底 + 宣纸白字） |
| 印泥朱砂强调 | `VermilionBrush` / `VermilionSoftBrush` | `#CE3A2B` / `#1ACE3A2B` | 传统印泥朱红，素白纸面高对比辨识 |
| 语义成功 / 警示 | `SuccessBrush` / `DangerBrush` | `#1B7A50` / `#B82A1E` | 浅色高对比语义状态 |

### 3.3 高对比度（HighContrast）

全部画刷与磨砂资源均映射至 `SystemColorWindowColor`、`SystemColorWindowTextColor`、`SystemColorHighlightColor` 等系统语义色，自动退化为纯色高对比表现并隐藏装饰性背景光晕。

---

## 4. 现代平面排版与几何规范

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

## 5. 全站页面视觉构图矩阵

| 页面 | 视觉构图与材质运用 |
| --- | --- |
| **应用壳与侧栏** | `LiquidBackdrop` 极轻大气底纹 + `GlassRailBrush` 磨砂导轨 + 「光隙」矢量徽标 + `Ctrl F` 键帽搜索按钮 + `01`–`05` 索引导航与弹性朱砂滑轨 |
| **点播（VodPage）** | 非对称画册报头 + 宽幅磨砂展厅铭牌画廊 + 滚动 1:1 跟手收起的吸顶磨砂筛选坞 + 2:3 画框海报网格（悬停双层景深微缩放 + 磨砂圆镜播放徽章）+ 悬浮回顶圆镜 |
| **详情（DetailPage）** | 全出血柔光磨砂画报（GPU 视差底纹 + 朱砂垂直简介引线 + 墨白主操作）+ 非对称磨砂双栏工作台（片源线路检视与编号选集矩阵） |
| **搜索（SearchPage）** | 一体化磨砂命令搜索条（底部 2px 朱砂进度光束）+ 非抢焦真磨砂预选浮层 + 244px 左侧磨砂站点遥测轨 + 画框结果流 |
| **直播（LivePage）** | 广播控制台报头（朱砂 `ON AIR` 呼吸灯）+ 左磨砂源组导轨 + 中间开放频道流 + 右侧磨砂 EPG 节目单与多线路检视面板 |
| **媒体库（LibraryPage）** | 磨砂分段滑轨（弹性玻璃胶囊水平滑行）+ 宽幅影院继续观看卡（3px 朱砂进度丝线）+ Hairline 个人片库与本地索引列表 |
| **我的云盘（CloudDrivePage）** | 磨砂四网盘账号卡（绿/红/灰连接状态点与 1px 高光描边）+ 一体化路径面包屑与搜索带 + 24×24 建筑线框文件表 |
| **投屏（CastPage）** | 左侧磨砂接收端遥测控制台 + 右侧精密同心光学波纹待机舞台（连接后切换磨砂预览帧） |
| **设置（SettingsPage）** | 8 分区弹簧滑轨侧栏 + 极细 Hairline 表单卡片（内容区最大宽度约束）+ 「光隙」品牌关于画报与软件更新区 |
| **播放器（PlayerPage）** | 纯粹暗室画面 + 悬浮烟熏磨砂玻璃底部控制坞 + 朱砂进度光束 + 84px 磨砂圆镜中央播放键 + 统一样式烟熏磨砂 OSD / HUD / AI 实时字幕浮层 |
