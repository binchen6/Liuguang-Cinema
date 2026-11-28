# 仓库与站点适配规范

适用于流光影院的 TVBox 仓库、点播站点和单独适配包。适配方法按 **原生 > 通用 > 单独适配包** 选择。提交时应写明采用哪一层、命中条件，以及用哪些样本核验了播放。

## 1. 选择适配方法

| 优先级 | 何时采用 | 实现方式 |
| --- | --- | --- |
| **原生** | 已有 C# 适配器可接入，或多个仓库共用同一站点的稳定接口，且需要独立处理认证、详情、分集、画质或播放 | 根据稳定的 `api`、站点域名或 `ext` 结构匹配。实现 `INativeVodProvider`，需要解析播放时实现 `INativePlaybackProvider`。按实需实现分类筛选和清晰度接口，在 `NativeVodProviderRouter.CreateDefault` 注册，并核对更宽泛的匹配器是否会提前接管。 |
| **通用** | 接口、协议或 HTML 结构可覆盖一类站点 | 优先直接配置标准 CMS JSON/XML 或 HTTP 爬虫。复用 `UniversalProtocolVodProvider` 已接入的协议和 `UniversalAutoHtmlVodProvider` 的通用页面识别。drpy2 或 CatVod JS 通过 Jint 执行。Python 脚本可使用本机 Python 3 或随包分发的嵌入版。Java CatVod 或 DEX/JAR 适配复用桌面 Java 运行时。 |
| **单独适配包** | 差异稳定地局限于某个仓库或少数站点，现有原生和通用实现经过实测仍缺少相应规则 | 通过 `RepoPackService` 发布声明式配置或一个随包 JavaScript 文件。通用解析器的问题应修在通用层，仓库特有的选择器、字段映射和说明才放入包中。 |

原生与通用层都应复用 `SourceHttpClient`、现有媒体模型和播放解析入口。沿请求链传递取消令牌。首页、分类和搜索返回稳定的站点标识及条目 ID，详情保留真实线路和分集，播放结果保留所需请求头、字幕和弹幕。认证数据来自本机已有的账号会话。

**现行运行时行为**。`NativeVodProviderRouter` 按注册顺序先检查站点专用适配，再检查通用协议。随后依次处理 JS、Python、显式覆盖规则、通用 HTML、资源包和 Java CatVod 或 CMS。通用 HTML 确认接口或页面形态不匹配时可使用已经安装且明确命中的资源包。超时、站点验证和账号限制保留相应状态。

`overrideUniversal` 默认为 `false`。只有在已验证 `UniversalProtocolVodProvider` 或通用 HTML 会将该站点错识别为另一种站型时，才可为一个明确的站点 `key` 设置为 `true`，并在包说明中附复现和验证结果。专用原生适配器和已识别的 JS/Python 脚本按自身入口处理。

实现入口见 [原生接口与注册](../src/TV/Services/Vod/Native/INativeVodProvider.cs)、[点播路由](../src/TV/Services/Vod/VodClientRouter.cs)。

## 2. 仓库配置

- 单仓使用 TVBox 配置对象，点播条目放在 `sites`，直播源放在 `lives`。多仓入口使用 `urls` 数组，每个子仓填写 `name` 和 `url`。每个仓库应单独导入验证。
- 每个点播站点提供稳定的 `key`、可读的 `name`、与接口协议相符的 `type` 和 `api`。同一仓库内的 `key` 必须唯一。需要专属规则时使用仓库前缀，例如 `example_archive`，避免多个仓库之间重名。
- `ext` 按对应适配器的输入填写。公开的 `api`、`jar`、`ext`、`lives`、`parses` 资源优先使用 HTTPS。使用相对地址时，以所在配置文件的地址验证实际解析结果。
- 按站点的真实能力填写 `searchable`、`quickSearch`、`filterable` 等开关。工具入口或受限站点用说明展示。共享配置中使用公开接口、去标识化样例和必要的静态请求头。
- 使用通用 HTML 时，可在 `ext.site` 填写站点地址数组。按照首选到备用的顺序排列，保留实际子目录、端口和固定查询参数。站点提供标准 CMS 接口时，直接填写该接口。HTML 页面使用同源 GET 搜索表单及可访问的下一页链接时，客户端可识别实际搜索和分页地址。
- 文件浏览、网盘市场和推送接收使用对应的操作入口。音频站点应返回实际音频地址或音频 Content-Type。需要随地址返回歌词或封面时，爬虫播放响应可使用 `lyrics`、`lrc`、`cover` 字段。
- Python 脚本依赖外部模块时，使用已装好模块的 Python 3。可通过 `TV_PYTHON_EXECUTABLE` 指定解释器绝对路径。缺少 WebView2 时，安装包可按需启动微软签名的在线引导程序。

