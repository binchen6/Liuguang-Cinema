# 设计契约

本文约定「流光影院」当前实现的 UI/UX 工程边界、响应式布局、交互状态、无障碍要求与 UIA 自动化名称。

- 色彩、玻璃材质、字阶、圆角与图标令牌见 [视觉与材质规范](brand-design-system.md)。
- Compositor 弹簧参数与动效编排见 [动效工程规范](motion-spec.md)。
- 历史 1.2 基线见 [归档](archive/design-guide-1.2.md)，仅作追溯。

---

## 1. 令牌复用、统一导航与滚轮契约

- `Styles/Tokens.xaml`、`Styles/Icons.xaml`、`Styles/Controls.xaml`、`Styles/Navigation.xaml` 与 `App.xaml` 是全站样式入口；新增或修改页面须复用既有语义资源键，避免硬编码色值、字号或零散图标。
- **统一单选导航**：横向与竖向单选导航统一采用 `SelectionMode="Single"` 的 `ListView` + `Styles/Navigation.xaml` + `Controls/NavigationMotion.cs`（不再使用 `ItemsControl + ToggleButton(OneWay)`），避免多选态残留；列表重建且存在待定位选中项时，由 `NavigationRailController` 通过一次性 `LayoutUpdated` 等待项容器生成，同序列刷新直接复用既有分类项集合；非虚拟化横向项模型需重写 `ToString()` 返回可读名称。
- **层级与盒子分块**：每屏同时可见的真实 `AcrylicBrush` 宏观层控制在 `≤ 4` 块；次级表单分组（如设置页卡片）采用无背景模糊的 `SettingsCardBackgroundBrush`；状态条与加载进度条内嵌于父级栏位或按需显隐，避免在 `Grid RowSpacing` 中留下空白带；可滚动列表右侧预留 `8–12 DIP` 滚动条避让内边距。
- **滚轮一致性（`Controls/ScrollWheelBehavior.cs`）**：
  - 独立横向条（云盘账号卡/面包屑、媒体库分段槽/监视目录）启用 `HorizontalWheelMode="Direct"`，普通滚轮与 `Shift+滚轮` 均可横滑；
  - 嵌套于纵滑页的横向条（点播画廊/分类栏、详情页相关推荐）启用 `HorizontalWheelMode="ShiftOnly"`，普通滚轮穿透驱动整页纵滑、`Shift+滚轮` 横滑；
  - 详情页选集网格启用 `IsVerticalScrollChainingEnabled="True"`，滑到边界后将滚轮交还整页；
  - 设置页与弹层滚动容器启用 `GuardNumberBoxWheel="True"`，滚轮经过未聚焦 `NumberBox` 时滚动容器而不误改数值。
- 每个区域突出一个高反差主操作（`AccentFillBrush` / `AccentInkBrush`），次级操作使用半透明表面、`1px` 细描边或文字按钮。
- 列表与海报网格使用虚拟化与增量分页。

---

## 2. 响应式布局契约

- **主侧栏**：宽窗按文字测量在 `196–320 DIP` 间自适应（默认 `208 DIP`），包含品牌区、全局搜索（`Ctrl+F`）、一级导航（点播、直播、媒体库、云盘、投屏）与底部设置；窄窗折叠为 `76 DIP` 图标导轨；进入播放页时隐藏。
- **点播页**：页眉 + 推荐画廊 + 随滚动收起的吸顶筛选坞 + 2:3 海报网格（默认窗口海报宽 `≥188 DIP`）+ 悬浮回顶按钮。
- **详情页**：顶部背景画报（支持滚动视差）+ 四个统一尺寸操作按钮（空间不足转为 2×2）+ 双栏片源线路与选集矩阵；窄窗纵向排列。
- **搜索页**：命令搜索条（底部内嵌 2px 进度线）+ 页面内非抢焦建议浮层（本地历史/片库/前缀缓存即时上屏 + `80ms` 防抖并发竞速豆瓣联想与健康站点，不中断中文 IME 组合输入与粘贴）+ `244 DIP` 左侧站点遥测面板 + 右侧历史/结果流。
- **直播页**：宽窗为三栏（源组导轨 + 频道列表 + EPG/线路面板），窄窗按「直播源/分组 → 频道 → 详情」分步导航。
- **媒体库页**：横向分段滑轨；窄窗将搜索框独立成行；继续观看采用宽幅卡片，其余分区为高密度列表。
- **云盘 / 投屏 / 设置页**：约束长文最大可读宽度（`800–840 DIP`）；云盘文件表与账号卡窄窗自适应；投屏为左控制台 + 右预览舞台；设置包含 8 分区导航（片源、播放、弹幕与 AI、网盘、网络、外观与内容、存储、关于）。

