# 流光影院开发指南

## 本地构建

本项目使用 WinUI 3、Windows App SDK 1.8 和 .NET 10，生成非打包的 Windows x64 客户端。目标平台是 Windows 10 SDK 19041，项目声明的最低系统版本是 Windows 10 1809。开发环境的 SDK 放在工作区的 `.dotnet`。

在工作区根目录执行以下 PowerShell 命令。首次构建或依赖变更后先运行 `.\.dotnet\dotnet.exe restore src/TV/TV.csproj --locked-mode`。

```powershell
New-Item -ItemType Directory -Force 'artifacts/tmp' | Out-Null
$env:TMP = (Get-Item 'artifacts/tmp').FullName
$env:TEMP = $env:TMP
& .\.dotnet\dotnet.exe build src/TV/TV.csproj -c Release --no-restore
```

输出程序是 `src/TV/bin/Release/net10.0-windows10.0.19041.0/win-x64/TV.exe`。当前工作区已建立本地 Git，`a50021f` 保存 1.2.0 续修前的源码基线。构建缓存、会话数据和本机验收日志留在工作区。按站点或问题拆分改动和证据。在没有 Git 的副本里，改动前将已有文件按原相对路径复制到独立的 `.bak` 目录。

## 运行与诊断

开发和自动化使用工作区内的独立数据目录。下列命令会打开窗口，请在桌面可供测试时执行。

```powershell
$env:TV_DATA_ROOT = Join-Path $PWD ('artifacts/tmp/dev-' + [guid]::NewGuid().ToString('N'))
& .\src\TV\bin\Release\net10.0-windows10.0.19041.0\win-x64\TV.exe
```

| 路径 | 内容 |
| --- | --- |
| `<TV_DATA_ROOT>/config/last-good.json` | 最近一次成功的内置配置。导入仓库在数据库设置 `source.imports` 中 |
| `<TV_DATA_ROOT>/tv.db` | SQLite 设置、历史、收藏及下载状态。网盘和 B 站凭据由 Windows DPAPI 加密 |
| `<TV_DATA_ROOT>/diagnostic.log` | 启动、配置加载、页面导航、爬虫、解析和播放状态 |
| `<TV_DATA_ROOT>/last-error.log` | 应用捕获的未处理异常 |

未设置 `TV_DATA_ROOT` 时使用当前 Windows 用户的本地应用数据目录。受限环境中的临时目录可能引起 WinUI 启动失败。窗口打开但页面未切换时，搜索日志中的 `页面导航失败`。测试配置库失效时，先检查应用能否连接脚本监听的 `127.0.0.1`。

## 代码地图

| 路径 | 职责 |
| --- | --- |
| `src/TV/MainWindow.xaml(.cs)`、`Views`、`ViewModels` | 导航与返回栈、各页面交互和异步状态；`CloudDrivePage` / `CloudDriveViewModel` 负责个人云盘目录与搜索 |
| `Services/Configuration/SourceConfigService.cs` | 内置配置的缓存与刷新、仓库导入、按 URL 合并直播源 |
| `Services/Vod/VodClientRouter.cs`、`Services/Vod/Native`、`CmsClient.cs` | 原生适配器优先。其余 type 3 走 CatVod，其余受支持的 CMS 走通用接口 |
| `Services/Spider`、`engine/catvod` | 子进程桥接，负责 `home`、`category`、`search`、`detail`、`play` |
| `Services/Live`、`LiveViewModel.cs` | 读取 TXT、M3U、JSON，维护源、分组、频道线路和 EPG |
| `Models/PlaybackParsing.cs`、`Services/Playback` | 线路和剧集拆分，解析直链、HLS、静态 DASH、WebView2、轨道和 HDR，调用 LibVLC |
| `Services/Cloud`、`Services/Http`、`Services/Imaging` | 四网盘分享/个人文件、统一扫码协调、可刷新媒体代理、请求头与重定向范围、封面缓存 |

修业务逻辑时同时核对入口和返回链路，尤其是搜索取消、切换站点、从详情返回、选集后切换线路。页面改动遵循[设计契约](design-guide-1.2.md)，保留 UIA 名称。WinUI 布局使用 DIP，自动化截图常用物理像素。150% 缩放下两者比例为 1.5。

