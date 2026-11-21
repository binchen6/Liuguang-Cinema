# 站点适配与验收

## 2026-11-22 · 1.4.0 最终基线

- 自适应 Range 预读后的播放协议 22 项通过，本地媒体/下载/清理协议 19 项通过；HDR 与单文件续播单独复验通过。
- 布局、投屏、关闭、推荐、媒体库、弹层、窗口和本地媒体桌面复验通过。搜索与补全沿用 1.3.1 已通过结果；最后一轮重试受 Windows 前台激活限制，不标为本轮全量通过。
- 最终隔离安装核对 970 个发布文件哈希，安装态布局、本地媒体与直达播放通过；卸载清理载荷、文件关联和独立登记。最终安装器 SHA-256 为 `4ff9fb099919ae46d5aacea6ed481923f45c838e11e377f5a464861b44274c39`。

## 2026-11-22 · 1.3.1 当前基线

- 云协议 32 项、播放协议 22 项、本地媒体/下载/清理/换源 18 项通过。
- 9 项既有桌面回归全部通过，另通过自动补全和本地媒体/文件参数直达播放专项。
- B 站公开接口精确匹配《流浪地球2》正片并读取 9600 条弹幕；公开会话无字幕轨时不伪造可用字幕。
- 隔离安装核对 970 个发布文件哈希，安装态布局与本地播放通过；卸载清理载荷、文件关联和独立登记。
- 弱网、不同 GPU、所有第三方站与未重新扫码的账号不作无条件通过声明。完整结果见[1.3.1 版本说明](release-1.3.1.md)。

`artifacts` 中的数据库、截图和日志默认不提交公开仓库。下文历史链接记录当时的证据路径，临时文件可能已按清理策略删除；公开用户应使用当前文档结论与 `artifacts/qa-fixture` 中的复现脚本，不要向 Issue 上传原始凭据或用户数据库。

## 2026-11-21 哔哩账号和官方画质

点播「登陆配置」在独立 Windows 桌面能打开官方扫码面板并显示二维码。真实扫码完成后，应用保存加密会话，重新访问 B 站账号接口校验通过。各合集使用同一存储。样片 `BV14q4y1K7Np` 的游客会话可以实播 1080P 高清和 360P 流畅，1080P 60 帧和 4K 在实际返回视频流后才启用。确认原编号 116 和 120 对应这两个档位。[扫码入口](../artifacts/qa-site-fidelity-20261121/video-quality-current/private-bili-gui-retry.log) · [游客实播](../artifacts/qa-site-fidelity-20261121/bilibili-account-current/guest-decode.log) · [真实扫码与会话清理](../artifacts/qa-site-fidelity-20261121/bilibili-account-current/seek-study-auth.log)

高画质分离视频改用有界字节范围和分块预读。真实账号下，1080P 60 帧解码到 1920×1080，4K 解码到 3840×2160。两档至少一轮在跳至 12 秒和 28 秒后恢复音画。成功跳转耗时约 7 到 15 秒。重复测试仍出现一轮 1080P 60 帧未进入可拖动状态，以及一轮 4K 跳到 12 秒后播放时钟停顿。可用档位、实播与跳转稳定性按各自证据记录。[CDN 读取](../artifacts/qa-site-fidelity-20261121/bilibili-account-current/cdn-byte-ranges.log) · [1080P 60 帧](../artifacts/qa-site-fidelity-20261121/bilibili-account-current/bounded-116.log) · [4K](../artifacts/qa-site-fidelity-20261121/bilibili-account-current/bounded-120.log)

## 2026-11-21 通用画质与高帧率

同分辨率的 30、60、120 和 240 帧分别制作原始样片。HLS 样片宣告 59.940、119.880 和 239.760 帧，DASH 样片使用实际分数帧率字段。新菜单显示「原画（270p · 240帧）」及四个实际档位。后台验证 HLS 五档和 DASH 三个高帧率档均请求到对应分段，解码输出音画，拖动后继续播放。直链 MP4 由 LibVLC 实际读取出 240 帧。独立 Windows 桌面中，点击 「270p（120帧）」取得该档分段，并确认当前输入桌面保持原样。[分段与解码](../artifacts/qa-site-fidelity-20261121/video-quality-current/quality-native.log) · [独立桌面](../artifacts/qa-site-fidelity-20261121/video-quality-current/private-hfr-gui.log)

Apple 样片继续按显示器能力选择 SDR 或 PQ，HLS 的关联音轨、字幕及 HLG 兼容层保留。夸克夹具返回同为 1080p 的普通、60 帧和 120 帧转码，选择 120 帧时匹配其独立媒体地址。原画线路同时读取可用转码列表。[HDR 回归](../artifacts/qa-site-fidelity-20261121/video-quality-current/hdr-manifest.log) · [网盘回归](../artifacts/qa-site-fidelity-20261121/video-quality-current/cloud-variants.log)

## 2026-11-21 四网盘与我的云盘

