# 流光影院 · 视觉与材质规范

> 本文记录「流光影院」当前代码实现的色彩令牌、五层玻璃表面控件（`Controls/GlassSurface`）、图标库与统一导航样式；交互契约见 [设计契约](design-guide.md)，动效参数见 [动效规范](motion-spec.md)。

---

## 1. 设计目标与主题方向

- **内容优先与低干扰层级**：以留白、`1px` 分割线和清晰字阶区分结构，克制多层实心卡片嵌套。
- **高反差主操作与单一强调色**：主要操作按钮与选中分类使用深浅高反差填充（`AccentFillBrush` / `AccentInkBrush`），朱砂红（`VermilionBrush`）仅用于焦点指示条、播放进度与在线状态点。
- **受控 GPU 模糊预算**：区分宏观磨砂表面（使用 WinUI 3 `AcrylicBrush`）与虚拟化列表/海报微元素（使用无背景模糊的渐变画刷），控制单屏背景采样开销。

---

## 2. 光学玻璃控件与 GPU 预算（`Controls/GlassSurface`）

### 2.1 五层表面结构

播放器悬浮控制坞、顶部双浮岛、暂停圆镜、OSD / HUD / AI 字幕浮条及部分页面悬浮栏基于 `Controls/GlassSurface` 组合：

| 层级 | 作用 | 当前实现（DirectComposition / XAML） |
| --- | --- | --- |
| **L1 基底** | 半透明背景 | 常规页面使用 `AcrylicBrush`；播放器位于 LibVLC `SwapChainPanel` 之上（WinUI 3 `CompositionBackdropBrush` 无法采样独立 DXGI SwapChain），使用半透明深色线性渐变基底以保证视频内容透射且不出现黑块 |
| **L2 体积渐变** | 顶左高光与底缘压暗 | 对角线 `LinearGradientBrush`（`VolumeBrush`），提升浅色视频画面上白色图标的可读性 |
| **L3 微晶纹理** | 模拟细磨砂表面 | 静态懒加载共享的 1:1 程序化纹理位图（仅播放器表面按需启用，一次性生成后复用） |
| **L4 双层描边** | 外框与内倒角 | 外 `1px` 定向渐变外框 + 内 `1px` 上亮下暗倒角边 |
| **L5 顶部焦散丝** | 上下边缘微反光 | 顶部 `1px` 峰值渐变线条 + 底部低透明度反光边；非 `SwapChainPanel` 悬浮容器（如点播吸顶筛选坞、搜索建议浮层）可附加 `ThemeShadow`，播放器悬浮层不使用 `ThemeShadow` 以避免叠加在高频视频 `SwapChainPanel` 上触发额外 DWM 合成负担 |

### 2.2 GPU 模糊预算约定

1. **宏观 `AcrylicBrush` 数量上限**：单屏同时可见的真实 `AcrylicBrush` 宏观层控制在 `≤ 4` 块（侧栏、吸顶筛选坞、命令搜索条/建议浮层、检视面板）。
2. **隐藏后折叠**：播放器控制层、OSD、HUD、暂停按钮与搜索建议浮层在退场动画结束后通过 `CompositionScopedBatch` 设为 `Visibility.Collapsed`，静默播放期间不参与布局与渲染。
3. **虚拟化微件不使用 `AcrylicBrush`**：海报角标（`GlassDarkBadgeBrush`）、胶囊与列表选中底板（`GlassChipBrush` / `NavActivePlateBrush`）及设置分组卡片（`SettingsCardBackgroundBrush`）统一采用普通渐变或半透明实色，避免列表滚动时产生大量并发背景模糊。
4. **指针移动期不动态改写画刷**：悬停与按压反馈通过 `VisualStateManager` 与 Compositor `Scale / Translation / Opacity` 完成，不在 `PointerMoved` 中实时重建画刷。
5. **降级与高对比度**：所有 `AcrylicBrush` 均提供半透明 `FallbackColor`，窗口失焦或系统关闭透明效果时平顺退化；高对比度模式自动映射至系统语义色。

---

## 3. 品牌徽标与图标库

- **应用图标**：由 `scripts/New-AppIcon.ps1` 生成 `src/TV/Assets/App.png`（512×512）与 `src/TV/Assets/App.ico`（包含 `16 / 20 / 24 / 32 / 40 / 48 / 64 / 128 / 256` 九档位图），应用内徽标为 `src/TV/Controls/BrandMark.xaml` 矢量实现。
- **线性图标库（`Styles/Icons.xaml` + `Controls/LineIcon.cs`）**：基于 `24×24` 网格、`1.65px` 默认线宽、圆润端点，覆盖导航、通用操作、设置、网盘文件与播放器按钮；`LineIcon` 为每个实例独立创建 `Path`，兼容虚拟化容器复用。界面功能图标不使用 Emoji。

---

