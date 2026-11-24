# 站点适配与验收

本文记录流光影院当前版本的内置源适配现状、分级验收标准、探测与回归命令及验收边界。逐站实测明细见 [站点矩阵](site-matrix.csv)；早期过程日志归档于 [历史站点验收](archive/site-verification-history.md)。

---

## 1. 当前验证基线：1.9.5

- **构建状态**：`dotnet build src/TV/TV.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 --no-restore` 0 错误（现有 231 条分析器/AOT 兼容警告，无新增错误）。
- **内置公开配置适配现状（50 个点播/工具项 + 5 个直播源）**：
  - **45 项已接入原生 C#（22 个站点适配器）或 Java CatVod 兼容层**：
    - 1.9.4 将 `❤️AI剧┃弹幕`（`csp_AiDjGuard`，原 Java 适配器指向失效域名 `zzdj.cc`）迁移为原生 `ShortDramaVodProvider`（首页 2 分类 20 条、分类 1398 页、5 组搜索关键词全中、直链后台解码 4307 ms / 254 帧）；
    - 修复 `🎈盘搜┃四盘`（`ZPan`）与 `🦋易搜┃四盘`（`JPan`）因固定 20 秒超时截断与默认 UA 卡死导致的搜索失败（实网「流浪地球」返回 38 条、「庆余年」返回 5 条，上游冷查偶发超时仍受第三方服务影响）；
    - 修复 `🏝奥特┃多线`（`csp_AueteGuard`）首页失效 `/aeete5so.php` 404 问题，改用 `/so.php?searchword=` 并准确触发应用内图片验证码；
    - 为 `csp_T4` / `csp_T4Guard` 增加明文 HTTP/HTTPS URL 配置支持；将内置防骗占位项 `cc`（`请勿信视频中任何广告`）由 `PushVodProvider` 降级为空目录并从搜索与联想列表中排除；
    - 需交互验证的站点：`💡聚剧┃四盘`（`seed`）、`🌟立播┃不卡`（`立播`）需通过应用内浏览器安全验证弹窗，`🏝奥特┃多线` 搜索需输入图片验证码；
    - 第三方上游当前不可达的站点：`🥝荐片┃多线`（`api2.rinhome.com` 上游异常）、`🍓糯米┃秒播`（`m.wwgz.cn` TLS 握手失败）、`🎧有声┃小说`（TLS 失败）、`♻️爱刷┃短视`（配置列表 404），以及网盘播放需对应账号会话。
  - **5 项无公开实现（界面标注为 `（无公开实现）`）**：
    - `🌞光影┃不卡`、`🧀奶酪┃秒播`（`csp_T4Guard`，内置配置 `ext` 为仅能由外部闭源 DEX 解密的密文）；
    - `🦊粤迷┃港剧`（`csp_MaiDuiDuiGuard`，对应版权/DRM 应用埋堆堆且依赖外部闭源 DEX）；
    - `🥜花生┃不卡`（`csp_HuashuGuard`）、`☁️风云┃多线`（`csp_FyGuard`）（外部闭源混淆 DEX，无公开接口）。
- **桌面实机 UIA 回归（1.9.4）**：`popup_regression`（19 处弹层与弹幕锚点）、`cast_regression`（SSDP/投屏播放/扫码确认）、`workspace_layout_regression`（窗口尺寸恢复/吸顶筛选/跨页搜索切换）、`library_regression`（4 分段筛选与检索状态保持）及播放器 3 秒自动隐藏与透明光标验证全部通过。

### 近期版本验证摘要

| 版本 | 主要验证项（详见对应 [发行说明](releases/)） |
| --- | --- |
| **1.9.5** | 阿里云盘分享目录与转存原画修复、UC 网盘 `ut` 设备令牌与提示视频识别/回退、弹弹play/DNS 超时取消治理、`CloudRegression`（34 项）/`LocalMediaRegression`（24 项）/`UpdateRegression`（13 项）与实网探测、及 `1.9.4 → 1.9.5` 增量升级校验 |
| **1.9.4** | 播放器鼠标快路径与跨环境兼容、`AI剧`/`盘搜`/`奥特` 搜索修复与实网解码抽样、4 项核心 UIA 回归与 `1.9.3 → 1.9.4` 增量升级校验 |
| **1.9.3** | 统一横竖导航（单选残留与切回「最新影片」胶囊修复）、全站弹层单滚动条治理、浅色主题调整、WinUI 原生透明光标隐藏与 `1.9.2 → 1.9.3` 962 文件逐字节校验 |
| **1.9.2** | 内核级播放防息屏/防休眠、`GlassSurface` 控件与 3 秒悬停感知隐藏 |
| **1.9.1** | 阿里云盘双盘与 ECDSA 设备签名（25 项协议回归）、多镜分片更新（13 项回归）与跨源弹幕规范化匹配（24 项回归） |
| **1.9.0** | 视觉体系重构、九档 `App.ico` 与矢量图标库、Composition 动效与深浅/高对比主题切换 |

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

矩阵状态定义：`SAMPLE`（已有局部样本通过）、`FAIL`（明确差异）、`BLOCKED`（账号/上游/反爬/无公开实现限制）、`PENDING`（待采样）、`N/A`（协议不提供该能力）。

---

## 3. 探测与回归命令

### 后台站点探测（不抢焦点）

```powershell
# 全量探测
& .\scripts\run-site-probe.ps1 -OutputDirectory 'artifacts/probe-current'

# 单站点或单直播频道抽样（可加 -VodId 或 -DecodeSample）
& .\scripts\run-site-probe.ps1 -SiteKey 'AiDj' -DecodeSample -OutputDirectory 'artifacts/probe-one'
& .\scripts\run-site-probe.ps1 -SiteKey 'live:0' -LiveChannel '频道名' -DecodeSample -OutputDirectory 'artifacts/probe-live'

# 将探测结果回写站点矩阵
py scripts/update-site-matrix.py artifacts/probe-current/probe.jsonl
```

### 桌面回归

```powershell
& .\scripts\run-site-regressions.ps1 -Python (Get-Command py).Source -OutputDirectory 'artifacts/qa-current'
```

执行约定：使用独立 `TV_DATA_ROOT`、音量置 0、独立安装 `AppId`（不覆盖正式安装）；桌面回归脚本会激活窗口，须在空闲桌面串行运行；严禁将带凭据的仓库地址、Cookie、Token 或私人视频写入共享日志。

---

## 4. 验收边界

- 第三方站、CDN、会员权限、验证码与地区策略随时变动，历史通过记录不代表永久可用。
- 未重新扫码登录的账号不视为当前实机账号验收。
- 后台解码抽样通过不等同于所有 GPU 驱动、HDR 显示器与桌面渲染均无异常。
- 弱网缓冲与预读只能降低卡顿概率，不能保证极端网络下无中断。