新增原生适配器时，在 `AppServices` 和后台探测入口注册同一实现。首页或分类首屏可通过 `CmsResponse.Filters` 提供该分类的筛选定义。原生适配器通过 `ICategoryFilterVodProvider` 接收选项，Java 桥接接收 `extend`。玩偶哥哥在打开各分类时获取其原站选项。画质需要重新取媒体地址时，实现 `IQualityVodProvider`。哔哩合集采用这两个接口，可用其分类排序和画质切换作为复验样本。

哔哩源通过「登陆配置」或播放页「清晰度」里的「B 站账号」扫码。各哔哩合集共用当前应用配置里的账号。`BilibiliAccountService` 验证登录后保存加密会话，个人凭据用于 B 站官方播放接口。选档匹配 B 站编号，启用状态取决于本次响应中的媒体流。高画质分离视频使用有界 HTTP Range 请求和分块预读。

通用播放从 HLS 的 `RESOLUTION` 和 `FRAME-RATE`、静态 DASH 的 `Representation` 信息及网盘转码列表提取档位。最高可选档显示为「原画（实际规格）」，其余档位在声明高帧率时标注帧率。直链单文件在解码后读取实际规格。含加密保护或需要自定义分段头的 DASH 保留默认播放路径。

玩偶哥哥的公开片库与网盘实播使用独立路径。搜索按原站会话间隔顺序提交。网盘实播使用用户确认的登录配置和样片，测试停止、切换线路和续播前先验证样片确实输出视频和音频。夸克原画 MP4 和转码 HLS 使用 LibVLC 的 FFmpeg 解复用器，当前 Windows 发行库将它打包在 `plugins/codec/libavcodec_plugin.dll`。

直播源的分组顺序、台标和 EPG 来自原列表。同名频道可聚合为一个频道项，每个不同地址保留为独立线路。修复媒体传输时同时核对请求头、跨域重定向和 Range，保存播放时钟及解码统计。[站点适配与验收](site-verification.md)

## Java 引擎与安装包

本地爬虫需要 Java 17 或更高版本。运行时优先使用应用目录下的 `runtime/java` 和 `runtime/catvod-engine.jar`，开发目录可运行 `engine/catvod/target/catvod-engine-1.0.0.jar`。修改 Java 源码后，把 `JAVA_HOME` 指向 JDK 17，再运行 Maven。

```powershell
mvn -o -q -DskipTests -f engine/catvod/pom.xml package
```

本机已有 Maven 依赖和 WebView2、VC++ 分发先决文件时，可使用 `scripts/build-installer.ps1 -MavenOffline`。打包还需要 Inno Setup 6 和带 `jlink` 的 JDK。脚本会补齐 WinUI 的 `*.xbf`、`*.pri`，将应用内容暂存在 `artifacts/publish-<id>`，然后在发布目录生成 `TV-Setup-<version>-x64.exe` 和 SHA-256 文件。版本来自 `Directory.Build.props`。

工作区根目录和正式安装器目录应使用普通 Medium 完整性。默认输出已经固定在项目内，不需要也不应改到 Downloads：

```powershell
$env:TEMP = (Get-Item 'artifacts/tmp').FullName
$env:TMP = $env:TEMP
& .\scripts\build-installer.ps1 -MavenOffline -NoRestore
```

`-NoRestore` 适用于已还原 `win-x64` 构建依赖、本次未更改依赖的工作区。

