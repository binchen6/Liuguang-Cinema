# 项目文档索引

日常安装与使用说明见根目录 [README](../README.md)，界面与功能图文概览见 [功能介绍](showcase.md)。

## 现行文档

| 文档 | 内容 |
| --- | --- |
| [功能介绍](showcase.md) | 基于实机截图的界面与主要功能概览 |
| [开发指南](development.md) | 构建环境、隔离运行、代码结构、安全约定与打包流程 |
| [设计语言规范](brand-design-system.md) | 色彩、光学玻璃层级、字阶、导航样式族与图标令牌 |
| [设计契约](design-guide.md) | 响应式布局、交互状态、滚轮与弹层契约、无障碍与 UIA 名称 |
| [动效规范](motion-spec.md) | WinUI Composition 动效参数、编排目录与性能约束 |
| [智能应用控制与签名](smart-app-control.md) | Windows 11 SAC 机制、发行包签名覆盖率与代码签名路径 |
| [站点适配与验收](site-verification.md) | 当前验证基线、内置源适配现状、分级标准与回归命令 |
| [站点矩阵](site-matrix.csv) | 逐站首页、分类、搜索、详情与播放实测记录 |
| [第三方代码与许可](../THIRD_PARTY.md) | 上游来源、固定版本与许可证边界 |

## 版本发行说明

- [1.9.4](releases/release-1.9.4.md)：消除播放器高回报率鼠标移动卡顿（节流快路径、去移动期 `SetCursorPos`、移除 `SwapChainPanel` 实时 `ThemeShadow`）、提升多环境兼容（LibVLC `:avcodec-hw=any`、多显/老系统 API 兜底）、修复 `AI剧`/`盘搜`/`奥特` 搜索并排除空 `XPath` 占位项、支持明文 URL `csp_T4`、清理死代码并精简事实化核对全站文档。
- [1.9.3](releases/release-1.9.3.md)：统一全站横竖导航并根治点播多选残留与切回「最新影片」胶囊丢失、移除全站弹层双层滚动条叠加、优化浅色护眼主题与浅色玻璃、以 WinUI 3 原生透明 `InputCursor` 实装播放页鼠标自动隐藏、搜索预测分层提速与全站滚轮一致性治理。
- [1.9.2](releases/release-1.9.2.md)：内核级播放防息屏/防休眠、播放器 3 秒悬停感知隐藏、`GlassSurface` 多层光学玻璃重构与 Compositor 缓存优化。
- [1.9.1](releases/release-1.9.1.md)：导航选中态复位修复、阿里云盘双盘与 ECDSA 设备签名、双向差异增量升级、国内多镜分片下载与跨源弹幕规范化匹配。
- [1.9.0](releases/release-1.9.0.md)：视觉体系重构、九档 `App.ico` 与矢量图标库、Composition 动效引擎与深浅/高对比主题切换。
- [1.8.1](releases/release-1.8.1.md) / [1.8.0](releases/release-1.8.0.md)：封面解码修复、GitHub 更新与文件级增量升级、HTTP/2 图片池、PLL 弹幕时钟与安装器启动优化。
- [1.7.0](releases/release-1.7.0.md)：弹弹play 弹幕库、阿里云百炼 AI 字幕生成/翻译/实时字幕与日志脱敏。
- [1.6.1](releases/release-1.6.1.md) / [1.6.0](releases/release-1.6.0.md)：非抢焦中文 IME 搜索建议、新增 6 个原生站点与界面交互重构。
- [1.5.0](releases/release-1.5.0.md) / [1.4.1](releases/release-1.4.1.md) / [1.4.0](releases/release-1.4.0.md)：VSync 弹幕、冷启提速、网盘预读与安全治理。
- [1.3.1](releases/release-1.3.1.md) / [1.3.0](releases/release-1.3.0.md) / [1.2.0](releases/release-1.2.0.md)：搜索换源、四网盘、本地媒体与早期视觉基线。

## 历史归档

[归档目录](archive/) 仅保留历史验收日志、旧版 1.2 设计稿与过程记录，不代表当前实现。
