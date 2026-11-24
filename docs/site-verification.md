# 站点适配与验收

本文定义流光影院的当前验证基线、分级通过标准、后台探测与桌面回归命令及验收边界。逐站最新结果以 [站点矩阵](site-matrix.csv) 为准；早期逐条证据归档于 [历史站点验收](archive/site-verification-history.md)。

---

## 1. 当前验证基线：1.9.1

- **构建状态**：`dotnet build src/TV/TV.csproj -c Release --no-restore` 0 错误、0 新增警告；全站 XAML 字号审计全部 `≥12px`，24 项 UIA 自动化名称契约全量通过。
- **核心协议与功能回归**：
  - `CloudRegression`（25 项）：阿里云盘 `POST /v2/user/get`、`/adrive/v3/file/list`、`/adrive/v3/file/search`、`/v2/file/get_download_url`、`create_session` 双盘路由、不向 CDN 透传 `Authorization` 及纯 C# `AliDeviceSession` secp256k1 ECDSA 自验 5/5 通过。
  - `UpdateRegression`（13 项）+ 实网国内加速实测：GitHub API 403 限流网页源兜底解析通过；多镜测速 + 4 路 Range 并发分片下载通过且严格匹配官方 SHA-256。
  - `LocalMediaRegression`（24 项）+ 实网弹弹play 端到端测试：`MediaMatching.Parse` 跨源规范化匹配精确命中（《葬送的芙莉莲》6 种命名均命中同一 `EpisodeId=176170005`、6,631 条弹幕；《2.5次元的诱惑》不再被误截断；单字符/综艺日期不再触发 `errorCode=2`；真人剧《庆余年》正确拒绝无关动画误匹配）。
- **桌面实机 UIA 验收（`1587×1039`，暗室素墨 + 素白画廊双主题）**：点播页 1:1 可打断下滑收起/回滑/回顶、搜索页/云盘页/直播页/设置页连点选中态复位、媒体库 4 分段滑轨与投屏页全部通过，`last-error.log` 为空。
- **安装包与增量升级验收**：
  - 完整安装器 `TV-Setup-1.9.1-x64.exe`（138.20 MB）三次启动实测 **304 / 255 / 244 ms**（冷启 ≤1500 ms、热启 ≤600 ms）；
  - 增量更新包 `TV-Update-1.9.1-from-1.9.0-x64.exe`（9.60 MB，14 个变更文件）实机静默升级后与 1.9.1 便携包逐文件 SHA-256 100% 一致。

### 里程碑验收摘要

| 版本 | 核心验收项（详情见对应 [发行说明](releases/)） |
| --- | --- |
| **1.9.0** | 「白 · 隙 · 镜」全站视觉重构、9 档位 `App.ico` + 矢量图标库、GPU 物理动效引擎与深浅/高对比主题切换全页 UIA 验收 |
| **1.8.1** | 封面 UI 线程解码修复、GitHub 正式版自动更新与 `1.8.0→1.8.1` 文件级增量升级端到端哈希校验（958 文件） |
| **1.8.0** | HTTP/2 独立图片连接池、播放/暂停零延迟与 PLL 单调弹幕时钟、SAC 98.4% 已签名覆盖率（726/738）与 417/285 ms 秒开安装器 |
| **1.7.0** | 弹弹play 开放弹幕库 v2 鉴权、阿里云百炼 AI 字幕生成/翻译/WASAPI 实时字幕、长 HLS 会话保留（4096）与日志自动脱敏 |
| **1.6.x** | 中文 IME 非抢焦搜索建议浮层、6 个新增原生站点（文采/瓜子/橘猫/韩迷/热播/聚剧）与 19 存量点播 + 5 直播源全量回归 |
| **1.5.0 / 1.4.1** | 单播放器懒加载冷启 ~0.7s、网盘分块/HLS 预读、奥特验证码、本地侧车字幕与独立安装卸载全链路回归 |

---

## 2. 分级验证标准

站点「存在适配器」「能返回内容」「与原站一致」「能解析地址」「能持续播放」分属不同验收层级：

| 环节 | 通过标准 |
| --- | --- |
| 首页 | 核对栏目、分类、卡片 ID、标题、封面与顺序，不以 HTTP 200 代替 |
| 分类 | 校验首、次、末页分页与组合筛选，确认末页正确停止 |
| 搜索 | 核对多组关键词、空结果、单站重试、取消、聚合排序与返回定位 |
| 详情 | 对照片名、年份、地区、演职员、简介、多线路与集数 |
| 播放 | 记录真实解码时钟、视频帧/音频缓冲、换轨、倍速、拖动与停播释放 |
| 直播 | 核对分组、频道、台标、EPG、多线路与实际切台 |
| 网盘 | 区分扫码成功、凭据有效、目录可读、地址可解析与实际播放 |

矩阵状态定义：`SAMPLE`（已有局部样本通过）、`FAIL`（明确差异）、`BLOCKED`（账号/上游/反爬限制）、`PENDING`（待采样）、`N/A`（协议不提供该能力）。

---

## 3. 探测与回归命令

### 后台站点探测（生产路由，不抢焦点）

```powershell
# 全量探测
& .\scripts\run-site-probe.ps1 -OutputDirectory 'artifacts/probe-current'

# 单站点或单直播频道抽样（可加 -VodId 或 -DecodeSample）
& .\scripts\run-site-probe.ps1 -SiteKey '站点键' -OutputDirectory 'artifacts/probe-one'
& .\scripts\run-site-probe.ps1 -SiteKey 'live:0' -LiveChannel '频道名' -DecodeSample -OutputDirectory 'artifacts/probe-live'

# 将探测结果回写站点矩阵
py scripts/update-site-matrix.py artifacts/probe-current/probe.jsonl
```

### 桌面回归

```powershell
& .\scripts\run-site-regressions.ps1 -Python (Get-Command py).Source -OutputDirectory 'artifacts/qa-current'
```

执行约定：使用独立 `TV_DATA_ROOT`、音量置 0、独立安装 `AppId`（不覆盖正式安装）；传统键鼠脚本在空闲桌面串行运行，布局检查优先使用无全局输入的 UIA 直接模式；严禁将带凭据的仓库地址、Cookie、Token 或私人视频写入共享日志。

---

## 4. 验收边界

- 第三方站、CDN、会员权限、验证码与地区策略随时变动，历史通过记录不代表永久可用。
- 未重新扫码登录的账号不视为当前实机账号验收。
- 虚拟/后台解码通过不等同于所有 GPU 驱动、HDR 显示器与桌面渲染均无异常。
- 弱网缓冲与预读仅降低卡顿概率，不能保证在极端网络下无中断。
