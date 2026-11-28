# 流光影院 · 视觉与材质规范

> 本文定义「流光影院」当前实现的色彩令牌、五层玻璃表面（`Controls/GlassSurface`）、专属 GPU 玻璃光影引擎（`Controls/GlassOpticsEngine`）、图标体系与统一导航样式；交互边界见 [设计契约](design-guide.md)，动效参数见 [动效工程规范](motion-spec.md)。

---

## 1. 设计原则

- **内容优先与低干扰层级**：以留白、`1px` 细描边与明确字阶界定结构，克制多层实心盒子嵌套。
- **高反差主操作与单一强调色**：主 CTA 与选中分类使用高反差黑白/墨白填充（`AccentFillBrush` / `AccentInkBrush`），朱砂红（`VermilionBrush`）仅用于焦点指示条、播放进度与在线状态点。
- **真实物理光学 + 受控 GPU 预算**：宏观磨砂层使用 WinUI 3 `AcrylicBrush`（每屏 `≤ 4` 块），指针光影与折射由 DWM Compositor 线程的 `GlassOpticsEngine` 驱动，静止时零动画开销。

---

## 2. 光学玻璃体系（`GlassSurface` + `GlassOpticsEngine`）

### 2.1 `GlassSurface` 五层静态结构与双主题材质分化

主侧栏、点播吸顶筛选坞、播放器悬浮控制坞/浮岛、暂停圆镜、OSD/HUD 及卡片表面由 `Controls/GlassSurface` 组合：

- **深色主题（月光 + 透光玻璃）**：低雾度高通透月光基底、零磨砂颗粒、冷银菲涅尔外框与锐利透镜聚焦；
- **浅色主题（日光 + 磨砂玻璃）**：温润宣纸底布叠加暖白日光柔雾基底，自动启用细微酸蚀微晶磨砂纹理（`FrostGrainOpacity = 0.055`）与更宽广柔和的体积漫射；
- **播放控件（`PlayerGlass*` / `Cinema`）**：保持独立清透影院玻璃材质与光学参数，不参与深浅主题切换。

| 层级 | 作用 | 实现方式 |
| --- | --- | --- |
| **L1 基底** | 半透明磨砂底座 | 常规页面使用 `AcrylicBrush`；播放器位于 LibVLC `SwapChainPanel` 之上（规避 WinUI `CompositionBackdropBrush` 无法采样独立 DXGI SwapChain 的限制），采用标定后的半透明深色光学渐变基底 |
| **L2 体积渐变** | 顶左天光与底缘压暗 | 对角线 `LinearGradientBrush`（`VolumeBrush`），确保浅色画面上白色文字与图标清晰可读 |
| **L3 微晶纹理** | 酸蚀细磨砂质感 | 懒加载共享 1:1 程序化纹理位图（按需启用，一次性生成后全局复用） |
| **L4 双层倒角边** | 玻璃厚度边界 | 外 `1px` 定向菲涅尔外框 + 内 `1px` 上亮下暗倒角描边 |
| **L5 顶部焦散丝** | 上下棱面微反光 | 顶部 `1px` 峰值渐变高光线 + 底部弱反光边；非 `SwapChainPanel` 悬浮容器可按需叠加 `ThemeShadow`，播放器内严禁使用 `ThemeShadow` |

### 2.2 专属玻璃光学光影引擎（`Controls/GlassOpticsEngine`）

`GlassOpticsEngine` 通过 `ElementCompositionPreview.SetElementChildVisual` 直接在 DWM Compositor 线程挂载轻量化 `ShapeVisual` 树，为全局玻璃控件（`GlassSurface`、`NavPane`、播放器控制坞/浮钮、次按钮、图标按钮、信息卡片、设置卡片与媒体库历史卡片）提供真实物理光学响应：

| 光学层 | 物理原理 | Compositor 实现 |
| --- | --- | --- |
| **1. 体积透射与 Snell 视差** | 光线穿过具厚度的折射介质（$n \approx 1.5$）发生侧向折射偏移 | 复用常驻 `CompositionRadialGradientBrush`，中心跟随指针的同时对 `GradientOriginOffset` 施加反向微位移，模拟透视景深 |
| **2. Schlick Fresnel 倒角色散** | 掠射角趋近边缘时玻璃反射率骤升并伴随微弱色散 | `1.3–1.45px` 圆角矩形描边由径向光刷驱动，带冷冰蓝/暖焦散过渡，指针靠边时自动提亮 |
| **3. 顶部切面全内反射游走光** | 光源沿玻璃顶棱产生全内反射高光带 | `1.5px` 顶部高亮微带追踪指针 X 坐标平滑滑行 |
| **4. 点击焦散折射波纹** | 按压瞬间触发径向折射光环 | `PointerPressed`（`handledEventsToo: true`）触发 GPU 半径与不透明度扩散动画后自然消散 |

