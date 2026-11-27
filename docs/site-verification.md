# 站点适配与验收

本文记录流光影院当前的三层站点适配架构、最新验证基线、分级验收标准与回归探测命令。逐站实测明细见 [站点矩阵](site-matrix.csv)；早期日志归档于 [历史站点验收](archive/site-verification-history.md)。

---

## 1. 三层适配优先级架构

新增适配按 **原生 > 通用 > 单独适配包** 选择。处理器命中顺序、专属覆盖条件和包格式见 [仓库与站点适配规范](repository-site-adaptation.md)。

| 优先级 | 适配层 | 核心能力 |
| --- | --- | --- |
| **Tier 1 · 原生** | 22 个 C# 原生站点适配器（含 `Wogg` / `FishCloud`、`Bili`、`ChangZhang`、`Jpys/WenCai`、`Guazi`、`Douban/PianDan`、`ShortDrama/DJHub`、`Kanqiu/Sir88`、`Rebo`、`Seedhub`、`Libvio` 等） | 零外部进程、最高解析速度与最完整元数据/网盘线路支持 |
| **Tier 2 · 通用** | `UniversalProtocolVodProvider`（AppYs/AList/360Kan/网盘合集）→ `JsSpiderClient`（`drpy2 js:` + `CryptoJS.AES` + T3）→ `PySpiderClient` → `UniversalAutoHtmlVodProvider`（3 段式自动探测）→ Java 17/21 内置爬虫与 `ExternalJarLoader` DEX 动态转译 | 开箱兼容绝大多数第三方 TVBox 单仓、多仓、加密配置与主流站型 |
| **Tier 3 · 资源包** | `RepoPackService` + `RepoPackVodProvider`（托管于 [Liuguang-RepoPacks](https://github.com/binchen6/Liuguang-RepoPacks)，复用 5 条国内 GitHub 镜像加速 + SHA-256 + Zip-Slip 校验） | 针对特定外部仓库的专属规则补齐；导入源自动下载热启用，亦可在设置页手动更新/卸载；对闭源 `.so` 站点展示诚实说明卡 |

---

## 2. 当前验证基线（1.9.7+）

- **构建状态**：Java `catvod-engine-1.0.0.jar`（JDK 21 Maven 离线打包）与 C# `TV.csproj` `Release x64` 均 0 错误通过。
- **84 项 C# 离线回归全部通过（`scripts/run-release-regressions.ps1`）**：
  - 覆盖 UC 网盘原画/转码/提示视频拦截与跨域头隔离、阿里云盘/分块缓存跳转取消（`CloudBlockCache`）、无回弹进度条状态机（`PlaybackSeekCoordinator`）；
  - 覆盖多仓隔离与自定义命名/重命名持久化、多域名 `ext.site` 解析、`UniversalAutoHtmlVodProvider` 通用抽取、`drpy2 js:` + 真实 `CryptoJS.AES` + `:eq()` / 自节点 `a&&href`、5 条国内 GitHub 镜像路由、Zip-Slip 拦截、SHA-256 校验与资源包热加载/卸载；
  - 覆盖弹弹play DPAPI 凭据、多节点故障转移与国内 CDN 白名单校验。
- **Java Android DEX 实时转译实测**：`ExternalJarLoader` 在 JDK 21 下成功将 `y456y` 4 MB 外部 Android DEX JAR 完整转换为 4.1 MB JVM JAR（已修复 `ReadOnlyFileSystemException` 并接入 5 条国内加速镜像）。
- **外部典型大仓实网抽样（`http://www.y456y.com`，104 站点 + 1 直播源，抽样 35 站）**：
  - 导入 `http://www.y456y.com` 自动匹配并通过「国内加速 A」下载并校验启用 `y456y` 专属资源包 `v1.0.0`；
  - **Tier 1 原生通过**：`Douban`、`YGP`、`Wogg`、`FishDuoduo`、`FishHuban`、`CZ`、`wencai`、`Guazi`、`Kanqiu`、`Sir88`、`DJHub`、`bili`、`少儿教育`；
  - **Tier 2 通用通过**：`Kan360`、`Local`（自动展开 33 集夸克合集）、`Market`、`WoNiu`；
  - **Tier 3 资源包通过**：`Rebo`、`HonHon`、`Duboku`、`TvDy`、`XgAnime`、`TingBookJinXia`（1,085 集音频目录解析）、`PanWebShare123`、`fishhxq`、`FishPs`、`Music24bit`、`BeiLeHu`，以及 `FishConfig`/`bl` 等诚实说明卡；
  - **内置 Java 优先通过**：`TuXiaoBei` 毫秒级直连播放（跳过 4 MB `.so` 外部 JAR 阻塞）。
- **内置公开配置基线**：50 项点播/工具与 5 项直播源中 45 项已由原生 C# 或 Java 兼容层接入；5 项闭源混淆 DEX / 密文站点（`光影`、`奶酪`、`粤迷`、`花生`、`风云`）明确标注 `（无公开实现）`；部分站点需应用内验证码或网盘会话。

---

## 3. 分级验证标准

| 环节 | 通过标准 |
| --- | --- |
| 首页 / 分类 | 核对栏目、分类、卡片 ID、标题、封面与末页终止，不以 HTTP 200 代替 |
| 搜索 | 核对多组关键词、空结果、单站重试、取消与并发聚合 |
| 详情 / 播放 | 对照多线路、选集、解码时钟、视频帧/音频缓冲、拖拽与停播释放 |
| 直播 / 网盘 | 核对分组、EPG、多线路切台；区分扫码成功、目录可读与实际播放 |

状态标记：`SAMPLE`（局部样本通过）、`FAIL`（明确差异）、`BLOCKED`（账号/上游/反爬/闭源限制）、`PENDING`（待采样）、`N/A`（协议不提供）。

---

## 4. 探测与回归命令

```powershell
# 84 项离线协议与核心引擎回归（加 -VerifyDefaultService 可同步实测在线弹幕服务）
pwsh -NoProfile -File ./scripts/run-release-regressions.ps1

# 后台全量 / 指定仓库 / 单站抽样探测（不抢焦点，可加 -DecodeSample 实播解码）
pwsh -NoProfile -File ./scripts/run-site-probe.ps1 -OutputDirectory 'artifacts/probe-current'
pwsh -NoProfile -File ./scripts/run-site-probe.ps1 -OutputDirectory 'artifacts/probe-y456y' -ImportUrl 'http://www.y456y.com' -SiteKey @('Douban','FishDuoduo','Kan360','TingBookJinXia')

# 将探测结果回写站点矩阵
py scripts/update-site-matrix.py artifacts/probe-current/probe.jsonl

# 桌面 UIA 回归（须使用独立 TV_DATA_ROOT 并在空闲桌面串行运行）
& .\scripts\run-site-regressions.ps1 -Python (Get-Command py).Source -OutputDirectory 'artifacts/qa-current'
```

---

## 5. 验收边界

- 第三方站点、CDN、反爬、验证码与账号权益随时变化，历史样本通过不代表永久可用；
- 后台解码抽样通过不等同于所有显卡驱动、HDR 显示器与远程桌面均无差异；
- 弱网缓存与分块预读可显著降低卡顿，但不能保证极端网络下绝对零中断。
