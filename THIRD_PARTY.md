# 第三方代码、服务与许可说明

流光影院不是下列上游项目的官方分支。本文记录源码复用、开源依赖与公开服务边界，不表示上游作者为本项目背书。

## 代码与依赖复用

| 上游 | 本项目中的用途 | 来源与许可 |
| --- | --- | --- |
| FongMi/TV | TVBox 配置协议、交互与兼容性参考；未复制其完整 Android 应用 | [FongMi/TV](https://github.com/FongMi/TV)，[GPL-3.0](https://github.com/FongMi/TV/blob/fongmi/LICENSE.md) |
| yagami-hue/MisaTV | 第三方安卓 TVBox 站点通用适配方案架构参考；未复制源码 | [yagami-hue/MisaTV](https://github.com/yagami-hue/MisaTV)，GPL-3.0（仅作架构调研参考） |
| FongMi/CatVodSpider | Java 爬虫、Bean、网络与工具类快照（由 `engine/catvod/pom.xml` 选择编入） | 固定提交 [6a43253](https://github.com/FongMi/CatVodSpider/commit/6a43253e48c9e6cd81bba19440a794448411c45f)，快照位于 `third_party/v2024`（上游未附可识别许可，本项目不据此授予额外权利） |
| AndroidCatVodSpider | 夸克、UC、Libvio、ChangZhang 等实现参考与工具类 | [lushunming/AndroidCatVodSpider](https://github.com/lushunming/AndroidCatVodSpider)，固定提交 [8415d16](https://github.com/lushunming/AndroidCatVodSpider/commit/8415d161e217118e5560d2e608df9948dfbc417e)，快照位于 `third_party/quark`（上游当前分支提供 MIT） |
| bizhangjie/CatVodSpider | 公开 Spider 实现参考 | [bizhangjie/CatVodSpider](https://github.com/bizhangjie/CatVodSpider)，MIT（原文留存 `third_party/bizhangjie/CatVodSpider-main/LICENSE`） |
| CatVodTVSpider | CatVod 架构与接口来源 | [CatVodTVOfficial/CatVodTVSpider](https://github.com/CatVodTVOfficial/CatVodTVSpider) |
| Jint & Acornima | C# 托管 JavaScript 沙盒（`drpy2 js:` / `CatVod-JS` 规则执行） | [sebastienros/jint](https://github.com/sebastienros/jint) 4.18.1（BSD-2-Clause）、Acornima 1.9.1（BSD-3-Clause） |
| dex2jar (`dex-translator`) | Java 引擎将外部安卓 DEX 字节码实时转换为 JVM 类文件 | [dex2jar](https://github.com/pxb1988/dex2jar) 2.4.37，Apache-2.0 |
| BouncyCastle (`bcprov-jdk18on`) | 外部安卓爬虫加密与签名兼容 | [Bouncy Castle](https://www.bouncycastle.org/) 1.87，MIT 兼容许可 |
| VideoLAN / LibVLCSharp | 音视频硬件/软件解码、多音轨、字幕与 Windows 渲染 | LibVLC 3.0.24、LibVLCSharp.WinUI 3.10.0（[源码说明](third_party/VideoLAN/SOURCE.md)，LGPL-2.1-or-later） |
| .NET / WinUI / NuGet 包 | 桌面 UI、SQLite、AngleSharp、SkiaSharp、QRCoder 等 | 版本锁定于 `src/TV/packages.lock.json`；各包许可随安装包 `licenses/` 目录分发 |
| CPython 3.14.10 x64 嵌入版 | 无系统 Python 时运行兼容的 TVBox Python 脚本 | Python Software Foundation License。官方 ZIP 的许可随 `runtime/python/LICENSE.txt` 分发。[Python.org](https://www.python.org/downloads/release/python-31410/) |
| Microsoft Edge WebView2 Evergreen 引导程序 | 缺少 WebView2 时按需安装浏览器运行时 | 微软签名的可再分发引导程序。[Microsoft Learn](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) |
| TagLibSharp 2.3.0 | 读取音频自带的歌词、封面与曲目信息 | LGPL-2.1-only。[对应源码](https://github.com/mono/taglib-sharp/tree/TaglibSharp-2.3.0.0)，[NuGet 包](https://www.nuget.org/packages/TagLibSharp/2.3.0) |
| MonoTorrent 3.0.2 | 分片校验、磁力元数据与可拖动的媒体流 | MIT。[对应源码](https://github.com/alanmcgovern/monotorrent/tree/e78faebd0aec117146cffccaaea987ab0629eec0)，[NuGet 包](https://www.nuget.org/packages/MonoTorrent/3.0.2) |
| Inno Setup 简体中文翻译 | 安装器与增量更新器简体中文界面 | [Inno-Setup-Chinese-Simplified-Translation](https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation)，MIT（留存 `installer/Languages`） |

`engine/catvod/src` 另含桌面 JSON IPC 桥接、Android 兼容垫片与项目维护补丁（启用 JVM 默认 TLS 校验并限制跨域凭据转发）。实际编入依赖以 `engine/catvod/pom.xml` 与 `src/TV/TV.csproj` 为准。

## 公开配置、资源包与在线服务

- **配套仓库资源包**：[binchen6/Liuguang-RepoPacks](https://github.com/binchen6/Liuguang-RepoPacks) 为本项目维护的独立开源适配规则仓，仅包含声明式规则与 SHA-256 清单，不托管任何影视文件。
- **公开 TVBox 配置**：默认内置配置由 `SourceConfigService.BuiltInEndpoints` 读取并缓存；公开配置收录可参考 [gaotianliuyun/gao](https://github.com/gaotianliuyun/gao) 与 [cyao2q/files](https://github.com/cyao2q/files)（第三方仓库）。
- **个人网盘与 B 站**：阿里、夸克、UC、百度网盘及 B 站均调用各自公开/官方接口，凭据由用户本地提供，遵循各服务方条款。
- **弹弹play 开放弹幕网络**：[doc.dandanplay.com/open](https://doc.dandanplay.com/open/)，用于可选的在线弹幕匹配与拉取。
- **阿里云百炼 / DashScope API**：用于可选的 AI 字幕生成、翻译与实时字幕，由用户自行申请 API Key 并按官方计费与服务条款使用。
- **豆瓣**：用于公开元数据与推荐信息展示，相关权利归原权利人。
- **在线音乐元数据**：手动匹配时使用 [LRCLIB](https://lrclib.net/docs) 获取歌词，通过 [MusicBrainz](https://musicbrainz.org/doc/MusicBrainz_API) 与 [Cover Art Archive](https://musicbrainz.org/doc/Cover_Art_Archive/API) 获取匹配封面。相关权利归原权利人。

## 许可边界

根目录 `LICENSE`（MIT）仅适用于本项目原创代码；第三方组件、配置、字体、图标、服务与媒体内容适用各自条款。
