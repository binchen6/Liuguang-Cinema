# 项目文档索引

日常安装与功能概览请先阅读根目录 [README](../README.md)，图文亮点展示见 [项目图文介绍](showcase.md)。

## 现行文档

| 文档 | 内容 |
| --- | --- |
| [项目图文介绍](showcase.md) | 基于实机截图的视觉与核心卖点图文导览 |
| [开发指南](development.md) | 构建环境、隔离运行、代码结构、安全约定与打包流水线 |
| [品牌设计语言规范](brand-design-system.md) | 「白 · 隙 · 镜」视觉哲学、色板、磨砂层级、字阶与图标规范 |
| [设计契约](design-guide.md) | UI/UX 工程边界、响应式布局、交互状态、无障碍与 UIA 契约 |
| [动效工程规范](motion-spec.md) | WinUI Composition GPU 可打断物理动效引擎与全链路编排 |
| [智能应用控制与签名](smart-app-control.md) | Windows 11 SAC 机制、高签名覆盖治理与代码签名路径 |
| [站点适配与验收](site-verification.md) | 当前 1.9.1 验证基线、分级通过标准、探测与回归命令 |
| [站点矩阵](site-matrix.csv) | 各站点首页、分类、搜索、详情与播放状态 |
| [第三方代码与许可](../THIRD_PARTY.md) | 上游来源、固定版本、外部服务与许可证边界 |

## 版本发行说明

- [1.9.3](releases/release-1.9.3.md)：彻底根治点播及全站导航多选残留、统一全站横向/竖向导航样式与物理动效（`Styles/Navigation.xaml` + `Controls/NavigationMotion.cs`）、重塑浅色护眼主题与浅色五层光学玻璃、以 WinUI 3 官方 `IInputCursorStaticsInterop` 原生透明光标实装播放页鼠标自动隐藏、搜索预测 0ms 本地即时上屏 + 80ms 流式并发提速、全站页面层级分块与滚轮一致性（`Controls/ScrollWheelBehavior.cs`）深度优化。
- [1.9.2](releases/release-1.9.2.md)：内核级播放防息屏与防休眠（默认开启）、播放器 3 秒悬停感知自动隐藏与同步隐藏鼠标、自研 `GlassSurface` 五层光学磨砂玻璃重构（解决 `SwapChainPanel` 叠层黑块并全站消除 30+ 滚动期多余模糊 Pass）、全页面动效补全与 GPU Compositor 缓存优化。
- [1.9.1](releases/release-1.9.1.md)：导航选中态复位修复、阿里云盘双盘与 ECDSA 设备签名、双向差异增量升级与图标即时刷新、国内多镜测速分片加速更新、跨源弹幕规范化匹配引擎、点播页 120Hz GPU 可打断吸顶收起动画。
- [1.9.0](releases/release-1.9.0.md)：「白 · 隙 · 镜」原研哉美学品牌重塑、建筑磨砂玻璃分层、「光隙」图标与 24×24 几何图标库、全站界面重构与 Compositor 物理动效升级。
- [1.8.1](releases/release-1.8.1.md)：封面 UI 线程解码修复、GitHub 正式版检查、更新弹窗、SHA-256 校验与文件级增量安装。
- [1.8.0](releases/release-1.8.0.md)：HTTP/2 独立封面池提速、播放/暂停零延迟与 PLL 单调弹幕时钟、SAC 98.4% 签名覆盖与 ~300ms 秒开安装器。
- [1.7.0](releases/release-1.7.0.md)：弹弹play 开放弹幕库、阿里云百炼 AI 字幕生成/翻译/实时字幕、字幕速度与偏移调节、深度性能与安全治理。
- [1.6.1](releases/release-1.6.1.md)：非抢焦中文 IME 搜索建议浮层，新增文采、瓜子、橘猫、韩迷、热播、聚剧 6 个原生站点。
- [1.6.0](releases/release-1.6.0.md)：全站 UX/UI 重构、WinUI Composition 可打断动效引擎与 12+ 站点原生还原。
- [1.5.0](releases/release-1.5.0.md)：VSync 流畅弹幕、单播放器冷启提速、网盘分块/HLS 预读重构与站点验证码鉴权。
- [1.4.1](releases/release-1.4.1.md) / [1.4.0](releases/release-1.4.0.md)：播放生命周期、预读、清理、安全治理与全面审查。
- [1.3.1](releases/release-1.3.1.md) / [1.3.0](releases/release-1.3.0.md) / [1.2.0](releases/release-1.2.0.md)：搜索换源、四网盘、本地媒体、下载与早期视觉基线。

## 历史归档

[归档目录](archive/) 保留早期站点验收记录、旧版 1.2 设计稿与过程资料，仅作追溯，不代表当前实现。