---

## 3. 播放器与弹层契约

- **播放画面与悬浮控制层**：画面区域保持纯黑；顶部双浮岛与底部控制坞基于 `GlassSurface` 构建（不在高频 `SwapChainPanel` 上叠加 `ThemeShadow`）。
- **低开销指针追踪与自动隐藏**：
  - 播放中控制层已显示时，`OnPointerMoved` 采用 O(1) 节流快路径（`200ms` 节流采样物理指针位移，懒挂定时器，不在每次鼠标事件上遍历可视化树、执行 `TransformToVisual` 或重启 `DispatcherQueueTimer`），避免高回报率鼠标在播放器内移动时卡顿；
  - 播放中无操作 **3 秒且鼠标指针不在任何控件上停留**后，控制层平滑收起并折叠，同时通过 `IInputCursorStaticsInterop` 生成透明 `InputCursor` 绑定至播放页、`VideoView` 与 `SwapChainPanel` 的 `ProtectedCursor` 隐藏光标；
  - 鼠标有效移动（`≥ 3px`）、触控/笔输入、暂停或窗口失焦时立即唤醒并恢复光标（移动唤醒不调用 `SetCursorPos`，避免指针回拉顿挫），不修改进程全局 `ShowCursor` 计数。
- **播放防休眠与防息屏**：默认开启，播放/投屏期间通过 `PowerCreateRequest` / `PowerSetRequest`（Display + System，双源引用计数）阻止系统息屏与休眠；暂停、停止、离页或退出时释放；可在「设置 → 播放」与播放器「更多选项」开关。
- **硬件解码兼容性**：开启硬解时向 LibVLC 传递 `:avcodec-hw=any`，在支持的 GPU 上使用硬件加速，在不支持 D3D11VA 的旧显卡、虚拟机或 RDP 环境下自动回退，避免黑屏。
- **显隐锁定**：悬停控件、拖拽进度条，或菜单、字幕/弹幕设置、登录、换源及对话框打开期间禁止自动隐藏。
- **快捷键与 OSD**：保留 `Space`、方向键、`M`、`F`、`P`、`D`、`Z/X`、`A`、`I`、`Esc`；音量、倍速、进度与字幕延迟通过 `GlassSurface` OSD 提示。
- **弹层、单一滚动条与锚点**：WinUI 3 `FlyoutPresenter` 与 `ContentDialog` 已内置宿主 `ScrollViewer`，可滚动弹层禁止再嵌套外层 `ScrollViewer`；内嵌独立 `ListView` 的弹层通过 `PlayerGlassStaticFlyoutPresenterStyle` / `StaticListFlyoutPresenterStyle` 禁用宿主滚动条；菜单项触发的二次弹层显式锚定至对应工具栏按钮；高风险清理、删除与退出账号须二次确认。

---

## 4. 无障碍、动效与防竞态

- 所有图标按钮提供 `AutomationProperties.Name` 与 `ToolTip`；主要命中区域 `≥40×40 DIP`；选中态同时通过位置指示条、底板材质与字重/色彩表达。
- 支持键盘导航：`Tab` 可达全部交互、`Enter/Space` 激活、`Esc` 逐层返回或关闭浮层。
- 所有异步请求与延迟折叠动画回调均受令牌/版本守卫，过期结果不回写新状态。

---

## 5. UIA 自动化名称契约

现有桌面回归依赖以下 `AutomationProperties.Name`，迭代时须保持稳定（变更时须同步更新回归脚本）：

`点播`、`点播分类`、`直播`、`媒体库`、`云盘`、`投屏`、`设置`、`打开搜索`、`重新加载`、`筛选`、`站点`、`点播海报`、`回到顶部`、`搜索关键词`、`搜索建议`、`搜索`、`下载`、`收藏`、`返回`、`网盘`、`等待扫码`、`关闭扫码`、`开始接收`、`投屏内容`、`设置内容`、`导入仓库`。

> 页面布局使用 DIP，UIA 边界与截图为物理像素；跨设备对比时须记录 Windows DPI 缩放比例。
