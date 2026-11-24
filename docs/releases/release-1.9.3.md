# 流光影院 1.9.3 发行说明

发布日期：2026-11-24

## 核心更新

### 1. 彻底根治点播及全站导航多选残留，统一全站横向/竖向导航体系
- **根因彻底消除**：定位点播页分类胶囊点击后全部卡死在白色选中态的根本原因——旧实现采用 `ItemsControl + ToggleButton(IsChecked Mode=OneWay)` 并在点击回调写入本地值，导致 WinUI 3 `OneWay` 绑定永久失效。现全面重构为 `SelectionMode="Single"` 的 `ListView` + 外层共享 `ScrollViewer` + 单实例高反差弹簧滑行胶囊（`CategoryGlidePill`），从架构层面杜绝多选态残留与横向滚动 1 帧滞后。
- **统一导航样式与动效控制器**：新增 `src/TV/Styles/Navigation.xaml` 与 `src/TV/Controls/NavigationMotion.cs`（`NavigationRailController`），集中托管全站 7 处竖向/横向导航的完整 WinUI 3 `CommonStates`（含 `SelectedUnfocused / PointerOverSelected / PressedSelected / SelectedDisabled`）、前景继承字阶、滚动跟随、移出视口自动淡隐与主题切换重锚定，并按用途做克制而合理的差异化：
  - **主侧栏 / 设置分区 / 搜索站点 / 直播分组**：统一贴边 `3px` 朱砂竖向弹性滑轨（`GlideVerticalIndicator`）；高频百项的直播频道列表保持克制行内指示（`VerticalNavInlineItemStyle`），不做跨屏长跳；
  - **点播分类 / 媒体库分段**：高反差弹簧滑行胶囊（`PillMatchWidth`），媒体库搭配顶部光丝、底部朱砂微光点与内容区水平方向切换；
  - **我的云盘账号卡**：零模糊四网盘状态卡 + 底缘居中弹性拉伸朱砂光轨（`GlideHorizontalBar`）与账号切换方向性过渡。

### 2. 浅色护眼主题重塑与浅色五层光学玻璃优化
- **素白画廊 · 温润护眼版**：告别刺眼 `#FFFFFF / #F6F5F0` 过曝洗白，将画布重调为约 90% 亮度的温润亚麻画廊宣纸三段微渐变（`#F0EDE6 → #EAE6DD → #E4DFD4`，基底 `#ECE9E1`），主/次/辅文字采用柔墨阶梯（`#1F2024 / #53555E / #72747F`）与传统印泥朱砂（`#C93A2B`），并同步更新浅色标题栏按钮墨色。
- **浅色五层光学玻璃透光与立体界定**：为 `LiquidBackdrop` 增加浅色主题专属日光珍珠/微青/浅陶环境光场；校准 `GlassRailBrush / GlassPanelBrush / GlassOverlayBrush` 透光率与暖矿石四面立体外框（`GlassEdgeBrush`）+ `1px` 晶白内倒角（`GlassInnerBevelBrush`）+ 顶缘日光焦散丝（`GlassCausticBrush`）；同步优化浅色主题 `Button / TextBox / ComboBox / Flyout / ContentDialog` 及吸顶筛选坞（带 `ThemeShadow` 柔和浮起）质感。

### 3. 播放页 WinUI 3 原生透明光标自动隐藏真正落位
- 新增 `src/TV/Controls/InputCursorHelper.cs`：解决此前仅依赖 Win32 `ShowCursor` 在 WinUI 3 `ContentIsland / SwapChainPanel` 下被 XAML 输入系统重置而失效的问题。通过 Windows App SDK 1.8 官方 `IInputCursorStaticsInterop`（`CreateFromHCursor`）结合内存透明位图 `HCURSOR`（按系统 DPI 自动适配 32×32 / 48×48）生成原生 `Microsoft.UI.Input.InputCursor`，并绑定至 `PlayerPage`、`RootGrid`、`VideoStageHost`、`VideoView` 及其内部 `SwapChainPanel` 的 `ProtectedCursor`。
- 配套**静止光标 0px 刷新**、**物理屏幕坐标防抖（过滤 `ControlsLayer` 隐藏时 WinUI 3 派发的合成 `PointerMoved`）**与**全屏/窗口缩放几何边界自愈（`SyncAndCheckPointerOverControls`）**：播放中无操作 3 秒且鼠标不在任何控件上时连同控制层同步可靠隐藏，移动 `≥ 3px`、悬停控件、暂停或离窗即刻恢复，且绝不污染进程全局 `ShowCursor` 计数。

