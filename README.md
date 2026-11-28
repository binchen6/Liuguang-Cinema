# 流光影院 · Liuguang Cinema

基于 **C#、WinUI 3（Windows App SDK 1.8）、.NET 10 与 LibVLC** 的 Windows x64 原生桌面影音客户端。以光学液态玻璃美学、三层全源自适应生态与原生硬解播放为核心，在一扇窗口内整合点播、直播、四大个人网盘、自适应本地媒体库与投屏接收。

![流光影院主界面](./docs/assets/promo/01-hero-cinema.png)

> **项目定位与边界**：本项目是原生 Windows 桌面应用，不是 Android TVBox 模拟器，也不是 FongMi/TV 官方分支。界面、播控与核心站点适配器由 C# 原生实现，部分 CatVod 站点通过随包分发的独立 Java 17 进程运行。应用本身不托管或提供任何影视资源，所有片源、直播、网盘、字幕与弹幕均来自用户配置或第三方公开服务。
>
> 图文功能导览见 [项目功能介绍](docs/showcase.md)。

---

## 核心卖点

1. **光学液态玻璃美学 × 专属玻璃光影引擎（`GlassOpticsEngine`）**
   暗室素墨（`#0B0B0D`）与低眩光暖白宣纸（`#ECE9E1`）双主题，支持系统高对比度模式。玻璃折射、高光和点击波纹在交互时按需加载，指针更新上限约 120 Hz。关闭动效时使用基础样式。
2. **三层全源自适应生态 × 仓库资源包（原生 > 通用 > 专属包）**
   支持 C# 原生直连、通用多协议、自动 HTML、`drpy2 js:` 与 `CryptoJS`、Python 和 Android DEX 动态转译。导入仓库时可匹配专属资源包。独立资源包仓库提供 SHA-256，下载包体时可走国内镜像。支持按仓切换，导入时可自定义仓库名，也可随时重命名。 [Liuguang-RepoPacks](https://github.com/binchen6/Liuguang-RepoPacks)
3. **LibVLC 原生硬解 × 零回弹精准播控**
   默认 `:avcodec-hw=any` 硬件解码（不支持时自动降级软解），搭载 `PlaybackSeekCoordinator` 确定性状态机、悬浮时间预览胶囊与网盘跳转实时取消旧预读，消除进度条回弹与暂停抖动；支持 3 秒悬停感知隐藏控制栏与原生透明光标、播放期防息屏/防休眠与可选阿里云百炼 AI 双语/实时字幕。
4. **四大网盘直连 × 自适应媒体库 × 直播与投屏**
   阿里、夸克、UC、百度网盘通过本地 `127.0.0.1` 回环代理与分块缓存免转存直播；媒体库「继续观看」采用 1–6 列全宽自适应宽屏信息流，联动收藏、HLS/单集下载、本地目录递归监听与「打开方式」直达播放，并内置 M3U/TXT 直播（含 EPG）与 DLNA/扫码投屏接收。
5. **纯净原生工程 × 本地优先隐私边界**
   免管理员权限、免预装 .NET/Java/VC++ 运行库、98.4% 微软签名 PE 覆盖率、~300 ms 秒开安装器与便携 ZIP；支持 5 镜国内加速文件级双向差异增量更新与官方 SHA-256 校验；全部密钥经 Windows DPAPI 本机加密、跨域头隔离、日志自动脱敏、零遥测上报。

---

## 功能一览

