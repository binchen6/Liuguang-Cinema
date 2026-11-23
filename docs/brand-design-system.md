# 流光影院 · 品牌设计语言规范（SHIRO · MA · GLASS）

> 设计哲学参考**原研哉（Kenya Hara）的「白（Shiro）与余白（Ma）」美学**，融合**国际现代平面设计（Swiss / Editorial Graphic Design）的秩序排版**与**建筑级磨砂玻璃（Architectural Frosted Glass / 和纸透光）材质语言**。

---

## 1. 品牌精神与视觉主张

**流光影院（LIUGUANG CINEMA）**不是堆砌霓虹渐变与深色卡片的传统影音工具，而是一座克制、通透、以影像为核心的**现代美术馆式桌面放映厅**。

### 三大设计支柱

1. **余白即银幕（Emptiness as Cinema Canvas）**
   - 借鉴原研哉对「白」与「空（Emptiness）」的诠释：界面本身应当退居幕后，成为承载海报、片名、光影与字幕的器皿。
   - 减少无意义的嵌套盒与装饰性厚重卡片，以**呼吸留白（Ma）**、**1px 极细建筑分割线（Hairline）**和**克制的排版比例**建立清晰的视觉层级。

2. **现代平面秩序（Modern Graphic Editorial）**
   - 引入国际平面设计与画册编辑排版语法：非对称画廊页眉、极克制的等宽英文索引标（如 `01 / ARCHIVE`、`02 / INDEX`、`03 / BROADCAST`）、等宽数字时间码与评分（`Tabular Numerals`）、清晰的字重与字阶反差。
   - 所有主次操作通过**高对比度的墨白反差（Ink & Paper）**区分，而非依赖浮夸色彩。

3. **和纸透光与磨砂玻璃（Frosted Glass Materiality）**
   - 将日本传统和纸障子（Shoji）的柔光漫射感转译为 Windows 11 原生 `AcrylicBrush` 磨砂玻璃分层。
   - 磨砂玻璃仅用于**空间悬浮层、侧栏导轨、吸顶工具条、浮层菜单、播放器悬浮坞与焦点信息画板**，每一层均配以 `1px` 极细高光描边（Specular Hairline Edge）界定玻璃厚度，避免浑浊发灰。

---

## 2. 品牌标识与图标系统

### 2.1 品牌徽标：「光隙 · Aperture」

- **图形寓意**：正方形建筑暗室（Monolith）中划开一道纯净的几何光隙，左侧以极简光学棱镜/放映光束切出隐喻播放（Play）的负空间，右侧点缀一颗克制的**朱砂落款光点（Vermilion Optical Seal）**，象征放映机光斑、画册落款印章与「ON AIR」信号。
- **应用图标资产**：
  - 生成脚本：`scripts/New-AppIcon.ps1`
  - 高清主图：`src/TV/Assets/App.png`（512×512 PNG）
  - 多尺寸图标：`src/TV/Assets/App.ico`（内置 `16 / 20 / 24 / 32 / 40 / 48 / 64 / 128 / 256` 九档无损清晰位图，完整覆盖任务栏、Alt+Tab、标题栏与桌面大图标）。
- **应用内矢量标识**：侧栏品牌区与「设置 → 关于」使用纯 XAML 矢量几何徽标（非位图缩放），支持任意 DPI 下像素级锐利显示。

### 2.2 全站定制几何图标库（`src/TV/Styles/Icons.xaml`）

- 摒弃零散混用的旧字形图标，在 `Styles/Icons.xaml` 建立统一的 **24×24 光学网格、1.5px 统一线宽、圆润开放端点**的现代平面几何路径（`Geometry` / `PathIcon`）。
- 覆盖全部一级导航（点播、直播、媒体库、云盘、投屏、设置）、全局操作（搜索、返回、刷新、筛选、下载、收藏、更多）与播放器核心控制（播放、暂停、上一集、下一集、选集、音量、字幕、弹幕、全屏、画中画）。
- 禁止使用任何 Emoji 充当功能或站点图标。

