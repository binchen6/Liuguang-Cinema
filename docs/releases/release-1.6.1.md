# 流光影院 1.6.1 发布说明

发布日期：2026-11-22

## 本次更新

### 1. 搜索补全不打断输入修复
- **彻底解决输入中断与中文输入法组合态丢失**：移除会抢占焦点的 `FlyoutBase.AttachedFlyout` 搜索建议弹层，改为页面内非抢焦悬浮层（`SuggestionsOverlay`，`AllowFocusOnInteraction="False"`、`IsTabStop="False"`），删除显示建议时的焦点回弹逻辑。
- **IME 组合输入保护**：为 `SearchTextBox` 增加 `IsComposing` 组合态追踪（`TextCompositionStarted` / `TextCompositionEnded`），在中文拼音候选期间不打断输入，候选确认后再平滑刷新搜索建议；保留方向键上下选择、回车确认与 `Esc` 收起体验。

### 2. 新增 6 个热门站点原生适配与实播验证
- **💮文采┃秒播 (`csp_JpysGuard`)**：全新原生 `JpysVodProvider`，对接签名 JSON 接口，支持 6 分类、417 页（10,000 部）片库浏览、5 组全量搜索、完整详情及 1080p 蓝光直链（实测后台解码 3758ms / 230 视频帧）。
- **👀瓜子┃不卡 (`csp_AppgzGuard`)**：全新原生 `GuaziVodProvider`，对接 AES-128-CBC 加密 JSON 接口，支持 10 分类、1,805 页（43,306 部）片库、全量搜索、完整剧集详情及 1080p 蓝光直链（实测后台解码 4154ms / 246 视频帧）。
- **🍊橘猫┃多线 / 剧迷 (`csp_JumiTvGuard`)**：全新原生 `GimyVodProvider`，支持 9 分类（含陆剧/韩剧/美剧/日剧/台剧）、分页、全量搜索、多集详情与 `/_watch/` 多线自动择优直链解析（实测后台解码 4238ms / 290 视频帧）。
- **🐱韩迷┃外剧 (`csp_HanquanGuard`)**：全新原生 `HanjuVodProvider`，支持韩剧/韩国电影/韩综分类分页、POST 搜索、详情与 AES-256-CBC 解密 HLS 直链（实测后台解码 3818ms / 228 视频帧）。
- **📺热播┃多线 (`csp_AppTTGuard`)**：全新原生 `ReboVodProvider`，支持 5 分类分页、搜索、多线路详情，原生解析内嵌夸克网盘分享并支持 Artplayer 线路嗅探（`parse=1`）。
- **💡聚剧┃四盘 (`csp_SeedhubGuard`)**：全新原生 `SeedhubVodProvider`，接入 `SiteVerificationRequiredException` 浏览器安全验证交互弹窗与四网盘分享解析。
- 同步在 `SpiderCatalog` 登记上述全部原生站点及 `Auete` / `Bttwoo`，移除站源选择器中的 `(无公开实现)` 标记。
