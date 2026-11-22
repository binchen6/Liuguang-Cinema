# 站点适配与验收

## 当前基线：1.4.1

- Release 构建通过；配置、直播、CMS 与离线下载协议 15 项通过。
- LibVLC、HLS/DASH、Range、代理刷新、跨域请求头和轨道协议 22 项通过。
- 本地媒体、侧车弹幕字幕、EPG、跨域下载、清理、换源和直达服务专项 23 项通过。
- 无全局键鼠输入的 UIA 回归通过：直达播放、完整应用播放与离页、详情按钮尺寸、下载弹层、窄窗媒体库和设置导航。
- 独立安装实例核对 970 个发布文件哈希，安装态版本、文件关联和静音直达播放通过；卸载后载荷和独立文件关联全部清理。
- 安装器 SHA-256：`ff4d2c51f99e727758aba09f4d81a8ffe0d8092dd1dc771d38ee66dcbaac29a9`。

历史站点逐条证据见[验收归档](archive/site-verification-history.md)。归档中的临时日志可能已清理，不能把历史结果自动视为当前第三方服务可用。

## 验证层级

站点“存在适配器”“能返回内容”“与原站一致”“能解析地址”“能持续播放”是不同层级。

| 环节 | 通过标准 |
| --- | --- |
| 首页 | 核对栏目、分类、卡片 ID、标题、封面和顺序，不满足于 HTTP 200 |
| 分类 | 对照首、次、末页，检查筛选、分页和末页停止 |
| 搜索 | 核对多个关键词、空结果、重试、取消、来源聚合和返回位置 |
| 详情 | 对照片名、年份、地区、演职员、简介、线路和集数 |
| 播放 | 记录实际时钟、画面、音频、轨道、画质、倍速、拖动和停止 |
| 直播 | 核对分组、频道、台标、EPG、多线路和实际切台 |
| 网盘 | 区分扫码成功、凭据有效、目录可读、地址可解析和实际播放 |

矩阵中的 `SAMPLE` 表示已有局部样本，`FAIL` 表示明确差异，`BLOCKED` 表示账号、上游或环境限制，`PENDING` 表示缺少样本，`N/A` 表示协议本身不提供该能力。

## 后台站点探测

在工作区根目录使用独立输出目录：

```powershell
& .\scripts\run-site-probe.ps1 -OutputDirectory 'artifacts/probe-current'
```

按站点或直播项缩小范围：

```powershell
& .\scripts\run-site-probe.ps1 -SiteKey '站点键' -OutputDirectory 'artifacts/probe-one'
& .\scripts\run-site-probe.ps1 -SiteKey 'live:0' -LiveChannel '频道名' -DecodeSample -OutputDirectory 'artifacts/probe-live'
```

点播可配合 `-VodId` 指定详情样本。探测运行生产路由，不发送键鼠，也不启动 WebView2 嗅探。结果可写入站点矩阵：

```powershell
py scripts/update-site-matrix.py artifacts/probe-current/probe.jsonl
```

不要把带凭据的仓库地址、Cookie、Token、用户数据库或私人视频写入共享日志。

## 桌面回归

```powershell
& .\scripts\run-site-regressions.ps1 -Python (Get-Command py).Source -OutputDirectory 'artifacts/qa-current'
```

传统脚本可能激活窗口并发送键鼠，必须在空闲桌面串行执行。只做布局或生命周期检查时使用 UIA Invoke/Selection/Value 等直接操作模式，避免全局输入。测试应设置独立 `TV_DATA_ROOT`、音量 0 和独立安装 AppId。

安装验收至少核对：安装器版本和 SHA-256、全部载荷哈希、启动与真实播放、文件关联、卸载登记及卸载清理。QA 实例不得覆盖正式安装目录或复用正式文件关联。

## 边界

- 未重新扫码的账号不列为当前真实账号验收。
- 第三方站、CDN、会员权限、验证码和地区限制可能随时变化。
- 虚拟输出解码通过不等于所有 GPU、HDR 显示器和桌面渲染均通过。
- 缓冲与预读只能降低弱网影响，不能保证所有网络环境无卡顿。
- 当前站点逐项状态以[站点矩阵](site-matrix.csv)和对应版本说明为准。