---

## 3. 色彩与材质系统（`src/TV/Styles/Tokens.xaml`）

色彩体系完全告别旧版「深蓝灰 + 高饱和暖黄」，建立**高雅无彩色建筑基底 + 单一朱砂红落款（Vermilion Seal）**的双主题体系，并完整支持 Windows 高对比度模式。

### 3.1 暗室素墨（Default / Dark Theme — 默认）

| 语义角色 | 资源键 | 色值 / 材质 | 用途 |
| --- | --- | --- | --- |
| 暗室画布 | `AppBackgroundBrush` | `#0B0B0D` | 窗口底层画布，深邃温润的素墨黑 |
| 磨砂导轨 | `GlassRailBrush` | `AcrylicBrush` (`#121215`, Opacity `0.78`, Luminosity `0.86`) | 左侧导航栏、设置侧轨 |
| 磨砂面板 | `GlassPanelBrush` | `AcrylicBrush` (`#16161A`, Opacity `0.74`, Luminosity `0.84`) | 详情面板、吸顶筛选坞、悬浮画板 |
| 磨砂浮层 | `GlassOverlayBrush` | `AcrylicBrush` (`#1C1C22`, Opacity `0.84`, Luminosity `0.90`) | 搜索预选弹层、菜单、对话框卡片 |
| 播放器烟熏玻璃 | `PlayerGlassBrush` | `AcrylicBrush` (`#0D0D10`, Opacity `0.76`, Luminosity `0.82`) | 播放器悬浮控制坞、OSD 气泡、HUD |
| 实色表面 | `SurfaceBrush` | `#131317` | 次级平面容器、列表底面 |
| 抬升表面 | `SurfaceAltBrush` | `#1A1A20` | 胶囊底、输入框、次级标签 |
| 悬停表面 | `SurfaceHoverBrush` | `#23232B` | 指针悬停反馈层 |
| 玻璃高光描边 | `GlassEdgeBrush` | `#24FFFFFF` | 磨砂玻璃外沿 1px 光学高光线 |
| 极细分割线 | `DividerBrush` | `#16FFFFFF` | 版面区块 Hairline 分隔 |
| 主文字（宣纸白） | `PrimaryTextBrush` | `#F4F3EE` | 标题、正文与高优内容 |
| 次文字（温润灰） | `SecondaryTextBrush` | `#9E9EA6` | 副标题、说明、非激活导航 |
| 辅文字（石墨灰） | `TertiaryTextBrush` | `#6E6E78` | 索引号、占位符、弱提示 |
| 主操作填充 | `AccentFillBrush` | `#F4F3EE` | 主 CTA（墨白反差：白底黑字，极简有力） |
| 主操作文字 | `AccentInkBrush` | `#0D0D10` | 主 CTA 内部文字与图标 |
| 朱砂落款强调 | `VermilionBrush` | `#E8503E` | 品牌光点、LIVE 呼吸灯、进度光束、导航指示条、焦点评分 |
| 朱砂柔底 | `VermilionSoftBrush` | `#24E8503E` | LIVE 徽章底、选中态微弱气晕 |
| 语义成功 / 警示 | `SuccessBrush` / `DangerBrush` | `#4CC38A` / `#F06458` | 连通状态、高风险清理警示 |

### 3.2 素白画廊（Light Theme — 原研哉白）