| 模块 | 实现说明 |
| --- | --- |
| **光学玻璃与主题** | 深色（`#0B0B0D`）、浅色（`#ECE9E1`）与高对比度主题；每屏宏观 `AcrylicBrush ≤ 4` 块，结合 `GlassSurface` + `GlassOpticsEngine` 光学折射光影与 `NavigationMotion` 统一导航动效 |
| **三层点播适配** | 兼容 TVBox 单仓/多仓、注释 JSON、`8位**` Base64 与 AES-128-CBC 加密配置；支持导入自定义仓库名、已导入重命名与按仓隔离；聚合 22 个原生 C# 适配器、`UniversalProtocol`（AppYs/AList/360/网盘合集）、`UniversalAutoHtml`、`Jint`（`drpy2 js:` + `CryptoJS.AES` + T3）、`Python`、Java 17 DEX 动态转译及自动/手动仓库资源包 |
| **搜索与换源** | 多站并发搜索、本地历史/片库即时联想 + 防抖流式远端建议（不抢占中文 IME 焦点）、单站重试、应用内验证弹窗与同名片源切换 |
| **播控、字幕与弹幕** | LibVLC 硬件解码、无回弹拖拽与时间预览、倍速/画质/画面比例/多音轨/片头片尾跳过、画中画与防休眠；支持外挂字幕偏移（±60s）与调速（`0.80x–1.25x`）、阿里云百炼 AI 字幕生成/双语翻译/WASAPI 实时字幕（需自备 Key），以及弹弹play 开放弹幕库（含国内直连签名中继与 `cas2.dandanplay.net` CDN 直连）与 B 站弹幕 |
| **四大个人网盘** | 阿里云盘（资源库/备份盘 + 纯 C# 设备签名）、夸克、UC（设备令牌 + 原画/转码智能回退）、百度网盘扫码登录、目录直读与分块缓存播放 |
| **媒体库与下载** | 继续观看 1–6 列响应式宽屏信息流、收藏管理、本地目录递归监听与同名侧车字幕/弹幕挂载、Windows「打开方式」直达播放、单集另存与 HLS 批量下载 |
| **直播、投屏与清理** | M3U/TXT 直播源聚合、EPG 节目单与多线路切换；局域网 DLNA/UPnP 与手机扫码投屏（电脑确认播放）；分类扫描与显式确认的安全清理 |
| **安装与增量更新** | Inno Setup 秒开安装器与免安装便携 ZIP；5 条国内 GitHub 镜像测速回退、多路 Range 断点下载、文件级双向差异增量补丁与强制 SHA-256 校验 |

> **站点可用性说明**：受上游反爬、验证码、域名变动或闭源 Android ARM `.so` / 加密参数限制，极少数站点无法在桌面端直接运行，应用会在列表或说明卡中如实提示而不破解闭源 DRM，详见 [站点适配与验收](docs/site-verification.md)。

---

## 下载与安装

### 系统要求

- **64 位 Windows 10 1809（Build 17763）及以上**（推荐 Windows 11；不支持 Mica 或 HDR 时自动降级）
- 支持 D3D11 的显卡驱动（不支持硬解时自动回退软解）
- 客户端自带 .NET、Java 17、VC++ CRT 和 Python 3 嵌入版。缺少 WebView2 时，可联网按需安装 Evergreen 运行时。

