# Windows 智能应用控制（Smart App Control）适配与签名说明

Windows 11 的 **智能应用控制（Smart App Control, SAC）** 运行在内核代码完整性（`CI.dll`）层。与 SmartScreen 不同，开启状态下不提供“仍要运行”按钮，仅在满足以下任一条件时放行可执行文件与 DLL：

1. **受 Microsoft Trusted Root Program 信任的 RSA Authenticode 代码签名**；或
2. **微软云端安全情报服务（Microsoft Security Intelligence）给出明确安全信誉预测**。

---

## 1. 工程与打包层已落实的治理（v1.8.0+）

1. **还原 Microsoft 官方签名 Java 运行时**：`scripts/build-installer.ps1` 在 `jlink` 后自动从 Microsoft Build of OpenJDK 恢复全部 67 个原生签名 PE 文件并移除未使用的 `jrunscript.exe` / `keytool.exe`，使发布包内已签名二进制占比达 **98.4%（726 / 738）**。
2. **内置官方应用本地 VC++ 运行库**：随应用附带 6 个仅 ~1.5 MB、由 Microsoft 签名的 CRT DLL，移除 24.45 MB 嵌套 `vc_redist.x64.exe` 与 UAC 提权，显著降低启发式拦截面。
3. **完整 PE 元数据与 `asInvoker` 清单**：主程序、核心 DLL 与安装器均补齐产品、发布者、版权、版本与仓库 URL 信息，`app.manifest` 声明 `asInvoker`。
4. **开箱即用的双层 Authenticode 签名与构建溯源**：
   - `build-installer.ps1` 与 `installer/TV.iss` / `TV-Update.iss` 支持 `-CertThumbprint` / `-PfxPath`（或环境变量 `TV_SIGN_THUMBPRINT` / `TV_SIGN_PFX` / `TV_SIGN_PASSWORD`），打包前自动签发所有未签名 PE，并通过 `SignedUninstaller=yes` + RFC 3161 时间戳签署卸载器与安装包；
   - `.github/workflows/release.yml` 集成 **GitHub Build Provenance Attestation** 提供公开可验证的开源构建溯源。

---

## 2. 维护者消除 SAC 拦截的官方路径

开源本地构建默认不含商业代码签名证书，公开发行可通过以下任一微软官方路径彻底通过 SAC：

- **SignPath Foundation（推荐，开源免费）**：向 [SignPath Foundation](https://signpath.org/) 申请免费开源 RSA 代码签名，并在 `.github/workflows/release.yml` 接入自动签发。
- **Microsoft Trusted Signing / 商业 RSA 证书**：在 GitHub Secrets 配置 `SIGN_PFX_BASE64` 与 `SIGN_PFX_PASSWORD`，或本地执行：
  ```powershell
  pwsh -NoProfile -File ./scripts/build-installer.ps1 -CertThumbprint "<SHA1_THUMBPRINT>"
  ```
- **Microsoft Store（MSIX）**：通过商店分发由微软免费自动重签，天然免拦截通过 SAC。
- **Microsoft Security Intelligence 提报**：每次发版后以 *Software Developer* 身份向 [Microsoft WDSI 文件提交入口](https://www.microsoft.com/en-us/wdsi/filesubmission) 提交 `TV-Setup-<version>-x64.exe` 建立云端信誉。

---

## 3. 开启 SAC 设备上的用户合规指引

若在启用 SAC 的 Windows 11 上运行未附带商业签名的社区发行包并遇到拦截：

1. **优先核对发布页 SHA-256**，确认安装包完整未被篡改；
2. **本机源码构建（推荐）**：按 [README](../README.md#从源码构建) 执行 `dotnet build` 或 `scripts/build-installer.ps1`，本机编译产物通常可直接运行；
3. 如确认信任本项目开源代码，可在 **Windows 安全中心 → 应用和浏览器控制 → 智能应用控制设置** 中按自身策略调整（注意：Windows 11 关闭 SAC 后需重装系统方可重新开启，请谨慎决定）。