| 语义角色 | 资源键 | 色值 / 材质 | 用途 |
| --- | --- | --- | --- |
| 宣纸画布 | `AppBackgroundBrush` | `#F6F5F0` | 温润美术馆宣纸白底 |
| 磨砂导轨 | `GlassRailBrush` | `AcrylicBrush` (`#FAF9F5`, Opacity `0.76`, Luminosity `0.88`) | 透光和纸感侧栏 |
| 磨砂面板 | `GlassPanelBrush` | `AcrylicBrush` (`#FFFFFF`, Opacity `0.72`, Luminosity `0.86`) | 悬浮展板、吸顶筛选坞 |
| 磨砂浮层 | `GlassOverlayBrush` | `AcrylicBrush` (`#FFFFFF`, Opacity `0.86`, Luminosity `0.92`) | 弹层与下拉面板 |
| 实色表面 | `SurfaceBrush` | `#FFFFFF` | 洁净纸面容器 |
| 抬升表面 | `SurfaceAltBrush` | `#EEECE5` | 浅暖石色次级底 |
| 悬停表面 | `SurfaceHoverBrush` | `#E5E2D9` | 指针悬停层 |
| 玻璃高光描边 | `GlassEdgeBrush` | `#1C000000` | 素白玻璃清晰 1px 界线 |
| 极细分割线 | `DividerBrush` | `#12000000` | 画册 Hairline 分割线 |
| 主文字（松烟墨） | `PrimaryTextBrush` | `#141416` | 深邃松烟墨主文字 |
| 次文字（青灰墨） | `SecondaryTextBrush` | `#5C5C64` | 副标题与辅助说明 |
| 辅文字（淡墨） | `TertiaryTextBrush` | `#8A8A94` | 索引号与占位符 |
| 主操作填充 | `AccentFillBrush` | `#141416` | 主 CTA（墨黑底 + 宣纸白字） |
| 主操作文字 | `AccentInkBrush` | `#FAF9F5` | 主 CTA 内部文字与图标 |
| 朱砂落款强调 | `VermilionBrush` | `#CE3A2B` | 传统印泥朱红，在素白纸面具备极高辨识度与对比度 |
| 朱砂柔底 | `VermilionSoftBrush` | `#1ACE3A2B` | 轻盈朱砂气晕底 |

### 3.3 高对比度主题（HighContrast）

- 所有语义键严格映射到 `SystemColorWindowColor`、`SystemColorWindowTextColor`、`SystemColorHighlightColor`、`SystemColorHighlightTextColor`，磨砂画刷自动降级为纯色高对比画刷，确保无障碍可读性 100% 合规。

---

## 4. 现代平面排版与字阶系统

- **字体栈**：
  - 展示/标题：`Segoe UI Variable Display, Microsoft YaHei UI, Segoe UI`
  - 正文/控件：`Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI`
  - 索引/等宽数据（时间码、索引编号、分辨率）：`Cascadia Mono, Consolas, Segoe UI Variable Text`
- **严格可读字阶**（延续 ≥13px 元数据与 ≥15px 正文的清晰可读底线）：
  - `FontSizeHero`：34 DIP / 行高 42 / SemiBold（详情大片名）
  - `FontSizePageTitle`：30 DIP / 行高 38 / SemiBold（一级页面主标题）
  - `FontSizeSection`：20 DIP / 行高 28 / SemiBold（分区标题）
  - `FontSizeCardTitle`：16 DIP / 行高 22 / SemiBold（影片/卡片标题）
  - `FontSizeBody`：15 DIP / 行高 22 / Regular（正文与常规控件）
  - `FontSizeSecondary`：14 DIP / 行高 20 / Regular（次级按钮、辅助说明）
  - `FontSizeMeta`：13 DIP / 行高 18 / Regular（年份、来源、时长、索引标）
- **画册索引眉（Editorial Eyebrow）**：在各页面主标题上方保留克制的 13 DIP 宽字距（`CharacterSpacing="140"`）编号标（如 `01 · ARCHIVE`、`02 · INDEX`），与大号中文标题构成强节奏平面视觉，窄窗时平滑收起。

---

## 5. 间距网格、圆角与控件几何