## 4. 色彩与材质令牌（同步自 `Styles/Tokens.xaml` 与 `App.xaml`）

### 4.1 播放器玻璃令牌（`App.xaml`）

| 语义角色 | 资源键 | 参数 / 色值 | 主要用途 |
| --- | --- | --- | --- |
| 主控制坞基底 | `PlayerGlassBrush` | `#4C1B2130 → #5E131722 → #720B0E16`（`FrostGrainOpacity="0.085"`） | 底部悬浮控制坞、顶部左右浮岛、投屏预览坞 |
| 悬浮胶囊/圆镜 | `PlayerGlassPillBrush` | `#5E1C2232 → #70131722 → #840B0E16`（`FrostGrainOpacity="0.10"`） | 暂停按钮、OSD、HUD、AI 字幕条、状态卡 |
| 菜单与弹层 | `PlayerGlassFlyoutBrush` | `#D6161B28 → #E40E111A` | 播放选项、倍速、画质、音轨、字幕与弹幕弹层 |
| 体积漫射层 | `PlayerGlassVolumeBrush` | `#30F4F8FF → #16E4ECFF → #0AE0E8FF → #2C04060D` | `GlassSurface` L2 体积光影与底部图标托底 |
| 内倒角边 | `PlayerGlassInnerBevelBrush` | `#2EFFFFFF → #0AFFFFFF → #06000000 → #30000000` | `GlassSurface` L4 内 `1px` 倒角边 |
| 顶部焦散丝 | `PlayerGlassCausticBrush` | `#00FFFFFF → #96FFFFFF → #58FFFFFF → #22FFFFFF → #00FFFFFF` | `GlassSurface` L5 顶部 `1px` 高光丝 |
| 菲涅尔外框 | `PlayerGlassBorderBrush` | `#8CFFFFFF → #38FFFFFF → #1CFFFFFF → #4CFFFFFF` | `GlassSurface` L4 外 `1px` 边框 |
| 交互反馈态 | `PlayerGlassHoverBrush` / `Pressed` / `RimHover` | `#2CFFFFFF` / `#42FFFFFF` / `#62FFFFFF` | 播放器按钮悬停与按压态 |

### 4.2 深色主题（Dark / Default — 默认）

| 语义角色 | 资源键 | 参数 / 色值 | 主要用途 |
| --- | --- | --- | --- |
| 窗口底布 | `AppBackgroundBrush` | `#0B0B0D` | 页面底层背景（配 `LiquidBackdrop` 环境底纹） |
| 磨砂侧轨 | `GlassRailBrush` | `AcrylicBrush` (`#12141B`, Opacity `0.60`, Luminosity `0.74`, Fallback `#EB12141B`) | 主侧栏、设置侧轨 |
| 磨砂面板 | `GlassPanelBrush` | `AcrylicBrush` (`#161820`, Opacity `0.54`, Luminosity `0.68`, Fallback `#E615171F`) | 吸顶筛选坞、详情工作台、直播侧栏、投屏控制台 |
| 磨砂浮层 | `GlassOverlayBrush` | `AcrylicBrush` (`#1B1E28`, Opacity `0.74`, Luminosity `0.82`, Fallback `#F2181B24`) | 搜索建议浮层、回顶按钮、菜单与弹窗 |
| 无模糊微件 | `GlassChipBrush` / `GlassDarkBadgeBrush` / `NavActivePlateBrush` | 线性半透明渐变 | 筛选胶囊、海报角标、导航选中底板 |
| 外框与高光 | `GlassEdgeBrush` / `GlassCausticBrush` | `#3EFFFFFF → #1CFFFFFF → #0EFFFFFF → #22FFFFFF` / 顶部渐变丝 | 玻璃面板外描边与上缘高光 |
| 实色表面 | `SurfaceBrush` / `SurfaceAltBrush` / `SurfaceHoverBrush` | `#13141A` / `#1A1C24` / `#232631` | 输入框、次级容器、悬停态 |
| 文字层级 | `PrimaryTextBrush` / `SecondaryTextBrush` / `TertiaryTextBrush` | `#F4F3EE` / `#A2A3AE` / `#727380` | 主文字 / 副文字 / 辅助信息 |
| 主操作 | `AccentFillBrush` / `AccentInkBrush` | `#F4F3EE` / `#0D0D10` | 主按钮与选中胶囊 |
| 强调与状态 | `VermilionBrush` / `SuccessBrush` / `DangerBrush` | `#E8503E` / `#4CC38A` / `#F26B5E` | 导航指示条、进度条、连通与警示状态 |

### 4.3 浅色主题（Light）

浅色主题使用低眩光暖灰宣纸底色（`#ECE9E1`）搭配浅色 `AcrylicBrush` 与暖矿石外框，降低长时间阅读的纯白反光。

