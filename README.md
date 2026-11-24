# 流光影院 · Liuguang Cinema

一款基于 **C#、WinUI 3 与 LibVLC** 的 Windows 原生桌面影院与类 TVBox 影音客户端。融合**「白 · 隙 · 镜」**现代美术馆式界面美学、120Hz GPU 可打断物理动效与高性能原生解码，一站式覆盖点播、直播、跨站搜索、四大网盘、弹幕、AI 字幕翻译、本地媒体、离线下载与投屏接收。

> **项目边界**：本项目并非 Android TVBox 模拟器，也不是 FongMi/TV 官方分支。界面、Windows 原生服务层与播放控制由 C# / WinUI 3 实现，复杂 CatVod 站点通过独立裁剪的 Java 17 进程隔离运行。应用本身不托管任何影视内容，片源、直播、网盘、字幕与弹幕均来自用户配置或第三方服务。
>
> 想快速了解界面审美与核心卖点？请直接阅读 **[项目图文介绍（含实机宣传图）](docs/showcase.md)**。

---

## 核心特性

| 模块 | 核心能力 |
| --- | --- |
| **视觉与动效** | 原研哉灵感「白 · 隙 · 镜」设计语言、WinUI 3 建筑级磨砂玻璃分层、**暗室素墨 / 素白画廊**双主题 + 高对比度模式；120Hz GPU Compositor 可打断弹簧动效与 1:1 跟手吸顶收起交互 |
| **点播与直播** | 兼容 TVBox 单仓/多仓/直播源；20+ 站点原生 C# 高速适配 + Java CatVod 兼容层；分类筛选、选集、多线路、EPG 节目单、收藏与断点续播 |
| **跨站搜索与换源** | 并发跨站检索、非抢焦中文 IME 联想浮层、单站重试与验证、同名片源一键切换、失效地址自动刷新与历史换源保护 |
| **原生影院播放器** | LibVLC 硬件解码（无硬解自动回退）、HLS/DASH、倍速/画质/画面比例/多音轨、零延迟播放暂停、全屏、画中画、逐集片头片尾跳过与弱网智能预缓存 |
| **跨源弹幕与字幕** | 接入**弹弹play 开放弹幕网络**（16 MB 哈希 + 规范化跨源 `MediaMatching` 匹配 + 手动搜集）与 B 站弹幕、PLL 单调 VSync 防回退渲染；支持字幕前移/后移与 `0.80x–1.25x` 调速 |
| **AI 字幕与实时翻译** | 接入阿里云百炼 API：自动提取音轨生成字级时间轴字幕（`paraformer-v2` / `qwen3-asr-flash-filetrans`）、多语种双语翻译（`qwen-mt-flash` / `qwen-mt-plus`）与 WASAPI 进程级实时字幕（`qwen3-asr-flash-realtime`） |
| **四大网盘直播** | 阿里（含资源库/备份盘双目录与纯 C# 设备会话签名）、夸克、UC、百度登录与个人云盘浏览；回环安全媒体代理、分块 Range 预读与地址自动续签 |
| **本地媒体与下载** | 文件夹递归索引与实时变更监听、同名侧车字幕/弹幕自动挂载、Windows「打开方式」极速直达播放（跳过站点与爬虫初始化）；支持单集另存与批量/HLS 离线下载 |
| **投屏接收与维护** | DLNA/UPnP 局域网发现、手机扫码浏览器选片、电脑确认播放；分类扫描与显式二次确认高级清理 |
| **极速安装与更新** | ~300 ms 秒开安装器与便携免安装包；内置 GitHub 正式版自动检查、限流兜底、国内多镜测速 + 多路 Range 分片断点续传、双向差异增量补丁与强制官方 SHA-256 校验 |

---

## 下载与安装

### 系统要求

- **64 位 Windows 10 1809 及以上**（推荐 Windows 11；已对 Windows 10、多显示器、RDP 与虚拟机做自动降级兼容）
- 支持 D3D11 的显卡驱动（不支持硬解时自动回退软件解码）
- **开箱即用**：无需管理员权限，无需预装 .NET、Java 或 VC++ 运行库（已内置裁剪版 Microsoft 签名 OpenJDK 与应用本地 VC++ CRT）

