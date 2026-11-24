# 开发指南

## 1. 环境与构建

项目基于 **C#、WinUI 3、Windows App SDK 1.8、.NET 10 与 LibVLC**，目标为 Windows x64 非打包桌面应用。完整打包还需要 JDK 17+、Maven 与 Inno Setup 6。

```powershell
New-Item -ItemType Directory -Force 'artifacts/tmp' | Out-Null
$env:TMP = $env:TEMP = (Resolve-Path 'artifacts/tmp').Path
.\.dotnet\dotnet.exe restore src/TV/TV.csproj --locked-mode
.\.dotnet\dotnet.exe build src/TV/TV.csproj -c Release --no-restore
```

开发产物位于 `src/TV/bin/Release/net10.0-windows10.0.19041.0/win-x64/TV.exe`（若使用系统 .NET 10 SDK，可直接将 `.\.dotnet\dotnet.exe` 替换为 `dotnet`）。

## 2. 隔离运行与数据目录

开发与测试必须通过 `TV_DATA_ROOT` 隔离数据，避免污染正式用户配置：

```powershell
$env:TV_DATA_ROOT = Join-Path $PWD 'artifacts/tmp/dev-profile'
& .\src\TV\bin\Release\net10.0-windows10.0.19041.0\win-x64\TV.exe
```

未设置时默认写入 `%LOCALAPPDATA%\TV`：

| 路径 | 说明 |
| --- | --- |
| `tv.db` | 设置、历史、收藏、下载、本地媒体索引及 DPAPI 加密凭据 |
| `config/last-good.json` | 最近成功的配置缓存 |
| `cache` | 图片、字幕与临时媒体缓存 |
| `diagnostic.log`、`diagnostic.log.1` | 轮转诊断日志（单份上限 2 MB，已自动脱敏） |
| `last-error.log` | 未处理异常记录 |

> 提交 Issue 日志前仍请检查并移除私人路径与媒体地址，切勿上传 `tv.db` 或真实凭据。

## 3. 代码结构

| 路径 | 职责 |
| --- | --- |
| `src/TV/MainWindow.xaml(.cs)` | 窗口壳、磨砂导航栏、返回栈、播放页进出与退出顺序 |
| `src/TV/Styles/`、`Controls/` | 「白 · 隙 · 镜」设计令牌、24×24 几何图标库、`BrandMark`、`LineIcon`、弹幕层与复用弹窗 |
| `src/TV/Views/`、`ViewModels/` | 页面 XAML、绑定状态、命令与 `MotionService` GPU 物理动效引擎 |
| `src/TV/Models/` | 媒体、站点配置、网盘、播放解析与持久化模型 |
| `src/TV/Services/AI/` | 阿里云百炼 AI 字幕生成（`paraformer-v2` / `qwen3-asr-flash-filetrans`）、翻译（`qwen-mt-flash` / `qwen-mt-plus`）、无头音轨提取与 WASAPI 实时字幕 |
| `src/TV/Services/Playback/` | 地址解析、HLS/DASH、LibVLC 优先级控制泵、跨源弹幕规范化匹配（`MediaMatching` + 弹弹play/B 站）、字幕与离线下载 |
| `src/TV/Services/Cloud/` | 四网盘账号/文件接口、纯 C# secp256k1 阿里设备签名、分块预读与回环安全媒体代理 |
| `src/TV/Services/Vod/`、`Spider/` | CMS、20+ 原生站点适配器、跨站搜索评分、换源恢复与 Java CatVod JSON IPC |
| `src/TV/Services/Live/`、`Casting/` | 直播源聚合、EPG 节目单、DLNA/UPnP 与扫码投屏接收 |
| `src/TV/Services/Library/`、`Metadata/` | 本地媒体索引、侧车字幕/弹幕、目录监视与豆瓣公开元数据 |
| `src/TV/Services/Http/`、`Imaging/` | 自定义 DNS 解析、跨域请求头过滤、高速 HTTP/2 图片连接池与有界内存/磁盘缓存 |
| `src/TV/Services/Persistence/`、`Maintenance/` | SQLite（8 MB 页缓存）、Windows DPAPI `SecretStore`、分类扫描与显式安全清理 |
| `src/TV/Services/Updates/` | GitHub 正式版检查、限流网页兜底、国内多镜并行测速、多路 Range 断点下载与 SHA-256 校验 |
| `engine/catvod/` | Java 17 CatVod JSON IPC 引擎与上游站点修正 |
| `installer/`、`scripts/` | Inno Setup 完整/增量安装器（含中文语言包）、构建签名与秒开防回退脚本 |
| `artifacts/qa-fixture/` | 隔离测试夹具、协议回归与桌面 UIA 回归脚本 |

## 4. 密钥与开源安全约定

