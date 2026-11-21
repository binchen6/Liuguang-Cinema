# 流光影院 · 视觉与页面重构规范（1.2 目标）

> 本文件是 1.2 前端重构的唯一实现口径。所有页面必须以这里为准；与本文件冲突的旧实现一律改成本文件口径。

## 0. 目标与硬约束

1. 主诉是「界面太小、太素」。整体字号、控件、封面放大一档，留白更从容，信息层级一眼可读。
2. 视觉语言＝「深墨底 + 暖金流光」。禁止继续使用旧的深色+绿按钮观感，禁止沿用旧截图布局。
3. 只用可复现证据（构建 + artifacts/qa-fixture 回归 + 截图）说话。
4. 保留 1.1.9 已验收行为：搜索独立页、吸顶筛选与回到顶部、投屏/设置宽屏铺满、播放器控制层、托盘降级、网盘扫码、窗口 55% 记忆等。

## 1. 设计令牌（唯一来源）

`src/TV/Styles/Tokens.xaml` 定义颜色/字阶/间距/尺寸，`src/TV/Styles/Controls.xaml` 定义控件样式。页面里**禁止**写死字号数字、颜色 hex、导航/按钮尺寸；一律引用资源键。

- 颜色：`AppBackgroundBrush` `SurfaceBrush` `SurfaceAltBrush` `SurfaceHoverBrush` `SurfaceElevatedBrush` `PosterPlaceholderBrush` `DividerBrush` `PrimaryTextBrush` `SecondaryTextBrush` `TertiaryTextBrush` `SubtleBorderBrush` `StrongBorderBrush` `AccentBrush` `AccentFillBrush` `AccentSoftBrush` `SelectionBrush` `AccentInkBrush` `DangerBrush` `SuccessBrush` `RatingBrush`。
- 字号：`FontSizePageTitle`30 `FontSizeSection`21 `FontSizeCardTitle`16 `FontSizeBody`15 `FontSizeSecondary`14 `FontSizeMeta`13。**全站不得出现 <13px 文本**（唯一例外：`TitleBar` 里的“Ctrl F”徽标 11px）。
- 间距：`SpaceMini`8 `SpaceSmall`12 `SpaceMedium`16 `SpaceLarge`24 `SpaceXLarge`32；页面外边距 `PagePaddingWide`(32) / `PagePaddingStandard`(24) / `PagePaddingNarrow`(16) 三选一。
- 控件尺寸：`PrimaryButtonHeight`44 `SecondaryButtonHeight`40 `IconButtonSize`40 `InputHeight`44 `NavItemHeight`48 `ListRowHeight`48；圆角 `RadiusCard`12 `RadiusControl`8。
- 海报：`PosterWidth`188 `PosterHeight`282 `PosterColumnGap`18 `PosterRowGap`22。

## 2. 文本样式（TextBlock.Style 必须显式引用）

| 用途 | Style | 规格 |
| --- | --- | --- |
| 页面主标题 | `PageTitleText` | 30/38 SemiBold |
| 分区标题 | `SectionTitleText` | 21/28 SemiBold |
| 卡片/影片标题 | `CardTitleText` | 16/22 SemiBold，最多 2 行省略 |
| 正文 | `BodyText` | 15/22 Regular |
| 次级说明 | `SecondaryText` | 14/20 |
| 元数据/时间/来源 | `MetaText` | 13/18 |
| 表单小标签 | `LabelText` | 14 SemiBold |

禁止在 TextBlock 上直接写 `FontSize="14"` 之类数字；需要特殊字号时先用令牌键（如 `FontSize="{StaticResource FontSizeSecondary}"`）。

## 3. 控件样式

- 主操作（每屏通常只有 1 个）：`PrimaryButtonStyle`（44 高、暖金填充、墨色字）。
- 次操作：`SecondaryButtonStyle`（40 高）；纯文字：`GhostButtonStyle`；图标按钮：`IconButtonStyle`（40×40）；播放器内圆形按钮：`PlayerIconButtonStyle`。
- 筛选胶囊：`FilterChipStyle`（ToggleButton，36 高，选中暖金）。
- 需要边界的整块区域：`PanelCardStyle`（表面色 + 细边 + 圆角 12 + 16 内边距）。**克制使用**：优先用留白和排版分组，一张页面通常 ≤2 块卡片。
- 输入框/下拉框已有隐式样式（44 高 / 40 高），不要再单独设 Height。
- 图标一律用 `FontIcon`（Segoe Fluent/MDL2）或 `PathIcon`，**禁止 emoji**。

## 4. 布局规范