前往 [GitHub Releases](https://github.com/binchen6/Liuguang-Cinema/releases) 下载：
- `TV-Setup-<version>-x64.exe` — 秒开安装器（推荐）
- `TV-<version>-win-x64-portable.zip` — 解压即用便携包

> 社区开源版本尚未附带商业 Authenticode 证书，开启 Windows 11 **智能应用控制（Smart App Control）** 或 SmartScreen 的设备可能出现提示或拦截，详见 [智能应用控制与签名说明](docs/smart-app-control.md)。下载后请核对官方 SHA-256：

```powershell
Get-FileHash .\TV-Setup-1.9.1-x64.exe -Algorithm SHA256
Get-Content .\TV-Setup-1.9.1-x64.exe.sha256
```

---

## 使用速览

### 片源、搜索与换源
- 内置公开配置主备接口并优先读取本地最近成功缓存，可在 **设置 → 片源** 导入兼容单仓、多仓或直播源。
- 搜索按 `Enter` 或点击「搜索」提交；非抢焦磨砂建议浮层完整保护中文 IME 输入。详情页支持集数搜索与跨站换源，播放地址失效时优先刷新而不覆盖有效历史。

### 播放器与快捷键
顶部栏、进度条与烟熏磨砂底部控制坞无操作 5 秒后平滑沉降并隐藏鼠标（打开菜单或对话框时自动锁定），移动或点击即刻反向唤醒。

| 快捷键 | 功能 | 快捷键 | 功能 |
| --- | --- | --- | --- |
| `Space` | 播放 / 暂停 | `M` / `F` | 静音 / 全屏 |
| `←` / `→` | 后退 / 前进 5 秒 | `P` / `D` | 选集 / 弹幕开关 |
| `↑` / `↓` | 音量调节 | `Z` / `X` | 字幕前移 / 后移 0.5 秒 |
| `A` / `I` | 切换画面比例 / 媒体 HUD | `Esc` | 关闭浮层或返回 |

- 点击底部 **字幕** 可微调偏移/倍速并直达阿里云百炼 AI 字幕生成、双语翻译与实时字幕（API Key 在 **设置 → 弹幕与 AI** 配置）。
- 点击 **跳过** 可按站点、影片、线路与集数分别保存片头/片尾边界；手动选择原画不会被弱网策略自动降档。

### 网盘、本地视频、下载与更新
- **我的云盘**：集中管理阿里、夸克、UC、百度账号与目录直播；登录过期时引导重登并自动恢复原片、集数与进度。
- **本地媒体**：支持多文件导入（仅建索引不移动原文件）、文件夹递归监听与同名侧车字幕/弹幕（`.srt/.ass/.ssa/.vtt/.xml/.json`）。安装器仅添加「打开方式」候选，不篡改系统默认播放器；移除索引不删除磁盘原文件。
- **下载与清理**：批量下载默认保存至系统视频库「流光影院」目录；高级清理先扫描后勾选，已下载媒体、过期临时文件与回收站均需显式勾选确认。
- **软件更新**：**设置 → 关于** 支持手动与 12 小时自动检查（播放中静默记录，离页再弹窗）。下载自动择优国内 HTTPS 加速节点并发分片续传，优先安装文件级双向差异增量包（刷新快捷方式与系统图标缓存），所有字节严格校验 GitHub 官方 SHA-256，校验异常自动回切官方直连。

---

## 隐私与安全

默认数据存储于 `%LOCALAPPDATA%\TV`（含 `tv.db`、配置缓存、`cache`、自动脱敏轮转日志 `diagnostic.log` 与 `last-error.log`）：

- **零账号遥测**：不收集或上传用户观影与账号数据；第三方站点、网盘、豆瓣、B 站、弹弹play 与百炼请求遵循各自隐私条款。
- **本机加密与隔离**：所有网盘凭据、弹弹play 与百炼密钥均以当前 Windows 用户 **DPAPI** 加密存储；本地媒体代理仅监听 `127.0.0.1` 并严格拦截跨域敏感请求头泄露。
- **不侵入系统**：应用内自定义 DNS 仅作用于自身连接，不修改 Windows 系统网络配置。

---

## 从源码构建

环境要求：**.NET 10 SDK**、**Windows 10/11 SDK 19041+**、**JDK 17+ 与 Maven**（Java 引擎）、**Inno Setup 6**（仅打包需要）。

```powershell
git clone https://github.com/binchen6/Liuguang-Cinema.git
cd Liuguang-Cinema

# 编译主程序（输出位于 src/TV/bin/Release/net10.0-windows10.0.19041.0/win-x64/TV.exe）
dotnet restore src/TV/TV.csproj --locked-mode
dotnet build src/TV/TV.csproj -c Release --no-restore

# 编译 Java CatVod 引擎（可选，修改引擎时执行）
mvn -DskipTests -f engine/catvod/pom.xml package

# 使用隔离数据目录运行开发版
$env:TV_DATA_ROOT = Join-Path $PWD 'artifacts/tmp/dev-profile'
& .\src\TV\bin\Release\net10.0-windows10.0.19041.0\win-x64\TV.exe

# 打包完整安装器、便携包与增量补丁（统一输出至 artifacts/installers/）
pwsh -NoProfile -File ./scripts/build-installer.ps1
```

---

## 项目结构与文档导航

```text
src/TV/                  C# / WinUI 3 主程序
  Styles/ & Controls/    「白 · 隙 · 镜」设计令牌、24×24 矢量图标与通用控件
  Views/ & ViewModels/   各页面 XAML、状态绑定与 MotionService GPU 物理动效
  Models/                媒体、站点、网盘与持久化模型
  Services/
    AI/                  阿里云百炼 AI 字幕生成、翻译、音轨提取与实时字幕
    Vod/ & Spider/       CMS、20+ 原生站点、换源恢复与 Java CatVod IPC
    Playback/            解析、HLS/DASH、LibVLC、弹弹play/B 站弹幕、字幕与下载
    Cloud/               四网盘、纯 C# 设备签名、预读与回环安全代理
    Live/ & Casting/     直播源、EPG 节目单与 DLNA/扫码投屏接收
    Library/ & Metadata/ 本地媒体索引、目录监视与豆瓣公开元数据
    Http/ & Imaging/     自定义 DNS、跨域头过滤与高速 HTTP/2 图片缓存
    Persistence/         SQLite、DPAPI 密钥库与分类安全清理（Maintenance/）
    Updates/             GitHub 更新检查、国内加速分片下载与 SHA-256 校验
engine/catvod/           Java 17 CatVod JSON IPC 引擎
third_party/             固定版本上游快照与许可
installer/ & scripts/    Inno Setup 安装/增量脚本、构建签名与回归验收工具
docs/                    设计规范、开发指南、版本说明与图文介绍
```

更多文档见 [文档中心](docs/README.md)：[项目图文介绍](docs/showcase.md) · [开发指南](docs/development.md) · [品牌设计语言](docs/brand-design-system.md) · [设计契约](docs/design-guide.md) · [动效规范](docs/motion-spec.md) · [智能应用控制说明](docs/smart-app-control.md) · [站点验收](docs/site-verification.md) · [1.9.1 发行说明](docs/releases/release-1.9.1.md)。

---

## 贡献、许可与致谢

- 欢迎提交 Issue / PR（请附 Windows 版本、复现步骤与去敏日志，切勿上传 Cookie、Token、数据库或账号截图）。提交前请完成 `dotnet build src/TV/TV.csproj -c Release --no-restore` 及相关隔离回归。
- 原创代码采用 [MIT License](LICENSE)；第三方组件与上游服务不适用同一许可，详见 [第三方代码、服务与许可说明](THIRD_PARTY.md)。
- 致谢：FongMi/TV 与 CatVodSpider 社区、bizhangjie、AndroidCatVodSpider、VideoLAN / LibVLCSharp、.NET 与 Windows App SDK、QRCoder、SkiaSharp、AngleSharp 及公开配置与元数据服务维护者。
