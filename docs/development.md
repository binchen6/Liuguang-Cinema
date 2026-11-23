# 开发指南

## 环境与构建

项目使用 C#、WinUI 3、Windows App SDK 1.8、.NET 10 和 LibVLC，目标为 Windows x64 非打包应用。完整构建还需要 JDK 17+、Maven 和 Inno Setup 6。

在工作区根目录执行：

```powershell
New-Item -ItemType Directory -Force 'artifacts/tmp' | Out-Null
$env:TMP = $env:TEMP = (Resolve-Path 'artifacts/tmp').Path
.\.dotnet\dotnet.exe restore src/TV/TV.csproj --locked-mode
.\.dotnet\dotnet.exe build src/TV/TV.csproj -c Release --no-restore
```

开发输出位于 `src/TV/bin/Release/net10.0-windows10.0.19041.0/win-x64/TV.exe`。若使用系统 .NET 10 SDK，也可将上述路径替换为 `dotnet`。

## 隔离运行与诊断

测试必须设置独立数据目录，避免读写正式配置：

```powershell
$env:TV_DATA_ROOT = Join-Path $PWD 'artifacts/tmp/dev-profile'
& .\src\TV\bin\Release\net10.0-windows10.0.19041.0\win-x64\TV.exe
```

未设置时使用 `%LOCALAPPDATA%\TV`。主要文件如下：

| 路径 | 内容 |
| --- | --- |
| `tv.db` | 设置、历史、收藏、下载、本地媒体索引和加密凭据 |
| `config/last-good.json` | 最近成功的内置配置缓存 |
| `cache` | 图片、字幕和临时媒体缓存 |
| `diagnostic.log`、`diagnostic.log.1` | 当前及上一份轮转诊断日志，单份上限 2 MB |
| `last-error.log` | 未处理异常 |

向 Issue 提交日志前应去除账号、Cookie、Token、私人路径和媒体地址，不要上传原始数据库。

## 代码结构

| 路径 | 职责 |
| --- | --- |
| `src/TV/MainWindow.xaml(.cs)` | 窗口、导航、返回栈、播放页进出和退出顺序 |
| `Views`、`ViewModels` | 页面布局、绑定、异步状态和命令 |
| `Services/AI` | 阿里云百炼 AI 字幕生成（`paraformer-v2` / `qwen3-asr-flash-filetrans`）、翻译（`qwen-mt-flash` / `qwen-mt-plus`）、字级时间戳对齐、LibVLC 无头音轨提取与 WASAPI 实时字幕 |
| `Services/Playback` | 地址解析、HLS/DASH、LibVLC、弹弹play/B 站弹幕、字幕与下载 |
| `Services/Cloud` | 四网盘、登录协调、个人目录与本地媒体代理 |
| `Services/Vod`、`Services/Spider` | CMS、原生站点与 Java CatVod IPC |
| `Services/Live` | 直播列表、频道聚合与 EPG |
| `Services/Library` | 本地文件索引、侧车资源和目录监视 |
| `Services/Persistence` | SQLite、DPAPI 与应用路径 |
| `Services/Imaging` | 有界图片解码（后台线程全异步）、磁盘缓存和内存 LRU |
| `Services/Updates` | GitHub 正式版检查、增量包选择、断点下载与 SHA-256 校验 |
| `Services/Maintenance` | 分类扫描与显式安全清理 |
| `engine/catvod` | Java 17 CatVod JSON IPC 引擎 |
| `artifacts/qa-fixture` | 隔离夹具、协议与桌面回归源码 |

## 密钥与开源安全配置

- **开源仓库严禁提交任何明文 API Key、AppSecret、Cookie、Token 或本机绝对路径**。
- **弹弹play 开放弹幕库凭证**：
  - 开发者可复制 `src/TV/Secrets.Local.props.example` 为 `src/TV/Secrets.Local.props`（已加入 `.gitignore`），或通过环境变量 `DANDANPLAY_APP_ID`、`DANDANPLAY_APP_SECRET_1`、`DANDANPLAY_APP_SECRET_2` 在构建时注入（支持 `b64:` 前缀编码）；
  - 用户也可直接在应用「设置 → 弹幕与 AI」填入自定义 AppId 与 AppSecret（经 Windows DPAPI 加密保存于 `tv.db`）。
- **阿里云百炼 API Key**：不在源码或安装包中内置，由用户在「设置 → 弹幕与 AI」点击跳转百炼控制台自助生成后填入，应用仅使用当前 Windows 用户的 DPAPI（`SecretStore`）加密保存在本地数据库。
- **日志脱敏**：`Diagnostics.Log` 自动对 `sk-*`、`Bearer *`、`X-Signature`、`AppSecret`、`Cookie`、`token` 等敏感片段做掩码处理。