脚本在编译前检查发布目录，并在交付前检查新 EXE 的文件完整性级别。默认发布目录为 `artifacts/installers`，该目录显式使用 Medium 完整性并允许沙箱用户组读取和执行，其他 agent 可直接从该目录打开安装器。Windows 会按启动用户和可执行文件中较低的完整性级别启动进程，所以从资源管理器双击带低完整性标记的文件也会限制安装。可用 `icacls "<安装包绝对路径>"` 核对文件标记。[Windows 强制完整性控制](https://learn.microsoft.com/windows/win32/secauthz/mandatory-integrity-control)

### 安装错误 5 与覆盖升级

遇到「拒绝访问」时，先从安装日志定位失败路径。默认日志名为 `Setup Log *.txt`，保存在安装器启动器创建的 `tv-setup-*` 临时目录下。需要固定日志位置时可传入 `/LOG="绝对路径"`。

- 失败路径为临时目录下的 `is-*.tmp` 时，检查安装器所用的 `TEMP` 和 `TMP`。启动器会选择可写位置，并为 Inno 子进程设置私有临时目录。
- 失败路径为已有安装目录下的 `unins000.exe`，且提示在目标目录创建文件失败时，检查**安装进程本身**能否在该目录创建文件。新版在准备安装时创建一个关闭即删除的文件，遇到系统拒绝立即停止，并记录目标路径及 Win32 错误码。
- 覆盖升级使用普通发布目录里的 EXE，沿用安装程序识别出的旧路径。2026-11-21 的日志记录了工作区发布的 EXE 在资源管理器启动后返回错误 5。本机对照测试中，旧发布文件和启动探针继承工作区的低完整性标记，换用普通目录生成的探针后，进程以中等完整性运行，可以写入同一目标目录。[资源管理器失败日志](../artifacts/qa-site-fidelity-20261121/installer-destination/explorer-preflight-failure.log) · [旧目录探针记录](../artifacts/qa-site-fidelity-20261121/installer-destination/latest-context-approved.log) · [普通目录探针记录](../artifacts/qa-site-fidelity-20261121/installer-destination/standard-output-probe.log)

后台安装回归使用 `/VERYSILENT /SUPPRESSMSGBOXES /SP- /NORESTART /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS` 和独立路径。验证文件覆盖时可用 `/NOICONS /MERGETASKS=!desktopicon` 跳过快捷方式。用户名下的卸载注册项、快捷方式和卸载流程使用独立 `AppId`、安装目录与开始菜单组测试。检查退出码的同时读安装日志，确认写入过程中没有被静默忽略的异常。覆盖测试还需确认日志追加到旧版 `unins000.dat`，并对比发布文件与安装文件的哈希。[Inno Setup 命令行参数](https://jrsoftware.org/inno-setup/documentation/topic_setupcmdline.htm)

2026-11-21 在普通用户上下文验证了独立注册实例的全新安装、从 1.1.8 覆盖升级、临时目录回退、开始菜单快捷方式和卸载。测试还核对了 965 个应用文件的哈希。[完整安装回归](../artifacts/qa-site-fidelity-20261121/installer-destination/standard-registered-results.log)

正式发布包也已在现用目录完成 1.1.8 到 1.2.0 覆盖升级。安装前备份了旧程序、卸载登记和快捷方式。安装后核对了 965 个应用文件、卸载记录、两条开始菜单快捷方式和用户数据库哈希。已安装的 Java 17 与 CatVod 桥接用例通过。[原位升级结果](../artifacts/qa-site-fidelity-20261121/installer-destination/actual-release-results.log) · [发布包哈希与完整性标记](../artifacts/qa-site-fidelity-20261121/installer-destination/standard-release-manifest.csv) · [已安装的 Java 验证](../artifacts/qa-site-fidelity-20261121/installer-destination/actual-installed-java.log)

### Windows 发布者提示

当前打包脚本生成未签名的安装包。`AppPublisher` 填写安装元数据，Windows 通过 Authenticode 签名验证发布者。现存 1.1.0–1.1.9 测试包和 1.1.2–1.1.9 发布包均验证为 `NotSigned`。如果发布流程配置了可信代码签名，先签名，再校验签名并生成最终 SHA-256。[本机签名核对记录](../artifacts/qa-site-fidelity-20261121/installer-signatures.csv) · [微软 Authenticode 检查文档](https://learn.microsoft.com/powershell/module/microsoft.powershell.security/get-authenticodesignature)

## 测试

先看[站点验收](site-verification.md)的命令和结果说明。桌面回归会激活窗口并发送按键，安排在无人使用当前桌面时串行运行。后台站点探测可以独立执行，不发送键鼠输入。