- **4 DIP 基准网格**：常用间距 `4 / 8 / 12 / 16 / 24 / 32 / 48`。
- **克制建筑圆角**：
  - 标签 / 徽章：`RadiusTag = 4`
  - 控件 / 按钮 / 输入框：`RadiusControl = 8`
  - 筛选胶囊：`RadiusChip = 18`（全圆弧）
  - 海报 / 卡片：`RadiusCard = 10`（较旧版 12 更利落挺拔，呈现画框质感）
  - 磨砂大面板 / 悬浮坞：`RadiusPanel = 14`
  - 圆形徽章 / FAB：`RadiusPill = 999`
- **命中区域与尺寸**：
  - 导航项高 `48`；主按钮高 `44`；次按钮 / 图标按钮 / 菜单项高 `40`；输入框高 `44`；列表行高 `≥48`。
  - 默认 55% 窗口下海报宽度 `≥188 DIP`，2:3 严格画幅比例。

---

## 6. 页面版式重构蓝图（告别旧版平庸堆叠）

| 页面 | 旧版局限 | 新版「白 · 隙 · 镜」重构架构 |
| --- | --- | --- |
| **应用壳与侧栏** | 实色深灰侧栏 + 黄色方块图标 + 扁平列表 | 磨砂透光导轨 + 「光隙」矢量品牌徽标 + 键帽式搜索胶囊 + 带画册编号的定制图标导航 + 弹性变形朱砂滑轨指示条 |
| **点播（VodPage）** | 普通标题行 + 暗色渐变卡片 + 平铺筛选栏 | 画册级非对称报头 + 展厅宽幅磨砂铭牌画廊 + 滚动自动切换为**悬浮磨砂玻璃筛选坞** + 画框质感海报（内置图片微缩放 + 磨砂圆镜播放徽章） |
| **详情（DetailPage）** | 封闭卡片堆叠 | 全出血环境柔光磨砂画报区（视差封面 + 垂直朱砂简介引线 + 墨白高反差主操作）+ 非对称双栏磨砂工作台（片源检视器 + 编号选集矩阵） |
| **搜索（SearchPage）** | 孤立输入框 + 传统双栏 | 一体化磨砂命令搜索条（底部内嵌细丝进度光束）+ 真磨砂非抢焦预选浮层 + 左侧磨砂站点遥测轨 + 统一画框结果流 |
| **直播（LivePage）** | 三栏等重深色卡片 | 广播控制台报头（朱砂 ON AIR 呼吸灯）+ 左磨砂源组导轨 + 中间开放式呼吸频道流 + 右侧磨砂 EPG 节目单与线路检视面板 |
| **媒体库（LibraryPage）** | 简单横向标签 + 分散列表 | 磨砂玻璃分段滑轨（弹性胶囊随切换滑行变形）+ 影院画幅继续观看卡（3px 朱砂进度丝线）+ 极细分割线个人片库索引 |
| **我的云盘（CloudDrivePage）** | 纵向割裂的账号与路径栏 | 磨砂网盘状态卡（呼吸状态点）+ 一体化磨砂路径面包屑与搜索带 + 几何图标文件表 |
| **投屏（CastPage）** | 空旷的方框舞台 | 接收端遥测控制台 + 右侧精密同心光环舞台（待机时优雅波动、连接时切换磨砂预览帧） |
| **设置（SettingsPage）** | 静态左侧栏 + 厚重卡片 | 带弹簧滑行指示条的磨砂分区导航 + 极细 Hairline 表单卡片 + 全新「光隙」品牌关于画报页 |
| **播放器（PlayerPage）** | 上下黑渐变铺底 + 粗重暖黄按钮 | 纯净暗室画面 + **悬浮式烟熏磨砂玻璃控制坞** + 朱砂进度光束 + 磨砂圆镜中央播放键 + 统一样式烟熏玻璃 OSD / HUD / AI 实时字幕浮层 |

---

## 7. 动效规范索引

所有动效物理参数、打断机制、手势与滚动联动细节见 [动效工程规范](motion-spec.md)。