- Release 构建 0 错误。云协议夹具覆盖夸克回退与真实档位、UC 会话、百度提取码/目录/搜索/转存去重、阿里个人目录/搜索/直读及 `MDrive` 路由，全部通过。[云协议结果](../artifacts/qa-cloud-ui-20261121/cloud-protocol-final/results.log)
- Range 完整/有界字节、同源/跨域凭据、HLS 双音轨和字幕、初始地址过期刷新、后续分块 412 刷新与会话释放均通过。[播放协议结果](../artifacts/qa-cloud-ui-20261121/playback-protocol-5/playback.log)
- 授权夸克 profile 读取 66 个文件，抽查 5 个原画 MP4/MKV 均为 HTTP 200。[原画响应](../artifacts/qa-cloud-ui-20261121/quark-authorized/results.log) 原画和转码连续解码，等待 seek 推进后设 1.25 倍速，二者约 14.2 秒仍 Playing，正常停止且退出码 0。[实播结果](../artifacts/qa-cloud-ui-20261121/quark-playback-settled/results.log)
- 阿里、UC、百度真实账号扫码与播放仍未验收；夹具不能替代账号实播。玩偶旧 4.4MB 分享已失效，当前玩偶公开详情入口在本机返回 403。
- 盘他五组关键词均返回 10 条并展开 50 集；易搜三组成功、两组超时并展开 23 集；盘搜仍有上游超时。盘她、聚剧、抠搜、优汐没有公开端点，继续标记外部阻塞。[盘搜复测](../artifacts/qa-cloud-ui-20261121/panso-existing-2/probe.jsonl) · [无端点复查](../artifacts/qa-cloud-ui-20261121/panso-missing/probe.jsonl)
- 最终 9 项桌面回归全部通过：workspace_layout、search、cast、window、close_dialog、recommendation、library、popup、hdr_desktop。[结果汇总](../artifacts/qa-cloud-ui-20261121/final-ui/results.csv) 新版详情与播放器已抓图核对，浅色主按钮对比度修正后弹层回归再次通过。[实图](../artifacts/qa-cloud-ui-20261121/screenshots-final/)

## 2026-11-21 百度网盘扫码

原登录流程兑换百度账号票据后直接校验网盘接口，复现 `quota` 返回 `errno=-6`。对照官网后补上网盘入口及登录跳转。独立 Cookie 容器按域名保存各跳响应，网盘获取自己的 `STOKEN`、`PANPSC` 后再校验并保存会话。当前 `loginStatus` 返回 `errno=2` 时，网盘配额接口用于确认会话。[官网对照](../artifacts/qa-site-fidelity-20261121/baidu-login-current/official-scan-refresh.log) · [应用跳转记录](../artifacts/qa-site-fidelity-20261121/baidu-login-current/baidu-drive-redirect.log)

使用当前 Release 源码构建，0 错误。后台夹具验证扫码状态、网盘跳转与 Cookie 范围。用户在手机扫码确认后，应用后台显示登录成功，保存的会话重新校验通过，个人根目录也能读取。实测使用独立配置，验收结束后清理测试凭据。[客户端构建](../artifacts/qa-site-fidelity-20261121/baidu-login-current/drive-session-build.log) · [后台夹具](../artifacts/qa-site-fidelity-20261121/baidu-login-current/drive-redirect-fixture.log) · [真实扫码验收](../artifacts/qa-site-fidelity-20261121/baidu-login-current/ready-scan-results.log)

## 2026-11-21 玩偶哥哥优先版

| 环节 | 实测结果 |
| --- | --- |
| 首页 | 保留「最新影片」和原站八个分类。对照公开镜像默认可见的 135 张卡片，逐张核对影片 ID、片名、封面地址和顺序。读取卡片上的年份、地区、主演和连载备注。[原站页面回放](../artifacts/qa-site-fidelity-20261121/wogg-final-catalog-replay.log) |
| 分类 | 对照八个分类的首、次、尾页。玩偶电影为 70、70、26 条，尾页是第 175 页。五组原站筛选选项顺序一致，组合筛选「美国、科幻、2024、评分排序」返回 59 条。[页面回放](../artifacts/qa-site-fidelity-20261121/wogg-final-catalog-replay.log) · [线上探测](../artifacts/qa-site-fidelity-20261121/wogg-priority-final-live/probe.jsonl) |
| 搜索 | 同一会话按原站 3 秒间隔发起查询。五个关键词首屏返回 40、6、4、18、5 条。「山海」第 2 页为 1 条。空结果、连载备注、年代和剧情均有对照。[线上探测](../artifacts/qa-site-fidelity-20261121/wogg-priority-final-live/probe.jsonl) · [原站间隔提示](../artifacts/qa-site-fidelity-20261121/wogg-search-notice.log) |
| 详情 | 《现在就出发 第四季》通过旧链接仍能打开，同原站对照标题、年代、地区、主演、完整简介和连载备注。按次序保留 3 个夸克和 3 个百度分享入口，百度分享沿用原站的网盘打开方式。[详情和剧集对照](../artifacts/qa-site-fidelity-20261121/wogg-final-catalog-replay.log) |
| 播放 | 在独立登录配置中只转存同一份 4.4 MB 样片。夸克原画实测 1920×1080，高清实测 1440×810。两条线路在拖到 12 秒并变速后仍有声音和画面，也能正常停止。原画 MP4 和转码 HLS 均使用 FFmpeg 解复用器，修复了各自默认解复用器在拖动、变速后停止时的崩溃。[MP4 崩溃](../artifacts/qa-site-fidelity-20261121/wogg-quark-stop-software.log) · [HLS 崩溃](../artifacts/qa-site-fidelity-20261121/wogg-publish-hls-isolated.log) · [两条线路复验](../artifacts/qa-site-fidelity-20261121/wogg-hls-fix-both.log) · [高清 2 倍速](../artifacts/qa-site-fidelity-20261121/wogg-hls-fix-twice-speed.log) |

