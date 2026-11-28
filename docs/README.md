# 项目文档索引

日常安装与使用说明见根目录 [README](../README.md)，图文概览见 [功能介绍](showcase.md)。

## 现行文档

| 文档 | 说明 |
| --- | --- |
| [功能介绍](showcase.md) | 基于最新实机截图的核心卖点与全模块导览 |
| [视觉与材质规范](brand-design-system.md) | 深浅/高对比主题、五层玻璃 `GlassSurface`、`GlassOpticsEngine` 光学引擎与图标令牌 |
| [设计契约](design-guide.md) | 响应式布局（含自适应媒体库信息流）、导航与滚轮契约、播放器与 UIA 名称 |
| [动效工程规范](motion-spec.md) | WinUI Composition 物理弹簧、玻璃光学交互动效参数与零空闲开销约束 |
| [开发指南](development.md) | 构建环境、隔离运行、代码结构、仓库资源包安全规范与增量打包流程 |
| [代码书写规范](coding-standards.md) | 模块化反重复、C#/WinUI 3 工程规范、控件居中与双主题光学边界 |
| [仓库与站点适配规范](repository-site-adaptation.md) | 原生、通用与单独适配包的选型，仓库配置、资源包格式及提交验收 |
| [站点适配与验收](site-verification.md) | 三层适配架构基线（含 `y456y` 与资源包实测）、分级标准与回归命令 |
| [站点矩阵](site-matrix.csv) | 逐站首页、分类、搜索、详情与播放实测记录 |
| [智能应用控制与签名](smart-app-control.md) | Windows 11 SAC 机制、98.4% 微软签名覆盖与代码签名路径 |
| [第三方代码与许可](../THIRD_PARTY.md) | 上游依赖、仓库资源包与许可证边界 |

## 发行说明

- [2.0.6](releases/release-2.0.6.md) · 三类弹幕源智能匹配（B站/聚合/弹弹play）、片头片尾按节目生效、仓库备用地址、.so 站点通用兼容；修复设置页无法打开。
- [2.0.3](releases/release-2.0.3.md) · 磁力播放、桌面兼容接口、宽屏全屏修复与 Bouncy Castle 升级。
- [2.0.1](releases/release-2.0.1.md) · 音乐播放器、逐句歌词、资源包 ZIP 导入及通用适配。
- [2.0.0](releases/release-2.0.0.md) · 正则弹幕屏蔽、精简基础规则、仓库资源包和 Windows 兼容性。
- [1.9.7](releases/release-1.9.7.md)：三层通用安卓 TVBox 站点适配（DEX 转译、Jint JS、AppYs/AList、Python）、多仓库切换隔离、零回弹精准拖拽与国内直连中继架构。
- [1.9.6](releases/release-1.9.6.md)：UC 原画与转码智能切换、厂长浏览器渲染兼容、云盘分页、增量升级与凭据隔离强化。
- [1.9.5](releases/release-1.9.5.md)：阿里云盘分享直读与转存原画修复、UC 设备令牌与提示视频探测、DNS/超时取消治理。
- [1.9.4](releases/release-1.9.4.md)：消除播放器高回报率鼠标卡顿、提升多环境硬解兼容、站点搜索修复与文档核对。
- [1.9.3](releases/release-1.9.3.md)：全站统一横竖导航、弹层单滚动条治理、低眩光浅色暖白主题与原生透明光标隐藏。
- [1.9.2](releases/release-1.9.2.md)：内核级播放防息屏/防休眠、3 秒悬停感知隐藏与 `GlassSurface` 多层光学玻璃重构。
- [1.9.1](releases/release-1.9.1.md)：阿里云盘双盘与纯 C# 设备签名、国内多镜分片双向增量更新与导航选中态修复。
- [1.9.0](releases/release-1.9.0.md)：视觉体系重构、九档 `App.ico` 与 24×24 几何图标库、Composition 动效引擎与双主题切换。
- 早期版本：[1.8.1](releases/release-1.8.1.md) · [1.8.0](releases/release-1.8.0.md) · [1.7.0](releases/release-1.7.0.md) · [1.6.1](releases/release-1.6.1.md) · [1.6.0](releases/release-1.6.0.md) · [1.5.0](releases/release-1.5.0.md) · [1.4.1](releases/release-1.4.1.md) · [1.4.0](releases/release-1.4.0.md) · [1.3.1](releases/release-1.3.1.md) · [1.3.0](releases/release-1.3.0.md) · [1.2.0](releases/release-1.2.0.md)。

## 历史归档

[归档目录](archive/) 仅保留早期 1.2 设计基线与历史验收日志，不代表当前实现。
