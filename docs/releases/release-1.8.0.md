# 流光影院 1.8.0 发行说明

发布日期：2026-11-22

## 核心更新

### 1. 封面加载速度大幅优化
- 为 `ImageCacheService` 建立独立高速 HTTP/2 图片连接池（每主机最高 24 并发、5 秒建连与 8 秒快失败超时），不再与站点 API/会话查询争抢连接。
- 取消首帧渲染路径上的同步 SkiaSharp 重编码与同步磁盘写入：图片下载后在后台线程准备 WinRT 流并立即交由原生 WIC 解码首屏展示，缩略图压缩与落盘改为后台异步执行；遇到特殊编码格式（如异常 WebP/AVIF）自动降级 Skia 转码。
- 新增失效图片短时负缓存（2 分钟 TTL），避免反复请求已失效封面拖慢列表；内存 LRU 容量提升至 240 张 / 5400 万像素。
- 在点播首页、分页追加与搜索结果到达时自动预取首屏封面；为 `PosterImage` 新增 `CachedSource` 附加属性，媒体库（继续观看/收藏）与直播频道台标全面接入缓存与主流 CDN Referer。

### 2. 播放器响应速度与弹幕渲染修复
- **播放 / 暂停零延迟响应**：点击播放/暂停键、中央大播放键或按空格时，立即乐观同步按钮图标、中央大键与弹幕冻结合帧状态，并增加 650ms 过期快照防抖，杜绝旧快照回弹闪烁。
- **原生命令优先级与合并队列**：`PlayerService` 新增优先级控制泵，对连续音量/进度/字幕延迟/音频延迟拖动做自动合并，避免快速拖动滑块堵塞命令队列；`SetPause` 采用确定性显式暂停并最高优先执行。
- **修复进度条拖动不跟手问题**：为进度条 `Slider` 注册 `handledEventsToo: true` 指针事件，支持实时拖动预览时间与精准松手跳转。
- **彻底解决弹幕滚动到一半回退/重滚问题**：重写 `DanmakuOverlay` 时钟引擎为严格单调的 PLL 追频时钟（`DanmakuClock`），基于权威媒体时间差检测真实 Seek，采用非负增量步进更新滚动坐标 `CurrentX`，彻底消除因 LibVLC 粗粒度 PTS 刷新或时钟漂移导致的弹幕中段清屏重滚与回退。
- **全站控件点击即时响应**：弹幕开关改为即时切换并后台防抖落盘；字幕/弹幕设置浮层去除多余异步延迟帧，字幕速度拖动支持实时预览与防抖重烘焙；缓存点播卡片/侧栏/设置分区的视觉子元素，投屏预览播放键即时反馈。

### 3. 功能入口层级与整体排版优化
- **侧栏导航升级**：新增顶栏「全站搜索 (`Ctrl+F`)」快捷入口，并将「云盘」提升为一级侧栏入口（位于媒体库与投屏之间），无需再经多级菜单进入个人云盘。
- **播放器菜单收敛**：将右上角「更多播放选项」按轨道、AI 智能字幕、画面与同步、辅助工具重新分组；常用字幕时间轴与 AI 功能集中于底栏「字幕」面板。
- **页面排版统一**：媒体库「继续观看」升级为自适应多列网格卡片，充分利用宽屏空间；设置页「存储」分区全面统一为 `SettingsCardStyle` 卡片样式，并为超宽屏约束最大阅读列宽（920 DIP）。

### 4. Windows 智能应用控制（SAC）治理与通用兼容增强
- **恢复 67 个 Microsoft 签名的 Java 运行时二进制**：`build-installer.ps1` 在 `jlink` 后自动从 Microsoft Build of OpenJDK 还原全部 Microsoft 官方签名 PE 文件并清理无用 CLI 工具，使发布包内已签名二进制占比提升至 **726 / 738（98.4%）**，显著缩小 SAC 拦截面。
- **补全 PE 发布者元数据与 `asInvoker` 清单**，内置双层 Authenticode 签名参数（`-CertThumbprint` / `-PfxPath` + `SignedUninstaller=yes`）与 GitHub Actions 发布流水线（含 Build Provenance Attestation）；详见 [Windows 智能应用控制说明](../smart-app-control.md)。
- **跨电脑通用兼容加固**：
  - 随应用内置 6 个 Microsoft 签名应用本地 VC++ x64 CRT DLL（~1.5 MB），无需管理员权限即可在纯净 Windows 10/11 环境及便携版 ZIP 中直接运行；
  - 修复多显示器窗口位置恢复的坐标双重叠加 Bug；
  - 兼容 Windows 10 / 虚拟机 / RDP（`MicaController.IsSupported()` 降级、HDR 异常容错、标题栏定制容错、单实例注册降级）；
  - 显式绑定内置 `libvlc/win-x64` 目录，隔离宿主机预装 VLC 与全局 `_JAVA_OPTIONS` / `JAVA_TOOL_OPTIONS`；
  - 自定义 DNS 自动放行 `localhost`、单标签与 `.local` / `.lan` 局域网地址；
  - WASAPI 实时字幕音频捕获新增 24-bit PCM 支持与驱动不兼容自动回退。

### 5. 安装包“秒开”优化与防回退守卫
- 安装包改为 `InternalCompressLevel=none`、`SolidCompression=no`、`Compression=lzma2/fast`，已压缩资源采用 `nocompression` 直接存储，并移除嵌套 `vc_redist.x64.exe` 与未使用的 ANGLE/光盘/编码器 LibVLC 插件，安装包瘦身至 **138.03 MB**（较 v1.7.0 减少 10.65 MB），实测全新安装程序首次弹出仅 **417 ms**、热启动 **285 ms**（相比固实压缩方案的 7.34 秒提速 **17.6 倍**）。
- 新增 `scripts/Test-InstallerLaunchSpeed.ps1` 自动化启动耗时基准与结构守卫，后续版本严禁回退安装包打开速度。
- 同步提供便携免安装压缩包 `TV-1.8.0-win-x64-portable.zip`（188.14 MB）。