- **模式与档位**：通过附加属性或 `GlassSurface` 属性配置 `GlassOpticsMode`（`Off` / `Interactive` / `Ambient`）与 `GlassOpticsProfile`（`Standard` / `Cinema` / `Rail` / `Card` / `Control`），按深色、浅色与暗室影院自动匹配校准色板。
- **零空闲开销与安全边界**：
  1. **120Hz 指针节流**：`PointerMoved` 采样间隔 `≥ 8ms`，仅更新常驻 GPU 画刷的 `EllipseCenter` / `GradientOriginOffset`，绝不重建画刷或触发 XAML 布局。
  2. **静止 0% GPU 消耗**：无常驻循环计时器，指针移出且点击波纹结束后所有光学层透明度归零、动画停止。
  3. **全环境合规**：`SwapChainPanel` 安全（零 `ThemeShadow`、零新增 `AcrylicBrush`）；`HighContrast` 模式或关闭动效（`MotionService.CanAnimate == false`）时自动停用并清空视觉层。

### 2.3 GPU 模糊预算约定

1. **宏观 `AcrylicBrush` 上限**：单屏同时可见的真实 `AcrylicBrush` 宏观层 `≤ 4` 块（侧栏、吸顶筛选坞、命令搜索条/建议浮层、检视面板）。
2. **退场即折叠**：播放器控制层、OSD、HUD、暂停按钮与搜索建议浮层退场后设为 `Visibility.Collapsed`。
3. **虚拟化微件禁模糊**：海报角标、胶囊、列表选中底板与设置卡片采用无背景模糊的渐变/半透明实色，按需叠加轻量 `GlassOpticsEngine` 光学层。
4. **失焦与降级**：所有 `AcrylicBrush` 均配置 `FallbackColor`，失焦或系统关闭透明效果时平顺退化。

---

## 3. 品牌徽标与线性图标库

- **应用图标**：`scripts/New-AppIcon.ps1` 生成 512×512 `App.png` 与九档位图 `App.ico`（`16–256`）；应用内徽标为 `Controls/BrandMark.xaml` 矢量实现。
- **几何线性图标（`Styles/Icons.xaml` + `Controls/LineIcon.cs`）**：基于 `24×24` 网格、`1.65px` 线宽、圆润端点；每实例独立 `Path` 以兼容虚拟化复用。界面功能图标禁用 Emoji。

---

## 4. 色彩与材质令牌（同步自 `Styles/Tokens.xaml` 与 `App.xaml`）

### 4.1 播放器光学玻璃令牌（`App.xaml`）

| 语义角色 | 资源键 | 参数 / 色值 | 主要用途 |
| --- | --- | --- | --- |
| 主控制坞基底 | `PlayerGlassBrush` | `#30202937 → #40151A25 → #620B0E16`，清透无颗粒 | 底部悬浮控制坞、顶部双浮岛、投屏预览坞 |
| 悬浮胶囊/圆镜 | `PlayerGlassPillBrush` | `#40272F3C → #48131722 → #6D0B0E16`，清透无颗粒 | 暂停按钮、OSD、HUD、字幕条、状态卡 |
| 菜单与弹层 | `PlayerGlassFlyoutBrush` | `#D6161B28 → #E40E111A` | 倍速、画质、音轨、字幕与更多选项弹层 |
| 体积漫射 / 内倒角 | `PlayerGlassVolumeBrush` / `InnerBevelBrush` | `#34F4F8FF → #2C04060D` / `#44FFFFFF → #30000000` | L2 体积光影、L4 内 `1px` 倒角边 |
| 焦散丝 / 菲涅尔框 | `PlayerGlassCausticBrush` / `BorderBrush` | 顶部高光丝 / `#9CFFFFFF → #57FFFFFF` | L5 顶部高光与 L4 外描边 |

### 4.2 深色主题（Dark / 默认）