- **仓库严禁提交任何明文 API Key、AppSecret、Cookie、Token 或本机绝对路径**。
- **弹弹play 开放弹幕库凭证**：本地构建可复制 `src/TV/Secrets.Local.props.example` 为 `src/TV/Secrets.Local.props`（已加入 `.gitignore`），或通过环境变量 `DANDANPLAY_APP_ID` / `DANDANPLAY_APP_SECRET_1` / `DANDANPLAY_APP_SECRET_2` 注入；终端用户也可在「设置 → 弹幕与 AI」配置自定义凭证（DPAPI 加密落库）。
- **阿里云百炼 API Key**：不随源码或安装包内置，由用户在设置页跳转百炼控制台自助生成后填入，仅以当前 Windows 用户 DPAPI 加密存储于 `tv.db`。
- **日志脱敏**：`Diagnostics.Log` 自动掩码 `sk-*`、`Bearer *`、`X-Signature`、`AppSecret`、`Cookie`、`token` 等敏感字段。

## 5. 工程实现守卫

- 页面异步任务须带取消令牌与代际守卫，过期结果绝不回写新页面、新选集或新来源。
- LibVLC 对象仅经 `PlayerService` 后台串行路径访问；离页先停播并释放交换链再销毁视图。
- 所有代理媒体、音轨与字幕句柄均需明确所有权并在替换、取消与离页时释放。
- 跨域重定向、分片与下载须通过 `MediaRequestHeaders.ForTarget` 剥离敏感头，严禁将原站 Cookie/Authorization 泄露至第三方 CDN。
- 网盘凭据统一走 `SecretStore`（DPAPI）；本地媒体代理仅监听 `127.0.0.1`。
- XML 输入禁用 DTD 并限制体积；JSON、图片、播放列表与下载均须边界校验。
- UI 修改遵循 [品牌设计语言规范](brand-design-system.md)、[设计契约](design-guide.md) 与 [动效工程规范](motion-spec.md)，保留全部 UIA 名称。
- 本地媒体索引移除与清理功能绝不删除用户未显式勾选的磁盘原文件。

## 6. Java 引擎与安装器打包

JDK 优先使用 `JAVA_HOME`；安装器简体中文界面固定使用仓库内 `installer/Languages/ChineseSimplified.isl`。

```powershell
# 仅修改 Java 引擎后重新编译
mvn -o -q -DskipTests -f engine/catvod/pom.xml package

# 构建完整安装器、便携包与文件级增量更新器（本机已有缓存可加 -MavenOffline -NoRestore）
$env:TMP = $env:TEMP = (Resolve-Path 'artifacts/tmp').Path
& .\scripts\build-installer.ps1 -MavenOffline -NoRestore
```

统一产物位于 `artifacts/installers/`：
- `TV-Setup-<version>-x64.exe`（及 `.sha256`）
- `TV-<version>-win-x64-portable.zip`（及 `.sha256`）
- `TV-Update-<version>-from-<base>-x64.exe`（及 `.sha256`）

打包脚本关键机制（版本取自 `Directory.Build.props`）：
1. **双向差异增量包**：对比上一版便携包（或 `-IncrementalBaseZip`）逐文件 SHA-256，仅打包变更/新增文件，并自动生成 `[InstallDelete]` 清理废弃文件与空目录；`TV-Update.iss` 同步更新文件关联、快捷方式、卸载元数据、等待进程退出并通过 `SHChangeNotify` 刷新系统图标缓存，升级后目录与完整版 100% 逐字节一致。
2. **高签名覆盖与免提权运行库**：`jlink` 后从 Microsoft Build of OpenJDK 还原全部 Microsoft 签名 PE 文件，并内置 6 个 Microsoft 签名应用本地 VC++ x64 CRT DLL（免嵌套 `vc_redist.x64.exe` 与 UAC）。
3. **双层 Authenticode 签名**：支持 `-CertThumbprint` / `-PfxPath`（或环境变量 `TV_SIGN_THUMBPRINT` / `TV_SIGN_PFX` / `TV_SIGN_PASSWORD`），详见 [智能应用控制与签名说明](smart-app-control.md)。
4. **秒开防回退守卫**：`installer/TV.iss` 必须保持 `InternalCompressLevel=none`、`SolidCompression=no` 且不嵌套外部 `.exe`。发版前必须通过启动耗时校验：

```powershell
pwsh -NoProfile -File .\scripts\Test-InstallerLaunchSpeed.ps1 -InstallerPath .\artifacts\installers\TV-Setup-<version>-x64.exe -AssertIssSettings -Runs 3 -MaxColdMs 1500 -MaxWarmMs 600
```

## 7. 回归与验收

提交改动前至少运行 Release 编译及对应模块的协议回归；完整桌面回归：

```powershell
& .\scripts\run-site-regressions.ps1 -Python (Get-Command py).Source -OutputDirectory 'artifacts/qa-current'
```

传统桌面脚本会激活窗口，须在空闲桌面串行运行；布局与后台检查优先采用无全局键鼠输入的 UIA 模式。站点探测命令与验收层级见 [站点适配与验收](site-verification.md)。