- 页面根：`Padding` 用 `PagePaddingWide`（内容区宽度 ≥1100 时）/ `PagePaddingStandard` / `PagePaddingNarrow`；页面背景 `Transparent`（壳层是 `AppBackgroundBrush`）。
- 区块之间 24–32，组内 12–16，元素内边距 8–12（4px 网格）。
- 页头结构统一：左侧 `PageTitleText` 主标题（可带一行 `SecondaryText` 副信息），右侧操作区（次按钮/图标按钮），操作区与标题基线对齐。
- 列表/网格行高 ≥48；可点击控件 ≥40×40。
- 空状态/加载/错误统一：`FontIcon` 40px（`SecondaryTextBrush`）+ `SectionTitleText` + `SecondaryText` + 主/次按钮，垂直居中，间距 12/16。
- 深色是默认体验；不得用大面积渐变/玻璃/光晕背景动画。海报图上的压暗渐变（<30% 高度）允许。

## 5. 页面规格

### 5.1 点播 VodPage（负责人：lead）
- 页头：`点播` 主标题 + 右侧 搜索 / 重新加载 / 筛选（保留 AutomationProperties.Name：`打开搜索` `重新加载` `筛选`）。
- 精选横向画廊：卡片 320×180 圆角 12，标题 16 SemiBold 压底部渐变，评分 `RatingBrush`。
- 分类胶囊 + 站点选择 + 筛选 Flyout（保留 `站点` 头名与 `筛选` 弹出行为）。
- 海报网格：Item 宽 ≥188（`PosterWidth`），2:3 封面圆角 12，标题 `CardTitleText` 两行，元数据 `MetaText` 一行（年份·站点·清晰度）；悬停显示播放遮罩（`AccentFillBrush` 圆形播放钮）+ 边框 `AccentBrush`；骨架占位 `PosterPlaceholderBrush`；图片失败回退 `FontIcon E8B9`。
- 滚动态：页头与画廊收起（1.1.9 行为，workspace_layout_regression 断言滚动后 `筛选` 的 y 至少上移 35px），`回到顶部` 浮动按钮出现，滚回顶部恢复；`筛选` 行保持可用。
- 底部：`StatusMessage` + `加载更多` 按钮（`SecondaryButtonStyle`）。

### 5.2 搜索 SearchPage（负责人：agent-search）
- 顶部大搜索框（44 高）+ 主按钮「搜索」；**不自动搜索**，回车或点按钮才提交；新提交取消旧请求（ViewModel 已有 CancelPending）。
- 搜索历史：可点击条目、可删除单条与清空，空历史显示引导文案（`SecondaryText`）。
- 结果区（保留名称 `搜索关键词`、`搜索`、`山海·快站`（=站点结果分组标题命名规则）、`慢站`、`山海·慢站`、`下载`、`返回`）：
  - 左侧站点列表（宽 ~220，选中行高 ≥48，显示站点名 + 结果数），右侧结果瀑布流海报。
  - 单站重试按钮（失败站点行内 `重试`）；空站点显示「空站」。
  - 结果按影片聚合去重（同一影片多站点合并，展示来源线路数），历史点击直搜。
  - 返回后保留关键词、站点选择与滚动位置。

### 5.3 详情 DetailPage（负责人：agent-search）
- 两栏：左封面宽窗 260×390，低高度窗口按可用高度降为 208×312 或 176×264（圆角 12），右侧标题 30/38、元数据 13、简介 15/22 最多 6 行 + 展开。
- 主操作「播放/继续观看」最突出；播放、收藏、下载、投屏统一 112×44 DIP，宽窗一排、窄窗 2×2，收藏/下载/投屏为次按钮/图标按钮，即时反馈。
- 来源线路选择（胶囊或列表）、选集面板（虚拟化，自动定位当前集）、相关推荐横排海报。
- 空/错误状态齐全；保留返回（标题栏已有）。

### 5.4 播放器 PlayerPage（负责人：agent-player）
- 播放器页占满内容宽度，普通窗口保留系统窗控和拖动区。离开播放器后恢复浏览页的侧栏宽度。
- 视频全屏黑底；顶部标题、底部进度与操作控制层作为同一层级，在 5 秒无操作后一起淡出，视频区域鼠标同步隐藏；鼠标移动/点击唤出。
- 控制层分组：顶部（返回、标题、集数）、中部（播放/暂停、上一集/下一集、快进/快退 10s）、底部（进度条、时间、集数/倍速、音量、画质、音轨、字幕、弹幕、投屏、画中画、全屏）。
- 底部控制按「播放控制 / 媒体控制」分组，只保留播放、选集、倍速、音量、画质、片头片尾、弹幕、投屏和全屏等高频操作；音轨、字幕、在线匹配与画中画收进顶部播放选项。图标与文字按钮统一 44 DIP 高、8 DIP 圆角；播放键用暖金填充，其余按钮在轻底容器内使用透明底。全部按钮命中区域 ≥40×40，图标 20–24；进度条高 ≥6、可拖拽；时间文字 14，其他元数据 13。
- 加载/缓冲/错误/直播态齐全；不改播放器业务逻辑（PlayerViewModel），只改视觉与控件布局；保留 x:Name 与事件处理器。

### 5.5 直播 LivePage（负责人：agent-live）
- 布局：左侧 源/分组 两级列表（每行 ≥48），右侧频道网格或列表；窄窗时 源→分组→频道 单任务切换。
- 同频道多线路聚合展示（清晰度标签 13px），点击直接播放。
- 空状态/加载/错误；搜索框（如已有）44 高。