## 实现约定

- 页面异步操作需要取消和过期结果保护；旧请求不能覆盖新页面、新选集或新来源。
- LibVLC 对象只通过播放服务的后台串行路径访问。离开播放页先停播并释放交换链，再销毁页面。
- 所有代理媒体、音轨和字幕地址都要有明确所有者，并在替换、取消和离页时释放。
- 跨域重定向、分片、字幕和下载使用 `MediaRequestHeaders.ForTarget` 限制敏感请求头；禁止把原站 Cookie 或 Authorization 带到其他源。
- 网盘凭据只通过 `SecretStore` 使用当前 Windows 用户 DPAPI 加密。媒体代理仅监听回环地址。
- XML 输入禁止 DTD，并设置大小上限；图片、JSON、播放列表和下载同样需要边界检查。
- UI 遵循[设计契约](design-guide.md)，保留现有 UIA 名称、键盘操作、深浅主题和减少动画设置。
- 本地媒体与清理操作不得因索引、移除记录或清理缓存而删除未明确选择的用户原文件。

## Java 与安装包

修改 Java 引擎后：

```powershell
mvn -o -q -DskipTests -f engine/catvod/pom.xml package
```

本机已有依赖时构建正式安装器：

```powershell
$env:TMP = $env:TEMP = (Resolve-Path 'artifacts/tmp').Path
& .\scripts\build-installer.ps1 -MavenOffline -NoRestore
```

安装器与便携包统一输出到：

```text
artifacts/installers/TV-Setup-<version>-x64.exe
artifacts/installers/TV-Setup-<version>-x64.exe.sha256
artifacts/installers/TV-<version>-win-x64-portable.zip
artifacts/installers/TV-<version>-win-x64-portable.zip.sha256
artifacts/installers/TV-Update-<version>-from-<base>-x64.exe
artifacts/installers/TV-Update-<version>-from-<base>-x64.exe.sha256
```

存在上一版便携包时，脚本逐文件比较 SHA-256 并生成仅含新增/变化文件的增量安装器；可通过 `-IncrementalBaseZip <上一版便携包路径>` 指定基线。补丁使用相同 AppId 和安装目录，但不改写卸载记录。应用只从固定 GitHub 仓库的正式 Release 选择精确版本资产，增量包不可用时回退完整包；所有下载均须通过 SHA-256 校验。

版本来自 `Directory.Build.props`。发布脚本会自动：
1. 生成裁剪后的 Java 运行时，并从 Microsoft Build of OpenJDK 还原全部 Microsoft 签名的 `.exe` / `.dll`；
2. 随应用内置 6 个 Microsoft 签名应用本地 VC++ x64 CRT DLL，免去嵌套 `vc_redist.x64.exe` 与 UAC 提权；
3. 支持通过 `-CertThumbprint` / `-PfxPath`（或环境变量 `TV_SIGN_THUMBPRINT` / `TV_SIGN_PFX` / `TV_SIGN_PASSWORD`）执行双层 Authenticode 签名（内部未签名 PE + 卸载器 + 安装包，详见[智能应用控制与签名说明](smart-app-control.md)）；
4. 检查输出目录及 EXE 为 Medium 完整性。`artifacts/installers` 还允许沙箱用户组读取和执行，不要改到 Downloads。

### 安装包启动速度防回退守卫

`installer/TV.iss` 必须始终保持 `InternalCompressLevel=none`、`SolidCompression=no` 且不嵌套外部 `.exe` 安装器。每次发版前必须执行启动耗时基准检验，确保安装包秒开且后续版本不回退：

```powershell
pwsh -NoProfile -File .\scripts\Test-InstallerLaunchSpeed.ps1 -InstallerPath .\artifacts\installers\TV-Setup-<version>-x64.exe -AssertIssSettings -Runs 3 -MaxColdMs 1500 -MaxWarmMs 600
```

## 测试

至少运行与改动相关的协议测试和 Release 构建。完整桌面回归：

```powershell
& .\scripts\run-site-regressions.ps1 -Python (Get-Command py).Source -OutputDirectory 'artifacts/qa-current'
```

传统桌面脚本可能激活窗口并发送键鼠，应串行且在空闲桌面运行。只审查布局或做后台验证时优先使用 UIA 直接操作模式，不使用全局输入。站点探测和通过标准见[站点适配与验收](site-verification.md)。