下面是一个使用标准 CMS 的单仓配置。换成实际接口后可直接导入。

```json
{
  "sites": [
    {
      "key": "example_cms",
      "name": "示例片库",
      "type": 1,
      "api": "https://repo.example.org/api.php/provide/vod/",
      "searchable": 1,
      "quickSearch": 1,
      "filterable": 0
    }
  ]
}
```

## 3. 单独适配包

### 文件与版本

包使用 ZIP，根目录必须直接包含 UTF-8 的 `pack.json`。包内脚本使用相对路径，其他文件可放说明或许可。独立资源包仓库的 `manifest.json` 负责索引，发布索引里的 `downloadUrl` 指向该版本的 ZIP。

```text
manifest.json                       # 资源包仓库的索引
example-archive-v1.0.0.zip           # 一次发布的包体
  pack.json                         # ZIP 根目录
  scripts/archive.js                # 只有 js 规则需要
  README.md                         # 适用站点、变更、测试与上游出处
  LICENSE                           # 有随包分发的第三方代码时提供相应许可
```

- `id` 在清单与 `pack.json` 中一致。发布约定使用 1–64 个小写 ASCII 字母、数字、连字符或下划线。ID 建立后保持稳定。
- `version` 采用递增的 `主版本.次版本.修订版本`，例如 `1.0.1`。两个文件中的版本一致。包发生变化时递增版本并发布新的 ZIP。
- 清单需符合 `schemaVersion: 1`。填写每项的 `id`、`name`、`version`、`downloadUrl`、`sha256` 及可用的自动匹配条件。`sizeBytes` 和 `siteCount` 可用于展示，填写时与实际 ZIP 大小和规则数量一致。
- 客户端限制清单为 2 MiB，ZIP 下载为 32 MiB，ZIP 声明的解压后总大小为 64 MiB。保持归档内路径为规范的相对路径，采用普通文件与目录，保持引用的文件确实在包内。
- 规则使用公开数据或本机会话。ZIP 的可执行内容仅限客户端 Jint 支持的 JavaScript。原生二进制、JAR、Python、安装脚本及私人凭据属于包外。

### 自动安装与站点分派

客户端依据**清单条目**识别仓库。优先填写精确的 `matchHosts`，填主机名即可。客户端同时接受对应子域名。多个仓库共用一个主机名时，省略 `matchHosts`，改用含主机和稳定路径的 `matchUrlKeywords` 区分。也可用至少两个互不相同的 `matchSiteApis` 作为兜底标识，客户端要求导入的仓库命中至少两项。各包的匹配条件应互斥。

安装后，客户端以 `pack.json` 的 `sites` 分派站点。每条规则至少填写 `key` 或 `api`。优先用 `key` 作精确匹配，需要适配同一个 API 的所有站点时才只填写 `api`。同时填写两者时，`key` 决定是否命中，`api` 可用于客户端标记支持。两个字段均用当前站点的实际值。避免多个已安装包声明重复的 `key` 或广泛的相同 `api`。

| `kind` | 要提供的字段 | 用法 |
| --- | --- | --- |
| `alias` | `aliasApi`，可选 `aliasExt` | 调用应用内已有的适配器。省略 `aliasExt` 会沿用原站 `ext`。 |
| `js` | `scriptFile` | 运行包内相对路径指向的 UTF-8 drpy2 或 CatVod JS。验证涉及的首页、分类、搜索、详情及播放入口，复用应用提供的 Jint 接口。 |
| `cms` | `endpoint`，可选 `typeFilter`、`categories` | 指向 MacCMS JSON/XML 入口。`typeFilter` 指定默认分类，`categories` 可提供 `{ "id": "...", "name": "..." }` 列表。 |
| `bili-preset` | 可选 `categories`、`defaultKeyword` | 为现有 B 站原生适配提供主题分类或默认关键词。 |
| `info` | `title`、`message` | 说明工具入口、账号或桌面兼容限制。验收时以信息展示为准。 |

`preferredFlag` 和包文件中的 `lives` 尚未接入当前播放与直播路由。线路按实际播放响应选择，直播入口配置在仓库的 `lives` 中。

### 示例

此处假设仓库原有站点使用桌面端尚未支持的 `csp_ExampleArchive`，同时有可用的公开 CMS 接口。仓库条目如下。