### 4. 搜索预测展示及时性与速度深度优化
- **三层流式渐进预测（`0ms` 本地即时 + `80ms` 并发竞速）**：
  - **Phase 1（0ms 零等待）**：键入、中文 IME 上屏或 `Ctrl+V` 粘贴的当帧即同步检索搜索历史、点播当前片库/推荐画廊、媒体库观看历史/收藏/本地文件、会话已见片名与带 5 分钟 TTL 的前缀复用缓存（例如已缓存「流浪」后继续键入「流浪地」零等待过滤上屏）；
  - **Phase 2（80ms 短防抖 + 流式增量回填）**：在 `DoubanService` 新增独立非阻塞快速联想接口 `SuggestTitlesAsync`（绕开 900ms 详情抓取节流锁，约 80–180ms 返回），并与最多 2 个健康站点并发竞速，通过 `Task.WhenAny` 谁先返回谁先增量合并上屏，彻底告别等待 260ms 防抖 + 最慢站点 5 秒超时的卡顿；自动为超时站点设置 3 分钟冷却降权，且增量更新保持键盘上下键选中项与零闪烁渲染。

### 5. 全站页面及子页面层级、盒子分块合理性与滚轮一致性深度优化
- **统一滚轮一致性引擎（`src/TV/Controls/ScrollWheelBehavior.cs`）**：
  - 独立横向条（云盘账号卡/面包屑、媒体库分段槽/本地监视目录）支持普通滚轮与 `Shift+滚轮` 累加目标平滑横滑；
  - 嵌套于纵滑页的横向条（点播展陈画廊/分类栏、详情页相关推荐）普通滚轮 1:1 穿透驱动整页纵滑不卡死、`Shift+滚轮` 平滑横滑，且点播页浮动页眉/吸顶坞滚轮速度与海报网格 1:1 对齐；
  - 修复详情页选集网格（`EpisodeGrid`）因 `IsVerticalScrollChainingEnabled="False"` 导致的嵌套滚轮陷阱，滑至边界自动流畅交还页面滚动；
  - 设置页 `SettingsScroller` 新增未聚焦 `NumberBox` 滚轮防劫持（滚轮经过未聚焦数字框不再误改缓存/音量/线程数），左侧 8 分区导航支持矮窗纵滑。
- **层级与盒子分块合理性治理**：
  - **搜索页**：将「站点 · SITES」标题与分隔线完整封装进左侧 `GlassSurface` 遥测面板，并与右侧结果/历史栏底部分隔线像素级齐平；预选浮层每行补齐轻量搜索图标；
  - **云盘页 & 媒体库页**：将云盘 `2px` 加载进度条内嵌进 `PathBar` 玻璃条底部（与搜索页统一）、收拢媒体库未展开 `InfoBar` 与空本地监视目录的占位行，彻底消除 40px 幽灵空带；云盘文件行改用专用透明细线行样式（不再混用侧栏 `RailListStyle`）；
  - **设置页（8 子分区）**：`SettingsCardStyle` 切换至零额外模糊的轻透建筑表面 `SettingsCardBackgroundBrush`（将同屏 `AcrylicBrush` 控制在 ≤ 2 层，主次层次更清晰且滚动更丝滑）；「外观与内容」合理拆分为「外观与窗口」与「推荐与元数据」两张卡片；补齐「播放」「网盘」表单行内分割线与「关于」子页统一标头，替换最后一处杂散字体删除图标为 `LineIcon`；
  - **详情页 / 直播页 / 投屏页**：详情画报补齐内倒角与顶部焦散高光与下方双栏工作台材质统一；全站可滚动列表/侧栏统一预留 `8–12 DIP` 右侧滚动条避让内边距，投屏舞台矮窗自动启用纵滑。

## 验证范围

- `dotnet build src/TV/TV.csproj -c Release -p:Platform=x64 --no-restore`：0 错误，全站 20 个源 XAML 文件字号审计全部 `>= 12px`。
- 实机回归与双主题视觉验收（0 `last-error.log`）：
  - 深/浅双主题 16 屏全站截屏与单选断言：点播（9 分类连点仅单选）、搜索（4 站点仅单选）、云盘（4 网盘仅单选）、媒体库（4 分段仅单选）全通过；
  - 播放页实机 4.2s 静置验证：`Blank InputCursor ready (48x48)` 与 `Player chrome & cursor auto-hidden after 3s idle (blankCursor=True, swapChain=True)` 通过；
  - `workspace_layout_regression`、`library_regression`、`cast_regression` 与 `popup_regression`（全部 19 处弹层锚定与弹幕锚点）全部通过。
- 发行产物、秒开防回退验速与增量一致性核验：
  - 完整安装包 `TV-Setup-1.9.3-x64.exe`（138.22 MB）：三次启动实测 **314 / 447 / 236 ms**（通过冷启 ≤ 2500 ms、热启 ≤ 1000 ms 及 `.iss` 守卫门限）；
  - 增量更新包 `TV-Update-1.9.3-from-1.9.2-x64.exe`（9.59 MB，20 个变更文件，8.28 MB 净载荷）：两次启动实测 **1016 / 226 ms**，实机静默升级 `1.9.2 → 1.9.3` 全部 **962/962 文件** SHA-256 与 1.9.3 便携包 **100% 逐字节一致**；
  - 便携免安装包 `TV-1.9.3-win-x64-portable.zip`（188.49 MB）及全部 `.sha256` 校验文件均通过核验。
