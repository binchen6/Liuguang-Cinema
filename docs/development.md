# 开发指南

## 1. 环境与构建

项目基于 **C#、WinUI 3、Windows App SDK 1.8、.NET 10 与 LibVLC**，目标为 Windows x64 非打包桌面应用。完整打包另需 JDK 17+（推荐 Microsoft Build of OpenJDK 21）、Maven 与 Inno Setup 6。

```powershell
New-Item -ItemType Directory -Force 'artifacts/tmp' | Out-Null
$env:TMP = $env:TEMP = (Resolve-Path 'artifacts/tmp').Path
.\.dotnet\dotnet.exe restore src/TV/TV.csproj --locked-mode
.\.dotnet\dotnet.exe build src/TV/TV.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 --no-restore
```

构建产物位于 `src/TV/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/TV.exe`（使用系统 SDK 可将 `.\.dotnet\dotnet.exe` 换为 `dotnet`）。

## 2. 隔离运行与数据目录

开发与测试必须通过 `TV_DATA_ROOT` 隔离用户数据：

```powershell
$env:TV_DATA_ROOT = Join-Path $PWD 'artifacts/tmp/dev-profile'
& .\src\TV\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\TV.exe
```

未设置时默认写入 `%LOCALAPPDATA%\TV`：

| 路径 | 说明 |
| --- | --- |
| `tv.db` | 设置、历史、收藏、下载、本地媒体索引及 DPAPI 加密凭据 |
| `config/last-good.json` | 最近一次成功的配置缓存 |
| `packs/` | 已下载的仓库资源包与清单缓存（支持按包热重载/卸载） |
| `spider/jar-cache/` | 外部 Android DEX 实时转译的 JVM 字节码缓存 |
| `cache/` | 图片、字幕与临时媒体缓存 |
| `diagnostic.log`、`last-error.log` | 轮转诊断日志（上限 2 MB，已自动脱敏）与未处理异常记录 |

> 提交 Issue 日志前请复查并移除私人路径与媒体地址，切勿上传 `tv.db` 或真实凭据。

## 3. 代码结构

| 路径 | 职责 |
| --- | --- |
| `src/TV/MainWindow.xaml(.cs)` | 窗口壳、光学磨砂侧栏、返回栈与播放页进出 |
| `src/TV/Styles/`、`Controls/` | 设计令牌、24×24 图标库、`GlassSurface`、`GlassOpticsEngine`、`NavigationMotion`、`ScrollWheelBehavior`、弹幕层 |
| `src/TV/Views/`、`ViewModels/` | 页面 XAML（含 `LibraryPage` 自适应宽屏历史网格、`SettingsPage` 自定义命名与资源包管理）、状态绑定与 `MotionService` |
| `src/TV/Models/` | 媒体、站点、多仓库、资源包、网盘与持久化模型 |
| `src/TV/Services/Vod/`、`Spider/` | `VodClientRouter` 三层调度、22 个原生站点适配器、`UniversalProtocolVodProvider`、`UniversalAutoHtmlVodProvider`、`JsSpiderClient`（`drpy2 js:` + `CryptoJS.AES` + T3）、`PySpiderClient` 与 Java IPC |
| `src/TV/Services/Packs/` | `RepoPackService` / `RepoPackVodProvider`：独立 GitHub 资源包清单解析、自动匹配安装、SHA-256 与 Zip-Slip 校验、热重载 |
| `src/TV/Services/Http/`、`Imaging/` | `GitHubMirrorService`（5 条国内镜像加速 + 流式 SHA-256）、自定义 DNS、跨域请求头隔离与 HTTP/2 图片池 |
| `src/TV/Services/Playback/` | `PlaybackSeekCoordinator` 无回弹状态机、LibVLC 优先级控制泵、字幕、弹幕匹配（`MediaMatching`）、防休眠与离线下载 |
| `src/TV/Services/Cloud/` | 四网盘接口、纯 C# secp256k1 阿里设备签名、跳转感知 `CloudBlockCache` 与回环安全媒体代理 |
| `src/TV/Services/AI/` | 阿里云百炼字幕生成、双语翻译、无头音轨提取与 WASAPI 进程环回实时字幕 |
| `src/TV/Services/Live/`、`Casting/` | 直播源聚合、EPG 节目单、DLNA/UPnP 与手机扫码投屏接收 |
| `src/TV/Services/Library/`、`Metadata/` | 本地媒体索引、侧车字幕/弹幕、目录监视与豆瓣公开元数据 |
| `src/TV/Services/Persistence/`、`Maintenance/` | SQLite（8 MB 页缓存）、Windows DPAPI `SecretStore` 与分类安全清理 |
| `src/TV/Services/Updates/` | GitHub Release 检查、复用 `GitHubMirrorService` 多镜分片下载与双向差异增量升级 |
| `engine/catvod/` | Java 17/21 CatVod JSON IPC 引擎、`ExternalJarLoader`（DEX 转译 + 5 条国内镜像）与 Android 兼容层 |
| `installer/`、`scripts/` | Inno Setup 完整/增量安装脚本、打包签名、启动测速、84 项协议回归与后台站点探测 |