### 5.6 投屏 CastPage（负责人：agent-live）
- 宽屏铺满（1.1.9 行为）：左接收状态+设备会话列表 + 右「投屏内容」舞台；`开始接收` 按钮在主舞台垂直居中（workspace_layout_regression 断言 `开始接收` 与 `投屏内容` 水平中心误差 <30px）。
- DLNA/UPnP 设备与当前播放信息齐全，状态 13/14px；保持 `投屏内容` `开始接收` 名称。

### 5.7 媒体库 LibraryPage（负责人：agent-library）
- 顶部页头 + 三个分段（继续观看 / 收藏 / 下载）用筛选胶囊或分割控件（名称保留 `收藏` `下载`）。
- 继续观看：横向大卡片（300×170）+ 进度条；收藏/下载：高密度列表（行高 ≥56），每行含封面缩略图、标题、副信息、右侧操作（继续播放/删除）。
- 空状态分别给出引导与主操作。

### 5.8 设置 SettingsPage（负责人：agent-library）
- 放弃长 Expander：左侧可导航分区（片源 / 播放 / 网盘 / 网络 / 外观与内容 / 存储 / 关于），右侧内容区；`设置内容` 名称与 `导入仓库` 按钮位置断言保留（`导入仓库` 在 `设置内容` 内、x 偏移 < 设置内容宽度的 20%）。
- 表单行：左标签 14 SemiBold + 右控件，行高 ≥56，分组用 `SectionTitleText` + 分割线，不用卡片套卡片。
- 保留所有现有设置项与命令绑定、Flyout 对话框（如 UC 扫码：`网盘` 展开后出现 `UC 网盘扫码` / `等待扫码` / `关闭扫码`）。

## 6. 交互与动效

- hover/press/focus 五态齐全；选中态不能只靠颜色（加指示条/加粗/图标变化）。
- 微交互 100–180ms，页面过渡 180–280ms；`MotionService.CanAnimate` 为 false 时必须禁用动画。
- 键盘：Tab 可达所有操作，Enter/Space 激活，Esc 关闭浮层，Alt+Left 返回。
- 焦点视觉使用系统焦点框（`UseSystemFocusVisuals`），不要自绘。

## 7. 代码/契约约束（重要）

- 保留 ViewModel 绑定路径与 x:Name、事件处理器名称；只增不减。若必须改 code-behind，保留原事件处理器与 AutomationProperties.Name。
- 这些 AutomationProperties.Name 被回归脚本引用，不得删除或改名：`点播` `直播` `媒体库` `投屏` `设置` `搜索` `打开搜索` `重新加载` `筛选` `站点` `点播海报` `回到顶部` `加载更多` `下载` `收藏` `搜索关键词` `山海·快站` `慢站` `山海·慢站` `返回` `网盘` `UC 网盘扫码` `等待扫码` `关闭扫码` `开始接收` `投屏内容` `设置内容` `导入仓库`。
- 不得修改：`App.xaml`、`Styles/*.xaml`、`MainWindow.xaml(.cs)`、其他负责人负责的页面、`artifacts/qa-fixture/*`。
- 不要求你单独构建（多人并行会抢 MSBuild）；改完自查 XAML：资源键存在、绑定路径与 ViewModel 属性一致、无未定义事件处理器。

## 8. 圆角规范（1.2 补充，反馈#1）

统一 6 档圆角，全部来自 `Styles/Tokens.xaml`，页面禁止自写数字：

| 令牌 | 值 | 适用 |
| --- | --- | --- |
| `RadiusTag` | 4 | 角标、标签、评分小徽章、快捷键提示 |
| `RadiusControl` | 8 | 按钮、输入框、下拉、图标按钮、导航项 |
| `RadiusChip` | 10 | 筛选胶囊、分段控件、状态胶囊 |
| `RadiusCard` | 12 | 海报封面、内容卡片、预览面板 |
| `RadiusPanel` | 16 | 弹层（Flyout/Dialog）内容面板、大侧栏卡片 |
| `RadiusPill` | 999 | 纯圆形（播放悬浮按钮、头像、开关轨道） |

规则：
1. 嵌套时内层圆角 = 外层圆角 − 内边距，最小 4；避免「大圆角里套小直角」。
2. 同一屏内圆角档位不超过 3 种（例如卡片 12 + 控件 8 + 角标 4）。
3. 筛选胶囊不再用 999 全圆：36–40 高控件用 10，避免与 8/12 的控件、卡片打架显得突兀。
4. 选中态用「淡色底 + 主题色描边 + SemiBold 字」三件套，不再只用纯色填充。

## 9. 去重与全局搜索（1.2 补充，反馈#4）

- 标题栏不再放搜索按钮（与页面内重复）；`打开搜索` 只保留 VodPage 页头一处，全局快捷键 Ctrl+F 仍可用。
- 页面内出现同类操作时，保留离内容更近的那一处，删除窗口级重复入口。