| 语义角色 | 资源键 | 参数 / 色值 | 主要用途 |
| --- | --- | --- | --- |
| 窗口底布 | `AppBackgroundBrush` / `AppCanvasGradientBrush` | 基底 `#ECE9E1`（渐变 `#F0EDE6 → #EAE6DD → #E4DFD4`） | 低眩光页面底布 |
| 磨砂层 | `GlassRailBrush` / `GlassPanelBrush` / `GlassOverlayBrush` | `#F5F3EC` (`0.58/0.68`) / `#FAF8F3` (`0.52/0.62`) / `#FCFBF7` (`0.76/0.80`) | 侧栏、筛选坞与浮层 |
| 外框与倒角 | `GlassEdgeBrush` / `GlassInnerBevelBrush` | `#26322D25 → #1B322D25 → #302E2921` / `#F2FFFFFF → #1A5C5448` | 浅色面板边界界定 |
| 实色表面 | `SurfaceBrush` / `SettingsCardBackgroundBrush` / `SurfaceAltBrush` | `#F7F5F0` / `#EEF8F6F1` / `#E3DFD5` | 卡片、设置分组卡与次级底色 |
| 文字层级 | `PrimaryTextBrush` / `SecondaryTextBrush` / `TertiaryTextBrush` | `#1F2024` / `#53555E` / `#72747F` | 主 / 副 / 辅助文字 |
| 主操作与强调 | `AccentFillBrush` / `AccentInkBrush` / `VermilionBrush` | `#23252B` / `#FAF8F4` / `#C93A2B`（`VermilionSoftBrush`: `#1AC93A2B`） | 主 CTA、选中胶囊与指示条 |

### 4.4 统一导航样式族（`Styles/Navigation.xaml`）

横向与竖向单选导航均收敛至 `Styles/Navigation.xaml` 与 `Controls/NavigationMotion.cs`，完整定义 `CommonStates`（`Normal / PointerOver / Pressed / Disabled / Selected / SelectedUnfocused / PointerOverSelected / PressedSelected / SelectedDisabled`）：

| 样式族 | 样式键 | 指示器与动效 | 适用页面 |
| --- | --- | --- | --- |
| **竖向主导轨** | `MainNavRailItemStyle` / `SettingsNavSectionStyle` | 左侧 `3px` 朱砂竖轨（`GlideVerticalIndicator`）+ `NavActivePlateBrush` | 主侧栏、设置 8 分区导航 |
| **竖向列表导轨** | `SearchScopeNavItemStyle` / `LiveGroupNavItemStyle` | 左侧 `3px` 随滚动跟踪的朱砂竖轨（移出视口淡隐） | 搜索站点轨、直播源分组 |
| **竖向高频列表** | `VerticalNavInlineItemStyle` | 行内固定左侧指示条（不做跨多行长距离跳动） | 直播频道长列表 |
| **横向分类胶囊** | `HorizontalNavChipItemStyle` | 外层共享 `ScrollViewer` + 单实例滑行胶囊（`PillMatchWidth`） | 点播页分类栏 |
| **横向分段槽** | `HorizontalNavSegmentItemStyle` | 槽内胶囊滑行 + 底部朱砂点 + 内容区水平切入 | 媒体库 4 分段导航 |
| **横向状态卡** | `HorizontalNavCardItemStyle` | 无模糊四网盘卡片 + 底缘居中朱砂光轨（`BottomAccentBar`） | 我的云盘 4 网盘切换 |

### 4.5 高对比度（HighContrast）

全部画刷资源均映射至 `SystemColorWindowColor`、`SystemColorWindowTextColor`、`SystemColorHighlightColor` 等系统语义色，自动退化为纯色高对比显示并隐藏装饰性光晕。

---

## 5. 排版与尺寸规格

- **字体栈**：
  - 标题：`Segoe UI Variable Display, Microsoft YaHei UI, Segoe UI`
  - 正文：`Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI`
  - 等宽数字/时间码：`Cascadia Mono, Consolas, Segoe UI Variable Text`
- **字阶**（正文字号 `≥13 DIP`，辅助标签 `≥12 DIP`）：
  - `FontSizeHero` 34/42 · `FontSizePageTitle` 30/38 · `FontSizeSection` 20/28 · `FontSizeCardTitle` 16/22 · `FontSizeBody` 15/22 · `FontSizeSecondary` 14/20 · `FontSizeMeta` 13/18
- **网格与圆角**：
  - 间距基数 `4 DIP`（常用 `4 / 8 / 12 / 16 / 24 / 32 / 48`），长文本最大宽 `800–840 DIP`。
  - 圆角：`RadiusTag = 4` · `RadiusControl = 8` · `RadiusCard = 10` · `RadiusPanel = 14` · `RadiusChip = 18` · `RadiusPill = 999`。
  - 命中高度：导航项 `48` · 主按钮 / 输入框 `44` · 次按钮 / 图标按钮 / 菜单项 `40` · 列表行 `≥48`；海报默认 `188×282`（2:3）。