公开主站在本机返回 HTTP 403，目录对照使用玩偶哥哥公开镜像的原始页面。线上抽查及页面回放共覆盖首页、分类、搜索、详情。实播样片覆盖 1080p 原画和高清转码，4K/HDR、桌面菜单和端到端交互仍待实机验收。1.1.9 对照程序和 1.2.0 发布程序在本机独立桌面均因 `0xC0000142` 退出，当前桌面继续留给用户使用。[公开入口状态](../artifacts/qa-site-fidelity-20261121/wogg-original/requests.json) · [1.1.9 独立桌面](../artifacts/qa-site-fidelity-20261121/private-desktop-baseline-1.1.9.log) · [1.2.0 独立桌面](../artifacts/qa-site-fidelity-20261121/wogg-private-desktop-smoke.log)

1.2.0 x64 安装包来自本地源码提交 `97eb867`。核对了包体版本、SHA-256、WinUI 资源、FFmpeg 解复用组件与 Java 引擎。用该包的发布目录重新通过原站页面回放 80 项、夸克两条线路实播、媒体字节范围、HLS 分段和直播协议回归。安装包为未签名状态。[校验记录](../artifacts/qa-site-fidelity-20261121/wogg-release-verification.csv) · [最终目录回放](../artifacts/qa-site-fidelity-20261121/wogg-release-payload-catalog.log) · [最终实播](../artifacts/qa-site-fidelity-20261121/wogg-release-payload-playback.log) · [打包日志](../artifacts/qa-site-fidelity-20261121/wogg-installer-release.log)

## 2026-11-21 八八看球

