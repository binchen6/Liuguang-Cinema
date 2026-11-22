# Windows 智能应用控制（Smart App Control）适配与签名说明

Windows 11 的 **智能应用控制（Smart App Control, SAC）** 运行在内核代码完整性（`CI.dll`）层。与传统的 SmartScreen 不同，当 SAC 处于「开启」状态时，它不会提供“仍要运行”按钮，而是严格依据以下两项之一放行可执行文件与 DLL：

1. **受 Microsoft Trusted Root Program 信任的 RSA 代码签名（Authenticode）**；或
2. **微软云端安全情报服务（Microsoft Security Intelligence）给出的明确安全信誉预测**。

---

## 一、本项目在工程与打包层面已完成的处理（v1.8.0+）

为最大程度消除误报、降低 SAC 拦截面并为正式代码签名铺平道路，流光影院已在构建与打包链路中落实以下措施：

1. **恢复全部 Microsoft 签名的 Java 运行时二进制**：
   - `jlink` 默认从 `.jmod` 归档提取未签名的 `java.exe` / `jvm.dll` 等 67 个原生 PE 文件，会直接触发 SAC Event 3077 拦截爬虫引擎。
   - `scripts/build-installer.ps1` 现在会在 `jlink` 之后自动从 Microsoft Build of OpenJDK 还原全部 67 个原始 **Microsoft Corporation 官方签名** 二进制，并移除未使用的 `jrunscript.exe` / `keytool.exe`，使发布包内已签名二进制覆盖率达到 **98.4%（726 / 738）**。
2. **移除嵌套 `.exe` 安装器，改用官方 Microsoft 签名应用本地 VC++ 运行库**：
   - 以 6 个仅 1.5 MB、由 Microsoft 签名的应用本地 CRT DLL（`vcruntime140*.dll`、`msvcp140*.dll`、`concrt140.dll`）替代原有的 24.45 MB 嵌套 `vc_redist.x64.exe`。
   - 安装过程不再嵌套释放外部 `.exe`，也不再触发 UAC 提权（`runas`），显著减少 Defender / SAC 启发式扫描。
3. **补全完整 PE 版本元数据与 `asInvoker` 清单**：
   - `TV.exe`、`TV.dll` 与 `TV-Setup-*-x64.exe` 均包含完整的产品名、发布者、版权、版本与仓库 URL 元数据，并在 `src/TV/app.manifest` 声明 `<requestedExecutionLevel level="asInvoker" uiAccess="false" />`。
4. **双层 Authenticode 签名流水线（开箱即用）**：
   - `scripts/build-installer.ps1` 与 `installer/TV.iss` 已完整支持 `-CertThumbprint` / `-PfxPath`（或环境变量 `TV_SIGN_THUMBPRINT` / `TV_SIGN_PFX` / `TV_SIGN_PASSWORD`）双层签名：
     - 打包前自动扫描并签发发布目录内全部未签名的 `.exe` / `.dll`；
     - 通过 Inno Setup `SignTool` + `SignedUninstaller=yes` 对卸载器与最终 `TV-Setup-<version>-x64.exe` 执行 SHA-256 + RFC 3161 时间戳签名。
   - `.github/workflows/release.yml` 同时集成 **GitHub Build Provenance Attestation**（`actions/attest-build-provenance`）以提供可验证的开源构建溯源。

---

## 二、维护者彻底消除 SAC 拦截的官方路径

由于开源项目本地构建默认不含商业代码签名证书，在公开发布二进制时，可通过以下任一微软官方认可途径彻底通过 SAC：

### 方案 A：SignPath Foundation（推荐，开源项目免费）

1. 向 [SignPath Foundation](https://signpath.org/) 提交本项目公开仓库申请免费开源 RSA 代码签名。
2. 审批通过后，在 GitHub Actions（`.github/workflows/release.yml`）中配置 SignPath 签发步骤，自动对构建产物执行双层签名。

### 方案 B：Microsoft Trusted Signing / 商业 RSA 代码签名证书

1. 在 GitHub 仓库 Secrets 中配置：
   - `SIGN_PFX_BASE64`（RSA 代码签名证书的 Base64 编码）
   - `SIGN_PFX_PASSWORD`
2. 或在本地发布时执行：

```powershell
pwsh -NoProfile -File ./scripts/build-installer.ps1 -CertThumbprint "<SHA1_THUMBPRINT>"
```

### 方案 C：发布到 Microsoft Store（MSIX，微软免费重签）

通过 Microsoft Store 分发的 MSIX 包由微软免费自动签名，天然免拦截通过 Windows 11 SAC。

### 方案 D：向 Microsoft Security Intelligence 提交发行包建立云端信誉

每次发布后，维护者可将发行版 `TV-Setup-<version>-x64.exe`（并核对 SHA-256）以 **Software Developer** 身份提交至 [Microsoft WDSI 文件提交入口](https://www.microsoft.com/en-us/wdsi/filesubmission)，帮助微软云端安全服务尽早建立安全信誉。

---

## 三、用户在启用 SAC 的 Windows 11 设备上的合规选项

如果当前下载的是尚未附带商业签名的社区构建版本，并遇到 Windows 智能应用控制拦截：

1. **优先核对 SHA-256 校验值**，确认文件完整未被篡改；
2. **从源码本地构建（推荐）**：按照 [README](../README.md#从源码构建) 在本机执行 `dotnet build` 或 `scripts/build-installer.ps1`，本机编译的二进制通常可直接运行；
3. 如确认信任本项目开源构建并希望直接运行社区发行包，可在 **Windows 安全中心 → 应用和浏览器控制 → 智能应用控制设置** 中根据自身安全策略进行调整（注意：在 Windows 11 中关闭 SAC 后需重装系统才能重新开启，请谨慎决定）。