| 语义角色 | 资源键 | 色值 / 参数 | 主要用途 |
| --- | --- | --- | --- |
| 窗口底布 | `AppBackgroundBrush` | `#0B0B0D` | 底层画布（配 `LiquidBackdrop` 环境底纹） |
| 磨砂侧轨 / 面板 / 浮层 | `GlassRailBrush` / `GlassPanelBrush` / `GlassOverlayBrush` | `AcrylicBrush` `#12141B` / `#161820` / `#1B1E28`（均配 `FallbackColor`） | 主侧栏、筛选坞、详情台、建议浮层与菜单 |
| 实色表面 | `SurfaceBrush` / `SurfaceAltBrush` / `SurfaceHoverBrush` | `#13141A` / `#1A1C24` / `#232631` | 输入框、卡片与悬停底色 |
| 文字层级 | `PrimaryTextBrush` / `SecondaryTextBrush` / `TertiaryTextBrush` | `#F4F3EE` / `#A2A3AE` / `#727380` | 主 / 副 / 辅助文字 |
| 主操作与强调 | `AccentFillBrush` / `AccentInkBrush` / `VermilionBrush` | `#F4F3EE` / `#0D0D10` / `#E8503E` | 主 CTA、焦点指示条与进度条 |

### 4.3 浅色主题（Light — 低眩光暖白宣纸）

| 语义角色 | 资源键 | 色值 / 参数 | 主要用途 |
| --- | --- | --- | --- |
| 窗口底布 | `AppBackgroundBrush` / `AppCanvasGradientBrush` | `#ECE9E1`（`#F0EDE6 → #EAE6DD → #E4DFD4`） | 低眩光暖白底布 |
| 磨砂层 | `GlassRailBrush` / `GlassPanelBrush` / `GlassOverlayBrush` | `#F5F3EC` / `#FAF8F3` / `#FCFBF7` | 侧栏、筛选坞与浮层 |
| 实色表面 | `SurfaceBrush` / `SettingsCardBackgroundBrush` / `SurfaceAltBrush` | `#F7F5F0` / `#EEF8F6F1` / `#E3DFD5` | 卡片、设置分组卡与次级容器 |
| 文字层级 | `PrimaryTextBrush` / `SecondaryTextBrush` / `TertiaryTextBrush` | `#1F2024` / `#53555E` / `#72747F` | 主 / 副 / 辅助文字 |
| 主操作与强调 | `AccentFillBrush` / `AccentInkBrush` / `VermilionBrush` | `#23252B` / `#FAF8F4` / `#C93A2B` | 主 CTA、选中胶囊与指示条 |

### 4.4 统一导航样式族（`Styles/Navigation.xaml`）

| 样式族 | 样式键 | 指示器与交互 | 适用页面 |
| --- | --- | --- | --- |
| 竖向主导轨 | `MainNavRailItemStyle` / `SettingsNavSectionStyle` | 左侧 `3px` 朱砂滑轨 + `NavActivePlateBrush` | 主侧栏、设置 8 分区 |
| 竖向列表导轨 | `SearchScopeNavItemStyle` / `LiveGroupNavItemStyle` | 左侧 `3px` 随滚动跟踪的朱砂滑轨 | 搜索站点轨、直播源分组 |
| 竖向高频列表 | `VerticalNavInlineItemStyle` | 行内固定指示条 | 直播频道长列表 |
| 横向分类胶囊 | `HorizontalNavChipItemStyle` | 共享 `ScrollViewer` + 单实例滑行胶囊 | 点播页分类栏 |
| 横向分段槽 | `HorizontalNavSegmentItemStyle` | 槽内滑行胶囊 + 底部朱砂点 | 媒体库 4 分段导航 |
| 横向状态卡 | `HorizontalNavCardItemStyle` | 四网盘无模糊卡片 + 底缘居中朱砂光轨 | 我的云盘 4 网盘切换 |

### 4.5 高对比度（HighContrast）

全部画刷自动映射至系统语义色（`SystemColorWindowColor` / `WindowText` / `Highlight`），自动关闭装饰光晕与 `GlassOpticsEngine` 光学层。

---

## 5. 排版、网格与命中尺寸

- **字体栈**：标题 `Segoe UI Variable Display, Microsoft YaHei UI, Segoe UI`；正文 `Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI`；等宽数字/时间码 `Cascadia Mono, Consolas`。
- **字阶**（正文 `≥13 DIP`）：Hero `34/42` · PageTitle `30/38` · Section `20/28` · CardTitle `16/22` · Body `15/22` · Secondary `14/20` · Meta `13/18`。
- **圆角与间距**：间距基数 `4 DIP`（长文可读宽度上限 `800–840 DIP`）；圆角 Tag `4` · Control `8` · Card `10` · Panel `14` · Chip `18` · Pill `999`。
- **命中尺寸**：导航项 `48` · 主按钮/输入框 `44` · 次按钮/图标按钮 `40` · 列表行 `≥48`；点播海报 `188×282`（2:3），媒体库继续观看卡片为响应式宽屏比例（`164–246 DIP` 高）。