| 环节 | 实测结果 |
| --- | --- |
| 首页与赛事分类 | 同一批原站快照中恢复 22 项赛事分类、录像集锦入口和 54 张赛事卡片。NBA 和 CBA 各还原 10、44 场，赛事分类到第 1 页结束。线上复验读取了 57 张更新后的卡片。[快照回归](../artifacts/qa-site-fidelity-20261121/catalogue-origin/kanqiu-replay-fixture-results.log) · [直播探测](../artifacts/qa-site-fidelity-20261121/kanqiu-live-recheck/probe.jsonl) |
| 录像与集锦 | 第 1、2、1441 页分别还原 30、30、16 张卡片，保留封面、发布时间和说明。「猛龙 vs 活塞」详情还原 12 组共 20 个全场回放、分节和集锦入口。[录像探测](../artifacts/qa-site-fidelity-20261121/kanqiu-replay-recheck/probe.jsonl) · [原站录像栏目](https://www.88kanqiu.one/replay) |
| 赛事详情 | 分别对照未开始、直播中和已结束的赛事。直播样本读取两条公开线路，结束样本保留六条线路与两条同名 C4。未开始样本显示赛程，没有播放线路。[快照回归](../artifacts/qa-site-fidelity-20261121/catalogue-origin/kanqiu-replay-fixture-results.log) · [原站赛事详情](https://www.88kanqiu.one/live/132401/play) |
| 播放 | 客户端解析保留原播放器地址。原站 Q1 线路为 1280×720，时钟到 100881 ms。高清 1 回放为 1920×1080，时钟从 2803 增至 7817 ms。客户端 WebView2 起播、切线和回放拖动待验。[原站直播时钟](../artifacts/qa-site-fidelity-20261121/kanqiu-browser/first-line-clock.log) · [原站高清回放时钟](../artifacts/qa-site-fidelity-20261121/kanqiu-browser/replay-hd-playing.log) |

该源关闭搜索。本文的结果对应配置键「看球」。[Release 构建](../artifacts/qa-site-fidelity-20261121/catalogue-origin/replay-build.log)为 0 错误、158 条警告。

## 2026-11-21 第三批复验

| 范围 | 实测结果 |
| --- | --- |
| 七个哔哩合集 | 主线路均重新解码。对同一公开视频核对了 1080p、480p、360p。480p 和 360p 均载入三条原站独立音轨，切换后在当前位置继续输出，并验证拖动续播。原画下载继续解析单个 MP4。[原站格式](../artifacts/qa-site-fidelity-20261121/bili-playurl-formats.json) · [逐站日志](../artifacts/qa-site-fidelity-20261121/bilibili-all/probe.jsonl) · [音轨与拖动复验](../artifacts/qa-site-fidelity-20261121/bilibili-play-audio.log) |
| 厂长 | 按发布页更新入口。对照原站 10 个分类、58 张首页卡片，「最新电影」第 1、2、末页为 25、25、22 条。五个关键词均返回结果，《山海情》保留 23 集，《峡谷》保留两条 1080P 入口。首条 HLS 线路去除 PNG 包装后在客户端播放后端解码 243 帧、输出 238 个音频缓冲，拖到 12 秒后继续播至 13241ms。第二条原站播放器实播到 4787ms，客户端 WebView2 待验。[厂长发布页](https://www.czzy.site/) · [逐环节探测](../artifacts/qa-site-fidelity-20261121/changzhang-final/probe.jsonl) · [原站对照与播放](../artifacts/qa-site-fidelity-20261121/changzhang-png-playback.log) · [第二条原站实播](../artifacts/qa-site-fidelity-20261121/changzhang-browser/playback-clock.log) |
| 立播 | 配置入口的主页和「庆余年」搜索在本机均返回 HTTP 403。抽查的备用域名返回停用通知，通知新列的两个入口在本机也返回 403。本次按上游不可读记录。[配置入口复查](../artifacts/qa-site-fidelity-20261121/libvio-configured-response.json) · [原站停用通知](https://www.libvio.link/) |

以上验证使用公开会话。账户登录、限会员内容和桌面画质菜单仍待验收。[Release 构建](../artifacts/qa-site-fidelity-20261121/release-png-wrapper.log)为 157 条警告、0 错误，已保存[产物哈希](../artifacts/qa-site-fidelity-20261121/current-binaries.csv)。后台回归覆盖[分段包装与字节](../artifacts/qa-site-fidelity-20261121/png-wrapped-hls-fixture.log)、[媒体范围](../artifacts/qa-site-fidelity-20261121/offline/media-range-after-png.log)、[搜索聚合](../artifacts/qa-site-fidelity-20261121/offline/search-model-after-png.log)和[直播协议](../artifacts/qa-site-fidelity-20261121/offline/live-protocol-after-png.log)。

## 2026-11-21 动漫巴士

首页原站导航有四个分类，恢复「欧美动漫」的完整名称。逐项核对 56 张卡片。固定栏目核对顺序，热播栏的 8 张卡片按集合核对，原站排行会变化。[原站首页](https://dm84.tv/) · [对照记录](../artifacts/qa-site-fidelity-20261121/dm84-source/dm84-fixture-results.log)

首页可进入完整追番日历和热播动漫榜。日历默认显示原站选中的星期，客户端将当天选项放在首位。七天共 158 个条目，热播榜 100 个条目，逐项核对片名、更新备注和顺序。两类均在第 1 页结束。[原站日历](https://dm84.tv/timeline.html) · [原站热播榜](https://dm84.tv/top.html) · [对照记录](../artifacts/qa-site-fidelity-20261121/dm84-discovery/discovery-results.log)

国漫第 1、2、10 页各 36 条，显示原站实报的第 10 页为末页。按原次序还原类型、时间和排序三个筛选组。「奇幻、2024、按人气」组合返回 36 条首屏记录，实际末页为第 6 页。[国漫分类](https://dm84.tv/list-1.html) · [原站快照摘要](../artifacts/qa-site-fidelity-20261121/dm84-source/source-summary.log)

生产 Java 桥接对照「山海」的 12 张卡片、一个空结果词及「的」的首、次页。后者两页各 36 条，原站标记第 38 页为末页。使用旧影片 ID 读取详情，对照片名、年代、地区、更新备注、简介和 495 个剧集入口。13 项目录与入口对照通过。原站播放器以 1920×1080 推进至 12329 ms。客户端 WebView2 实播仍待验。[Java 桥接对照](../artifacts/qa-site-fidelity-20261121/dm84-source/dm84-fixture-results.log) · [原站实播](../artifacts/qa-site-fidelity-20261121/dm84-source/manifest-active-player.log)

## 2026-11-21 兔小贝

首页采用官网「动画视频」的默认可见卡片，保留原规则的儿歌、故事、国学和启蒙。使用同一份页面快照核对 24 张卡片的顺序、片名、封面和播放数。线上两组推荐会轮换。儿歌第 1、2、27 页分别为 30、30、16 条，第 28 页为空。CatVod 把未知分页值编码为 `2147483647`，客户端识别后按未知处理。有明确页数的动漫巴士仍显示第 10 页为末页。[官网动画栏目](https://www.tuxiaobei.com/animate) · [快照回放](../artifacts/qa-site-fidelity-20261121/tuxiaobei-current/snapshots-final.log) · [分页边界](../artifacts/qa-site-fidelity-20261121/tuxiaobei-source/pagination-boundary.log) · [动漫巴士复测](../artifacts/qa-site-fidelity-20261121/tuxiaobei-current/dm84-pagination-regression/probe.jsonl)

内置配置关闭此源的全站搜索。独立调用适配器对照「小兔」的三条结果及翻页终止。使用旧地址打开《小猪猪》，核对片名、封面、分类、时长、简介和原播放器给出的 MP4。[详情及搜索对照](../artifacts/qa-site-fidelity-20261121/tuxiaobei-current/catalogue-verified.log) · [原站详情](https://www.tuxiaobei.com/play/1235)

直连解码出现媒体范围读取中断。该视频域名改用应用的 Windows HTTPS 代理。生产路由两次解码样本可推进至 3652 ms，各输出 221 个视频帧和 231 个音频缓冲。另一次在等待起播时超时，起播稳定性和桌面操作留待继续验证。[传输对照](../artifacts/qa-site-fidelity-20261121/tuxiaobei-current/mp4-transport.log) · [复测实播](../artifacts/qa-site-fidelity-20261121/tuxiaobei-current/playback-repeat-1235/probe.jsonl) · [超时记录](../artifacts/qa-site-fidelity-20261121/tuxiaobei-current/installed-format-playback/probe.jsonl)

在独立 Windows 桌面启动当前客户端，窗口成功创建，首页加载 24 张卡片，输入桌面检查通过。[独立桌面启动](../artifacts/qa-site-fidelity-20261121/tuxiaobei-current/private-desktop-startup.log)

## 2026-11-21 有来医生急救指南

首页恢复八类共 159 个原站条目。逐类核对原有顺序、文章 ID、封面和子组名称。原封面使用 HTTPS 绝对地址，解析后保留该地址。分类均在第 1 页结束。配置关闭搜索。三条详情对照标题、封面、医生和原视频地址。[原站指南](https://m.youlai.cn/jijiu) · [目录和详情对照](../artifacts/qa-site-fidelity-20261121/aid-source/aid-catalogue-results.log)

定位到 LibVLC 的 GnuTLS 在本机不信任该视频域名的证书链，直连解码 0 帧。该站点的视频请求改用 Windows HTTPS 验证证书，并由本地代理把原视频交给 LibVLC。生产路由复验播放至 3651 ms，解码 225 帧，输出 216 个音频缓冲。同一视频拖动测试推进至 13072 ms。桌面交互仍待验。[原始故障与拖动对照](../artifacts/qa-site-fidelity-20261121/aid-source/aid-transport-results.log) · [生产路由复验](../artifacts/qa-site-fidelity-20261121/aid-fixed-decode/probe.jsonl) · [Release 构建](../artifacts/qa-site-fidelity-20261121/aid-source/client-build.log)

## 2026-11-21 易听酷狗榜单

恢复官网热门榜单、特色音乐榜和全球榜三个分组，首屏分别为 12、12、9 个榜单入口，第 2 页为空。客户端对照酷狗飙升榜、ACG 新歌榜和美国 BillBoard 榜，每份核对了 22 首曲目、歌曲顺序、歌手及榜单更新日期。配置关闭搜索。[酷狗榜单](https://www.kugou.com/yy/rank/home/1-6666.html?from=rank) · [目录和详情对照](../artifacts/qa-site-fidelity-20261121/kugou-origin/catalogue-final.log)

客户端把歌曲原地址交给播放器，包含查询和片段的链接夹具已通过回归。两个公开抽样在原站均未得到可播放的音频地址，时钟停在 0 ms，浏览器媒体错误码为 4。客户端实播仍待验证。[地址回归](../artifacts/qa-site-fidelity-20261121/kugou-origin/episode-link.log) · [飙升榜样本](../artifacts/qa-site-fidelity-20261121/kugou-origin/origin-audio.log) · [ACG 样本](../artifacts/qa-site-fidelity-20261121/kugou-origin/acg-original-audio.log)

## 2026-11-20 第二批复验

| 范围 | 实测结果 |
| --- | --- |
| 七个哔哩合集 | 读取原配置的分类与筛选，抽查内容分类第 1、2、末页。开启搜索的合集对五个关键词均返回结果。七个源的主线路样本均检测到视频、音频轨道，播放时钟继续前进。[逐站日志](../artifacts/qa-site-fidelity-20261120/final-samples/bilibili.jsonl) |
| 哔哩排序与画质 | 选择原站「最新发布」后返回不同结果。对同一视频分别验证 1920×1080 和 640×360，两个画质均能在跳到 12 秒后继续播放。[排序与画质验证](../artifacts/qa-site-fidelity-20261120/bilibili-quality/verify.log) |
| 奥特云播 M 线 | 源站对整段请求返回含完整 TS 片段的 206。代理校验范围覆盖整个片段后将状态转为 200，原样传输片段。修复后样本推进至 4043ms，解码 246 帧并输出 230 个音频缓冲。D 线抽查返回 403，搜索仍要求验证码。[片段响应](../artifacts/qa-site-fidelity-20261120/transport-diagnostics/auete-segments.jsonl) · [播放复验](../artifacts/qa-site-fidelity-20261120/auete-segment-206/probe.jsonl) · [整段与拖动范围回归](../artifacts/qa-site-fidelity-20261120/offline-final/media-range.log) |
| 直播目录 | 五个源均读取成功，频道分组按照列表首次出现的顺序显示。Kimentanm 原文件有 120 条线路与 4 个分组。斗鱼当时的原文件只有 3 条，抽样取自其中的 4K 测试线。[Kimentanm 原始 M3U](https://raw.githubusercontent.com/Kimentanm/aptv/master/m3u/iptv.m3u) · [斗鱼原始行数](../artifacts/qa-site-fidelity-20261120/douyu-playlist-origin.json) |
| 直播起播与节目单 | 综合、斗鱼测试线、YY 均起播。Kimentanm 的「春晚1983」解码 225 个视频帧，输出 109 个音频缓冲。央视两个样本上游返回 404，节目单请求为 403，北京卫视 4K 与翡翠台也未起播。[五源首轮解码](../artifacts/qa-site-fidelity-20261120/live-final/probe.jsonl) · [春晚样本](../artifacts/qa-site-fidelity-20261120/kimentanm-spring/probe.jsonl) · [北京卫视 4K](../artifacts/qa-site-fidelity-20261120/kimentanm-beijing4k/probe.jsonl) · [翡翠台](../artifacts/qa-site-fidelity-20261120/kimentanm-jade/probe.jsonl) · [上游响应](../artifacts/qa-site-fidelity-20261120/live-upstream-media.json) |
| 虎牙一起看 | 播放器以 HTTPS 请求原重定向所指的虎牙媒体地址。「周星星」与「新橙記Orange丶」均有视频和音频输出证据。两条线路的早期样本都出现过首帧停滞，普通频道的起播稳定性待验收。4K 测试样本解码 486 帧。[周星星日志](../artifacts/qa-site-fidelity-20261120/huya-flv-detail/probe.jsonl) · [新橙記与五源复测](../artifacts/qa-site-fidelity-20261120/final-transport-regression/probe.jsonl) · [早期记录](../artifacts/qa-site-fidelity-20261120/huya-star/probe.jsonl) · [4K 测试线](../artifacts/qa-site-fidelity-20261120/huya-4k/probe.jsonl) |
| 直播协议 | M3U 逗号、相对链接、频道 ID、压缩 XMLTV，以及 HLS 播放清单与直播目录区分，均已通过本地用例。[协议测试](../artifacts/qa-site-fidelity-20261120/protocol-fixtures/verify.log) |

哔哩的公开验证覆盖 HTML5 单段点播。合集中「登陆配置」为设置入口，账户登录、多段媒体与独立 DASH 音视频轨道仍待完成。一次早期直播批量进程提前退出，复用一个解码器的批次已完成五个源的记录。当前 [Release 构建](../artifacts/qa-site-fidelity-20261120/final-release-build.log)为 158 条警告、0 错误，[产物哈希](../artifacts/qa-site-fidelity-20261120/current-binaries.csv)已保存。

本批全部采用后台验证，尚需桌面复验历史搜索、菜单与实际切台。[站点矩阵](site-matrix.csv)中的 16 个无适配器项，以及其余站点的原版对照、账号和播放缺口，仍属于全站验收范围。玩偶哥哥按当前优先级单独验收与打包。

## 2026-11-20 基线

Release 增量构建为 0 警告、0 错误。沙箱里的 `py.exe` 找不到 Python 3.11。用完整路径可以启动脚本，但沙箱拒绝应用连接本机测试服务，日志中的 `Socket AccessDenied` 属于环境问题。下表来自随后允许回环的桌面测试。窗口脚本使用实际的 1.1.0 对照程序。

| 回归 | 结果 | 记录 |
| --- | --- | --- |
| workspace_layout | PASS | 初始窗口、吸顶筛选和宽窗投屏布局 |
| search | FAIL | 搜索筛选内未找到「相关度」菜单项 |
| cast | FAIL | 窄窗扫码入口不可见，侧栏不能滚动到该入口 |
| window | PASS | 尺寸、位置、最大化和对照程序启动 |
| close_dialog | FAIL | 第二次打开关闭提示后未能调用「最小化到托盘」 |
| recommendation | PASS | 推荐来源与设置 |
| library | PASS | 媒体库分段及空态 |
| popup | FAIL | 播放器「音量」的 UIA 边界不可见 |
| hdr_desktop | FAIL | 已输出 Rec.2020/PQ，切换时脚本未找到「720p」 |

完整日志及二进制哈希在 [baseline-desktop](../artifacts/qa-1.2.0-site-phase/baseline-desktop/results.csv)。投屏、播放器和搜索页面改动后的桌面回归需要独占桌面复验。

## 第一批修复与复查

| 改动 | 验证结果 |
| --- | --- |
| 搜索框文字居中、站点行和选中底色对齐 | Release 构建通过。原有自动化名称核对通过。亮暗主题截图待复验 |
| 普通窗口内播放时隐藏侧栏，窄窗控制分两行，音量菜单打开时保持控制层 | Release 构建通过。窗口返回、窄窗菜单和 HDR UIA 待复验 |
| 投屏接收动作移到侧栏首部，先显示扫码和停止入口 | Release 构建通过。窄窗扫码及局域网接收待复验 |
| 同片跨站合并，单站保留原结果，点击历史直接搜索 | [后台模型测试](../artifacts/qa-1.2.0-site-phase/search-model/verify.log)通过聚合、异年区分、详情保留来源与切站。历史点击及排序菜单待桌面复验 |
| 奥特备注、评分、详情元数据与分类末页 | [公开源复查](../artifacts/qa-1.2.0-site-phase/auete-recheck/probe.jsonl)读取了第 1、2、890 页和两条首集解析结果。另一个样本经 [LibVLC 后台解码](../artifacts/qa-1.2.0-site-phase/auete-headless-decode/decode.log)时 TLS 证书链验证失败，时钟为 0 ms |
| 比特卡片及详情字段 | [公开源复查](../artifacts/qa-1.2.0-site-phase/bttwoo-recheck/probe.jsonl)读取了首页年份、备注和评分，以及详情演职员等字段。WebView2 实播待复验 |
| 一直播与吃瓜的单页分类 | [公开源复查](../artifacts/qa-1.2.0-site-phase/live88-recheck/probe.jsonl)确认分页总数为 1。[桥接响应](../artifacts/qa-1.2.0-site-phase/live88-recheck/page-two.jsonl)确认请求第 2 页返回空列表。详情与实播待定位 |
| 各内置直播可单独选择 | 生产解析器已单独抽样 5 个列表，其中 4 个取回了分组和频道。选择器交互、超时源与实播待复验 |

为配合搜索历史和同片聚合，`search_regression.py` 更新了请求和结果数断言。UIA 助手会在当前窗口没有匹配的控件类型或操作模式时继续查找同进程浮层，避免同名文本遮住菜单项。原自动化名称保留，更新后的脚本已通过语法检查。

该批 [Release 构建](../artifacts/qa-1.2.0-site-phase/final-build.log)为 0 错误，离线 Maven 打包已完成。[构建产物哈希](../artifacts/qa-1.2.0-site-phase/current-binaries.csv)记录 TV.dll 和 CatVod JAR。

## 2026-11-21 后台调试

使用独立用户数据目录和虚拟音视频输出。安装后的玩偶哥哥实播使用已获用户授权的夸克样片，核对了原画和转码线路的画面、音频、时钟、跳转、1.25 倍速与停止。桌面布局、菜单交互和实际 HDR 输出沿用上面的基线，仍需桌面复验。[玩偶哥哥实播记录](../artifacts/qa-site-fidelity-20261121/installer-error5/debug-installed-wogg.log)

| 覆盖项 | 结果与证据 |
| --- | --- |
| 多站搜索 | 同片聚合、异年区分、详情保留多站来源、单站与全部站点切换通过。[日志](../artifacts/qa-site-fidelity-20261121/installer-error5/debug-search-aggregation.log) |
| 直播与 EPG | M3U 属性与标题、相对线路和台标地址、Gzip 节目表样本通过。[日志](../artifacts/qa-site-fidelity-20261121/installer-error5/debug-live-epg.log) |
| 播放传输 | 直链完整响应、Range 响应与 HLS 封装分片的字节校验通过。[Range 日志](../artifacts/qa-site-fidelity-20261121/installer-error5/debug-byte-ranges.log) · [HLS 日志](../artifacts/qa-site-fidelity-20261121/installer-error5/debug-hls-source.log) |
| 下载 | 修复部分 HTTP 206 被保存成完整文件。完整 MP4、完整 Range、HLS、截断与长度冲突等 8 项通过。[修复前](../artifacts/qa-site-fidelity-20261121/installer-error5/debug-download-before.log) · [修复后](../artifacts/qa-site-fidelity-20261121/installer-error5/debug-download-after.log) |
| SQLite | 设置读取、续播进度与线路、收藏及下载记录的添加和清理共 6 项通过。[日志](../artifacts/qa-site-fidelity-20261121/installer-error5/debug-persistence.log) |
| Java | 用发布包内的 Java 17 和 CatVod JAR 验证首页推荐合并、旧搜索分页和原生分页共 5 项。[日志](../artifacts/qa-site-fidelity-20261121/installer-error5/debug-java-bridge.log) |

下载修复后 Release 构建 0 错误、158 警告。警告包括分析器提示及 NuGet 漏洞数据源访问失败。随后发布的安装修订包包含这次下载修复和比特分类末页修复，并已验证用户目录覆盖升级。[调试构建日志](../artifacts/qa-site-fidelity-20261121/installer-error5/debug-after-build.log) · [发布构建日志](../artifacts/qa-site-fidelity-20261121/installer-destination/standard-release-build.log)

## 桌面回归

在 PowerShell 7 和空闲 Windows 桌面中，由工作区根目录运行。脚本会激活前台窗口，发送键鼠输入，并启动回环 HTTP 服务。照片、实播验收和各回归应串行执行。测试过程中如需使用这台桌面，可只运行下一节的后台探测。

```powershell
$python = (Get-Command py).Source
& .\scripts\run-site-regressions.ps1 -Python $python -OutputDirectory 'artifacts/qa-current'
```

每次使用新输出目录。另有对照版本时，通过 `-WindowBaseline <1.1.0目录/TV.exe>` 指定。Python 需安装 Pillow。脚本使用工作区内的隔离配置。回归失败时先读对应 `.log`，再从配置和样片把失败收敛到单个入口。

截图用 `artifacts/qa-fixture/shot.py <exe> <out.png> 1366 768 --theme dark`。主题复核使用 `--theme light`，记录分辨率、缩放、页面及操作步骤。播放探测需要记录实际播放时钟、画面和音轨等，不能只根据地址判断。

## 后台逐站抽样

干净客户端的[诊断日志](../artifacts/qa-1.2.0-site-phase/clean-desktop/profile/diagnostic.log)记录了 49 个点播项，配置快照另含 5 个直播源。当前用户配置只记录了旧版的三个内置接口，没有已保存的导入仓库。这轮使用干净用户数据和公开站点。每一项进入了 CSV，包括没有本地适配器的项。

```powershell
& .\scripts\run-site-probe.ps1 -Config 'artifacts/qa-1.2.0-site-phase/clean-desktop/profile/config/last-good.json' -OutputDirectory 'artifacts/probe-current'
& $python scripts/update-site-matrix.py artifacts/probe-current/probe.jsonl --date 2026-11-20
```

省略 `-Config` 可以从内置入口重新读取配置。单站使用 `-SiteKey '奥特'`，单个直播源使用当次快照的 `live:序号`。导入源用 `-ImportUrl` 指定要验收的仓库链接，脚本会用客户端导入链路生成实际站点键。请勿将包含访问凭据的链接写入共享日志。

探测读取首页、首个内容分类的分页、五个固定中文关键词，并优先从搜索结果抽样详情。单个点播源可配合 `-SiteKey` 使用 `-VodId '/replay/147781'` 指定详情样本。最多解析该详情前三条线路的首集。直播独立读取每个列表，记录分组、频道与抽样线路。结果包含请求失败原因及字段非空计数。探测运行生产路由，不发送键鼠，也不启动 WebView2 嗅探。

指定 `-SiteKey` 后使用 `-DecodeSample`，可用 LibVLC 虚拟输出后台解码直链，记录播放时钟、解码帧和音频缓冲。单个直播源可以通过 `-LiveChannel '春晚1983'` 按频道名抽样。桌面渲染、操作、WebView2 播放和 HDR 仍需使用对应回归验证。

## 还原判定

| 环节 | 升级为 PASS 的证据 |
| --- | --- |
| 首页 | 同一时间保存原站与客户端的栏目、分类和卡片样本，核对顺序、封面地址及显示、标题、年份、备注和评分 |
| 分类 | 同一分类对照第 1、2、末页的 ID 和总量，检查原站提供的筛选是否透传、末页是否停止加载 |
| 搜索 | 比较山海、庆余年、三体、繁花、流浪地球的结果与字段，记录站点重试、历史、同片多来源和返回位置 |
| 详情 | 按影片 ID 对照片名、年份、地区、类型、演职员、完整简介、线路与集数，验证收藏、下载和投屏入口 |
| 播放 | 用样本实播，记录线路、选集、请求头、播放时钟、画质和轨道切换、倍速和续播。对 HDR 样本记录实际输出 |
| 直播 | 对照原列表的分组、频道、台标、EPG、同名多线路。对样本逐条起播并验证切换。搜索栏只筛选本地已加载频道 |

`SAMPLE` 表示已有客户端响应或解析记录，原版对照或实播仍待完成。`FAIL` 表示观察到明确差异。`BLOCKED` 表示上游响应、账号、公开实现或运行环境限制了验证，详细原因见行内证据。`PENDING` 表示缺少该环节样本。`N/A` 表示配置或协议没有这个能力。

已知缺口包括缺少公开适配器的 15 个点播项、立播配置入口返回 HTTP 403、奥特搜索要求验证码、部分夸克线路需要真实账号，以及尚缺实播的直播频道。`alllive` 和吃瓜目前都经 Java 桥接使用 Live88，原始接口还有待核对。实际起播需要播放时钟、解码或桌面实播记录。

有声小说原入口 HTTP 连接没有收到页面，HTTPS 与带同名称的备用域名均在 TLS 握手阶段失败。糯米首页和分类入口也出现 TLS 握手失败。爱刷的原抖音列表请求返回 HTTP 404。这些结果来自本机当次复查，对应站点的原始目录和实播状态见矩阵。[上游复查](../artifacts/qa-site-fidelity-20261121/upstream-recheck/origin-status.log)