## 4. 安全与仓库资源包约定

- **零明文凭据入仓**：严禁提交 API Key、AppSecret、Cookie、Token 或本机绝对路径；网盘凭据、百炼 API Key 与自定义弹幕凭证统一经 Windows DPAPI 落库，`Diagnostics.Log` 自动脱敏敏感字段。
- **仓库资源包安全（`binchen6/Liuguang-RepoPacks`）**：
  - `RepoPackService` 从配置的 HTTPS 来源读取清单。ZIP 可通过国内镜像下载，安装前按清单校验 SHA-256、资源包标识和版本。
  - 解压前由 `ExtractZipSafely` 严格拦截 Zip-Slip 路径穿越，校验通过后通过临时 staging 目录原子切换并热重载；
  - 资源包仅允许声明式规则（`alias` / `js` / `cms` / `bili-preset` / `info`），不执行任意原生二进制，不破解闭源 DRM / `.so`。
- **请求与解码守卫**：页面异步任务必带取消令牌与代际守卫；LibVLC 仅经 `PlayerService` 串行队列访问；跨域重定向经 `MediaRequestHeaders.ForTarget` 剥离敏感头；本地代理仅监听 `127.0.0.1`；XML 禁用 DTD；本地清理绝不删除未勾选磁盘文件。
- **UI 边界**：修改界面须遵循 [视觉与材质规范](brand-design-system.md)、[设计契约](design-guide.md) 与 [动效工程规范](motion-spec.md)，保留全部 UIA 名称。

## 5. Java 引擎与安装器打包

```powershell
# 仅修改 Java 引擎后重新编译（需 JDK 17+，指定 JAVA_HOME）
mvn -o -q -DskipTests -f engine/catvod/pom.xml package

# 构建完整安装器、便携包与文件级双向增量包（输出至 artifacts/installers/）
$env:TMP = $env:TEMP = (Resolve-Path 'artifacts/tmp').Path
& .\scripts\build-installer.ps1 -MavenOffline -NoRestore
```

打包核心机制（版本取自 `Directory.Build.props`）：
1. **双向差异增量包**：对比上一版便携包逐文件 SHA-256，仅打包变更项并自动生成 `[InstallDelete]`，升级后与完整包逐字节一致。
2. **高签名覆盖与免提权**：`jlink` 后还原微软官方签名 OpenJDK PE 文件并附带 6 个微软签名本地 VC++ CRT DLL（免 UAC，98.4% 已签名）。
3. **双层 Authenticode 签名与启动耗时守卫**：`installer/TV.iss` 必须保持 `InternalCompressLevel=none`、`SolidCompression=no` 且不嵌套外部 `.exe`，发版前执行：

```powershell
pwsh -NoProfile -File .\scripts\Test-InstallerLaunchSpeed.ps1 -InstallerPath .\artifacts\installers\TV-Setup-<version>-x64.exe -AssertIssSettings -Runs 3 -MaxColdMs 1500 -MaxWarmMs 600
```

## 6. 回归与验收

```powershell
# 84 项 C# 离线协议、三层路由、JS/AES、镜像与资源包回归
pwsh -NoProfile -File ./scripts/run-release-regressions.ps1

# 后台多站探测（支持 -ImportUrl 与 -DecodeSample，不抢焦点）
pwsh -NoProfile -File ./scripts/run-site-probe.ps1 -OutputDirectory 'artifacts/probe-current'

# 桌面 UIA 回归（须在空闲桌面串行运行）
& .\scripts\run-site-regressions.ps1 -Python (Get-Command py).Source -OutputDirectory 'artifacts/qa-current'
```

## 7. 弹弹play 签名中继（功能模块）

默认在线弹幕功能的签名中继源码位于 `server/dandanplay-relay/`（核心 `relay-core.mjs`），仅转发对 `api.dandanplay.net` 的剧集搜索、文件匹配与弹幕查询，不泄露签名头至第三方域名：
- 支持阿里云 ESA Pages、腾讯云 EdgeOne Pages、Laf、自建 Node.js 与 Cloudflare Workers 兜底，通过 `X-Relay-Delivery: direct` 让客户端直连 `cas2.dandanplay.net` 下载弹幕主体；
- 环境变量 `DANDANPLAY_RELAY_URL` 支持英文逗号分隔多节点自动故障转移，单测运行 `node --test server/dandanplay-relay/worker.test.mjs`，实网验证可附带 `run-release-regressions.ps1 -VerifyDefaultService`。
