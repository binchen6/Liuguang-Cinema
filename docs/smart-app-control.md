# Windows 智能应用控制（Smart App Control）与签名说明

Windows 11 的 **智能应用控制（Smart App Control, SAC）** 运行在内核代码完整性（`CI.dll`）层，开启状态下不提供“仍要运行”选项，仅在满足以下任一条件时放行：

1. **受 Microsoft Trusted Root Program 信任的 RSA Authenticode 代码签名**；或
2. **Microsoft Security Intelligence 云端信誉预测为安全**。

---

## 1. 工程与打包层已落实的治理

1. **98.4% 微软签名 PE 覆盖率**：`scripts/build-installer.ps1` 在 `jlink` 后恢复 Microsoft Build of OpenJDK 全部 67 个原生签名 PE 并清理未使用的 `jrunscript.exe` / `keytool.exe`（已签名占比 726 / 738）。
2. **应用本地官方 VC++ 运行库**：内置 6 个微软签名 CRT DLL（~1.5 MB），移除嵌套 `vc_redist.x64.exe` 与 UAC 提权。
3. **完整 PE 元数据与 `asInvoker` 清单**：补齐主程序、DLL 与安装器版本/发布者元数据，`app.manifest` 声明免提权运行。
4. **双层 Authenticode 签名与开源构建溯源**：支持 `-CertThumbprint` / `-PfxPath`（或 `TV_SIGN_*` 环境变量）自动签署所有未签名 PE、卸载器与安装包（含 RFC 3161 时间戳）；`.github/workflows/release.yml` 集成 **GitHub Build Provenance Attestation**。

---

## 2. 维护者彻底通过 SAC 的官方路径

- **SignPath Foundation（推荐，开源免费）**：申请免费开源 RSA 代码签名并接入 `.github/workflows/release.yml`。
- **Microsoft Trusted Signing / 商业 RSA 证书**：配置 GitHub Secrets（`SIGN_PFX_BASE64` / `SIGN_PFX_PASSWORD`）或本地运行 `build-installer.ps1 -CertThumbprint "<SHA1>"`。
- **Microsoft Store（MSIX）**：由微软商店自动重签，天然免拦截通过 SAC。
- **Microsoft WDSI 提报**：发版后向 [Microsoft Security Intelligence](https://www.microsoft.com/en-us/wdsi/filesubmission) 提交 `TV-Setup-<version>-x64.exe` 建立云端信誉。

---

## 3. 开启 SAC 设备上的用户合规指引

1. **优先核对发布页 SHA-256**，确认安装包完整未被篡改；
2. **本机源码构建（推荐）**：按 [README](../README.md#从源码构建) 执行本地 `dotnet build` 或 `build-installer.ps1`，本机编译产物通常可直接运行；
3. 需在所有开启 SAC 的设备上免提示部署时，请使用已签发可信 Authenticode 证书的发行包。