```json
{
  "key": "example_archive",
  "name": "示例档案馆",
  "type": 3,
  "api": "csp_ExampleArchive"
}
```

`pack.json` 为这一个站点配置 CMS 接口。

```json
{
  "id": "example-archive",
  "name": "示例片库适配包",
  "version": "1.0.0",
  "description": "为该仓库配置独立的 CMS 入口",
  "sites": [
    {
      "key": "example_archive",
      "api": "csp_ExampleArchive",
      "kind": "cms",
      "endpoint": "https://repo.example.org/api.php/provide/vod/",
      "overrideUniversal": false
    }
  ]
}
```

`manifest.json` 对应条目如下。`sha256` 的全零值用于展示长度，发布时替换为实际 ZIP 的 64 位十六进制 SHA-256。

```json
{
  "schemaVersion": 1,
  "packs": [
    {
      "id": "example-archive",
      "name": "示例片库适配包",
      "version": "1.0.0",
      "matchHosts": ["repo.example.org"],
      "downloadUrl": "https://repo.example.org/releases/example-archive-v1.0.0.zip",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
      "siteCount": 1
    }
  ]
}
```

### 创建、上传和验收

1. 在 [适配包仓库](https://github.com/binchen6/Liuguang-RepoPacks) 的 `packs/<id>/` 编写 `pack.json`、脚本和说明。升级沿用 `id` 并提升版本号，写明站点命中范围、公开接口、测试样本和所需会话。
2. 从包目录的内容创建 ZIP，确认 ZIP 根目录直接含有 `pack.json`。在客户端「设置 → 片源 → 仓库资源包 → 导入 ZIP」离线安装，切换目标仓库核对规则是否生效，再测试卸载与恢复。
3. 以 `<id>-v<版本>.zip` 命名，在适配包仓库的对应 GitHub Release 上传。使用公开 HTTPS 下载地址重新下载，核对 SHA-256 和字节数。
4. ZIP 可公开下载后再提交 `manifest.json`，同步 `id`、`version`、`downloadUrl`、`sha256`、`sizeBytes`、`siteCount` 和匹配条件。客户端从 GitHub 官方地址取得摘要，镜像只传输 ZIP。
5. 用新的客户端数据目录刷新清单、导入目标仓库、验证自动匹配和可用线路。需要回滚时，把清单指回之前已经验证的 ZIP 和摘要，并保留公开的历史版本。

以下命令在打包目录执行。手动导入使用你选中的 ZIP，可先按同样的方法核对发布者提供的摘要。

```powershell
$packDirectory = '.\example-archive'
$zipPath = '.\example-archive-v1.0.0.zip'
Compress-Archive -Path (Join-Path $packDirectory '*') -DestinationPath $zipPath
(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
(Get-Item -LiteralPath $zipPath).Length
```

参考 [默认资源包仓库](https://github.com/binchen6/Liuguang-RepoPacks)、[包模型](../src/TV/Services/Packs/RepoPackModels.cs)、[安装与匹配](../src/TV/Services/Packs/RepoPackService.cs)、[站点规则执行](../src/TV/Services/Packs/RepoPackVodProvider.cs)。

## 4. 提交与验收

1. 在说明中写明仓库地址、站点 `key` 和 `api`、选择的适配层、触发规则与有效范围。变更通用层时，记录至少一个同类站点的回归结果。
2. 使用隔离的 `TV_DATA_ROOT`，实际导入、切换仓库，并按站点支持范围校验首页、分类分页、搜索及空结果、详情、多线路、选集、首次出帧和拖动后的继续播放。验证播放失败、请求取消或断网时的实际提示。上游需要账号或验证码时，记录为受限并附已验证的环节。
3. 资源包额外验证自动匹配与非目标仓库、手动安装、版本升级、卸载后的路由恢复，以及 SHA-256 或版本不一致时保留上一份有效包。含 `overrideUniversal` 的规则应同时覆盖命中站点和相邻的正常站点。
4. 对新增适配执行发布回归和站点探测。保存可复核的主机与 Windows 版本、测试时间、样本和结果。验收状态见 [站点适配与验收](site-verification.md)。

```powershell
pwsh -NoProfile -File ./scripts/run-release-regressions.ps1
pwsh -NoProfile -File ./scripts/run-site-probe.ps1 -OutputDirectory artifacts/probe-adaptation -ImportUrl https://repo.example.org/config/sources.json -SiteKey example_archive -DecodeSample
```