从 [GitHub Releases](https://github.com/binchen6/Liuguang-Cinema/releases) 下载：
- `TV-Setup-<version>-x64.exe` — 标准安装器（推荐）
- `TV-<version>-win-x64-portable.zip` — 解压即用便携包

> 社区开源发行版未附带商业 Authenticode 代码签名证书，在开启 Windows 11 **智能应用控制（SAC）** 或 SmartScreen 的设备上可能出现拦截，详见 [智能应用控制与签名说明](docs/smart-app-control.md)。下载后建议校验官方 SHA-256：

```powershell
Get-FileHash .\TV-Setup-2.0.1-x64.exe -Algorithm SHA256
Get-Content .\TV-Setup-2.0.1-x64.exe.sha256
```

---

## 常用操作与快捷键

- 在 **设置 → 片源** 导入仓库，在 **仓库资源包** 刷新清单、下载、更新、卸载或导入本地 ZIP。
- **媒体库 → 本地媒体** 按视频和音乐筛选。打开音乐后，可在播放器中读取内嵌或同名封面和歌词文件、导入文件，或按曲名和歌手在线匹配。
- **弹幕屏蔽**。右键点播放器里的弹幕开关，输入普通屏蔽词或正则表达式，再点「应用并关闭」。正则每行一条，`^跳过[0-9]{2,4}$` 会屏蔽「跳过120」。基础规则只筛选明确的整条招揽广告，可独立关闭。
- **沉浸播控**：无操作 3 秒且鼠标未悬停控件时自动收起顶/底栏并隐藏鼠标；播放期防息屏/防休眠默认开启，可在 **设置 → 播放** 或播放器菜单切换。

| 快捷键 | 功能 | 快捷键 | 功能 |
| --- | --- | --- | --- |
| `Space` | 播放 / 暂停 | `M` / `F` | 静音 / 全屏 |
| `←` / `→` | 后退 / 前进 5 秒 | `P` / `D` | 选集 / 弹幕开关 |
| `↑` / `↓` | 音量调节 | `Z` / `X` | 字幕前移 / 后移 0.5 秒 |
| `A` / `I` | 切换画面比例 / 媒体信息 | `Esc` | 关闭浮层或返回 |

---

## 隐私与本地数据

默认数据保存在 `%LOCALAPPDATA%\TV`（包含 `tv.db`、配置缓存 `config/`、已下载资源包 `packs/`、`cache/` 及轮转日志 `diagnostic.log`）：

- **无自建统计上报**：不收集观影记录或账号数据；使用默认在线弹幕中继时仅转发匹配所需片名/哈希，配置自有凭据后直连弹弹play。
- **本地加密与隔离**：网盘 Cookie/Token、百炼 API Key 与自定义弹幕凭据均以 Windows **DPAPI** 加密存储；本地媒体代理仅监听 `127.0.0.1` 并在跨域重定向时剥离敏感头；日志自动脱敏密钥字段；自定义 DNS 仅作用于本进程。

---

## 从源码构建

新增片源仓库、站点或单独适配包时，见 [仓库与站点适配规范](docs/repository-site-adaptation.md)。

环境要求：**.NET 10 SDK**、**Windows 10/11 SDK 19041+**、**JDK 17+ 与 Maven**（仅修改 Java 引擎时需要）、**Inno Setup 6**（仅打包安装器时需要）。

```powershell
git clone https://github.com/binchen6/Liuguang-Cinema.git
cd Liuguang-Cinema

# 还原并编译 x64 Release 主程序
dotnet restore src/TV/TV.csproj --locked-mode
dotnet build src/TV/TV.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 --no-restore

# 可选：修改 engine/catvod 后重新打包 Java 引擎
mvn -DskipTests -f engine/catvod/pom.xml package

# 使用隔离目录运行，避免影响正式用户数据
$env:TV_DATA_ROOT = Join-Path $PWD 'artifacts/tmp/dev-profile'
& .\src\TV\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\TV.exe

# 打包安装器、便携包与增量补丁（输出至 artifacts/installers/）
pwsh -NoProfile -File ./scripts/build-installer.ps1
```

---

## 目录结构与文档

```text
src/TV/                  C# / WinUI 3 主程序
  Styles/ & Controls/    设计令牌、矢量图标、GlassSurface、GlassOpticsEngine、导航与弹幕层
  Views/ & ViewModels/   页面 XAML、状态绑定、响应式网格与 MotionService 动效编排
  Models/                媒体、站点、资源包、网盘与持久化模型
  Services/
    Vod/ & Spider/       三层路由、22 个原生站点、通用协议/AutoHtml、Jint JS、Python 与 Java IPC
    Packs/               仓库资源包清单解析、自动匹配、SHA-256/Zip-Slip 校验与热加载
    Playback/            播放解析、SeekCoordinator、LibVLC 泵、字幕、弹幕、防休眠与下载
    Cloud/               四网盘接口、纯 C# 阿里设备签名、CloudBlockCache 与回环代理
    AI/                  阿里云百炼字幕生成、翻译、音轨提取与 WASAPI 实时字幕
    Live/ & Casting/     直播源、EPG 与 DLNA/扫码投屏接收
    Library/ & Metadata/ 本地媒体索引、目录监视与豆瓣公开元数据
    Http/ & Imaging/     GitHubMirrorService 国内加速、自定义 DNS、头隔离与 HTTP/2 图片池
    Persistence/         SQLite、DPAPI SecretStore 与安全清理（Maintenance/）
    Updates/             GitHub 更新检查、多镜断点下载与双向差异增量升级
engine/catvod/           Java 17/21 CatVod IPC 引擎、DEX 动态转译与 Android 兼容层
installer/ & scripts/    Inno Setup 安装/更新脚本、构建打包、协议回归与站点探测脚本
docs/                    功能介绍、视觉与光学玻璃规范、设计契约、动效规范与站点验收
```

文档索引见 [文档中心](docs/README.md)：[功能介绍](docs/showcase.md) · [视觉与材质规范](docs/brand-design-system.md) · [设计契约](docs/design-guide.md) · [动效规范](docs/motion-spec.md) · [开发指南](docs/development.md) · [站点验收](docs/site-verification.md) · [智能应用控制说明](docs/smart-app-control.md)。

---

## 许可与致谢

- 原创代码遵循 [MIT License](LICENSE)；第三方组件与上游服务适用各自许可，详见 [THIRD_PARTY.md](THIRD_PARTY.md)。
- 致谢：FongMi/TV 与 CatVodSpider 社区、bizhangjie、AndroidCatVodSpider、VideoLAN / LibVLCSharp、Jint、dex2jar、AngleSharp、SkiaSharp、QRCoder、.NET 与 Windows App SDK 及相关开源维护者。
