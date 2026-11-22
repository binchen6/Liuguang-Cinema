[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$MavenOffline,
    [switch]$NoRestore,
    [switch]$SkipPortableZip,
    [string]$ReleaseDirectory,
    [string]$CertThumbprint = $env:TV_SIGN_THUMBPRINT,
    [string]$PfxPath = $env:TV_SIGN_PFX,
    [string]$PfxPassword = $env:TV_SIGN_PASSWORD,
    [string]$SignToolPath = $env:TV_SIGNTOOL_PATH,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$defaultRelease = Join-Path $artifacts 'installers'
$release = if ($ReleaseDirectory) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReleaseDirectory)
} else { $defaultRelease }
$release = (New-Item -ItemType Directory -Force -Path $release).FullName
if (-not ('TV.InstallerBuild.InstallerOutputIntegrity' -as [type])) {
    Add-Type -Path (Join-Path $root 'installer/InstallerOutputIntegrity.cs')
}

function Assert-ReleaseIntegrity([string]$Path) {
    if ([TV.InstallerBuild.InstallerOutputIntegrity]::IsBelowMedium($Path)) {
        throw "Windows 低完整性标记会限制安装。请在普通 Windows 会话中使用 -ReleaseDirectory 发布。当前路径为 $Path"
    }
}

function Resolve-SignTool([string]$ExplicitPath) {
    if ($ExplicitPath -and (Test-Path -LiteralPath $ExplicitPath)) { return (Resolve-Path -LiteralPath $ExplicitPath).Path }
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kits) {
        $found = Get-ChildItem -LiteralPath $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

# Structural non-regression guard: installer must keep instant-launch configuration
$issPath = Join-Path $root "installer/TV.iss"
$issRaw = Get-Content -LiteralPath $issPath -Raw
if ($issRaw -notmatch '(?m)^InternalCompressLevel=none\s*$' -or $issRaw -notmatch '(?m)^SolidCompression=no\s*$') {
    throw "Installer fast-launch guard failed: installer/TV.iss must define InternalCompressLevel=none and SolidCompression=no."
}

Assert-ReleaseIntegrity $release
$dotnet = Join-Path $root ".dotnet/dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$publishId = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$publish = Join-Path $root "artifacts/publish-$publishId"
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid application version: $version" }
$installerName = "TV-Setup-$version-x64"
$installer = Join-Path $release "$installerName.exe"
$portableName = "TV-$version-win-x64-portable"
$portableZip = Join-Path $release "$portableName.zip"
New-Item -ItemType Directory -Force -Path $publish | Out-Null
if (-not $env:ProgramFiles) { $env:ProgramFiles = "C:\Program Files" }
if (-not ${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} = "C:\Program Files (x86)" }
if (-not $env:ProgramData) { $env:ProgramData = "C:\ProgramData" }
if (-not $env:LOCALAPPDATA -or -not (Test-Path $env:LOCALAPPDATA)) {
    $env:LOCALAPPDATA = [Environment]::GetFolderPath("LocalApplicationData")
}

# Prefer Microsoft Build of OpenJDK (binaries are Microsoft-signed, improving Windows Smart App Control compatibility)
$jdkCandidates = @()
if (Test-Path "$env:ProgramFiles\Microsoft") {
    $jdkCandidates += Get-ChildItem "$env:ProgramFiles\Microsoft\jdk-17*" -Directory -ErrorAction SilentlyContinue
}
$jdk17 = Join-Path $env:USERPROFILE ".jdks/jdk-17"
if (Test-Path $jdk17) { $jdkCandidates += Get-Item $jdk17 }
if (Test-Path "$env:ProgramFiles\Microsoft") {
    $jdkCandidates += Get-ChildItem "$env:ProgramFiles\Microsoft\jdk-*" -Directory -ErrorAction SilentlyContinue
}
$javaHome = $jdkCandidates |
    Where-Object { Test-Path (Join-Path $_.FullName "bin/jlink.exe") } |
    Select-Object -First 1
if (-not $javaHome) { throw "A JDK with jlink is required to build the Java engine and runtime." }
$env:JAVA_HOME = $javaHome.FullName
$env:Path = "$env:JAVA_HOME\bin;$env:Path"
$mvn = (Get-Command mvn -ErrorAction Stop).Source
$mavenArgs = @('-q', '-DskipTests', '-f', (Join-Path $root "engine/catvod/pom.xml"), 'package')
if ($MavenOffline) { $mavenArgs = @('-o') + $mavenArgs }
& $mvn @mavenArgs
if ($LASTEXITCODE -ne 0) { throw "Maven build failed with exit code $LASTEXITCODE" }

$restoreOptions = @()
if ($NoRestore) { $restoreOptions += '--no-restore' }
& $dotnet build (Join-Path $root "src/TV/TV.csproj") --configuration $Configuration @restoreOptions
if ($LASTEXITCODE -ne 0) { throw "Current build failed with exit code $LASTEXITCODE" }

& $dotnet publish (Join-Path $root "src/TV/TV.csproj") `
    --configuration $Configuration --runtime win-x64 --self-contained true --output $publish @restoreOptions `
    -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
$currentOutput = Join-Path $root "src/TV/bin/$Configuration/net10.0-windows10.0.19041.0/win-x64"
Get-ChildItem $currentOutput -Recurse -File | Where-Object { $_.Extension -in @(".xbf", ".pri") } | ForEach-Object {
    $relative = $_.FullName.Substring($currentOutput.Length + 1)
    $target = Join-Path $publish $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
    Copy-Item $_.FullName $target -Force
}

$runtime = Join-Path $publish "runtime"
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
$jar = Join-Path $root "engine/catvod/target/catvod-engine-1.0.0.jar"
if (-not (Test-Path $jar)) { throw "CatVod JAR is missing: $jar" }
Copy-Item $jar (Join-Path $runtime "catvod-engine.jar") -Force

$javaOutput = Join-Path $runtime "java"
$modules = "java.base,java.datatransfer,java.xml,java.prefs,java.desktop,java.logging,java.security.sasl,java.naming,java.net.http,java.scripting,java.transaction.xa,java.sql,jdk.charsets,jdk.crypto.ec,jdk.crypto.mscapi,jdk.httpserver,jdk.localedata,jdk.unsupported,jdk.zipfs"
& "$env:JAVA_HOME\bin\jlink.exe" --add-modules $modules --compress=2 --no-header-files --no-man-pages --strip-debug --output $javaOutput
if ($LASTEXITCODE -ne 0) { throw "jlink failed with exit code $LASTEXITCODE" }

# Remove unused JDK CLI utilities that are never invoked by the app
foreach ($unusedTool in "jrunscript.exe", "keytool.exe") {
    $toolPath = Join-Path $javaOutput "bin\$unusedTool"
    if (Test-Path -LiteralPath $toolPath) { Remove-Item -LiteralPath $toolPath -Force }
}

# Restore original Microsoft-signed binaries from JAVA_HOME\bin onto jlink output
# (jlink extracts unsigned PE files from .jmod archives, which triggers Windows Smart App Control!)
$javaBinSrc = Join-Path $env:JAVA_HOME "bin"
$javaBinDst = Join-Path $javaOutput "bin"
$restoredSignedJava = 0
Get-ChildItem -LiteralPath $javaBinDst -Recurse -File -Include *.exe,*.dll | ForEach-Object {
    $rel = $_.FullName.Substring($javaBinDst.Length + 1)
    $srcFile = Join-Path $javaBinSrc $rel
    if (Test-Path -LiteralPath $srcFile) {
        $sig = Get-AuthenticodeSignature -LiteralPath $srcFile
        if ($sig.Status -eq 'Valid') {
            Copy-Item -LiteralPath $srcFile -Destination $_.FullName -Force
            $restoredSignedJava++
        }
    }
}
Write-Host "Restored $restoredSignedJava Authenticode-signed Java runtime binaries from $($env:JAVA_HOME)" -ForegroundColor Cyan

# App-local Visual C++ x64 Runtime DLLs (Microsoft-signed, ~1.5 MB total):
# Replaces the 24.45 MB nested vc_redist.x64.exe — enables instant installer launch, zero UAC prompt,
# and universal compatibility on clean Windows 10/11 PCs (including Portable ZIP).
$crtDlls = @("vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "concrt140.dll")
$system32 = Join-Path $env:SystemRoot "System32"
$copiedCrt = 0
foreach ($crt in $crtDlls) {
    $src = Join-Path $system32 $crt
    if (-not (Test-Path -LiteralPath $src)) {
        throw "Required Microsoft VC++ Runtime DLL missing on build host: $src"
    }
    $sig = Get-AuthenticodeSignature -LiteralPath $src
    if ($sig.Status -ne 'Valid') {
        throw "VC++ Runtime DLL failed Authenticode verification: $src ($($sig.Status))"
    }
    Copy-Item -LiteralPath $src -Destination (Join-Path $publish $crt) -Force
    $copiedCrt++
}
Write-Host "Bundled $copiedCrt Microsoft-signed app-local VC++ CRT DLLs." -ForegroundColor Cyan

$publishPath = (Resolve-Path -LiteralPath $publish).Path.TrimEnd('\') + '\'
function Remove-PublishDirectory([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $item = Get-Item -LiteralPath $path
    if (-not $item.FullName.StartsWith($publishPath, [StringComparison]::OrdinalIgnoreCase) -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Invalid publish cleanup path: $path" }
    Remove-Item -LiteralPath $item.FullName -Recurse -Force
}

$prunePatterns = @(
    "DirectML.dll", "onnxruntime*.dll", "Microsoft.ML.OnnxRuntime.dll", "Microsoft.Windows.AI*",
    "Microsoft.Windows.Vision*", "Microsoft.Windows.Internal.Vision*", "Microsoft.Windows.Workloads*",
    "Microsoft.Windows.Private.Workloads*", "Microsoft.Windows.SemanticSearch*", "Microsoft.Graphics.Imaging*",
    "Microsoft.Windows.Media.Capture*", "Microsoft.Windows.Widgets*", "Microsoft.Windows.AppNotifications*",
    "Microsoft.Windows.PushNotifications*", "Microsoft.Security.Authentication.OAuth*",
    "PushNotificationsLongRunningTask.ProxyStub.dll", "createdump.exe", "mscordbi.dll", "mscordaccore*",
    "Microsoft.DiaSymReader.Native.amd64.dll",
    # Unused ANGLE OpenGL ES native libs (app uses CPU SkiaSharp only)
    "libGLESv2.dll", "libEGL.dll"
)
Get-ChildItem $publish -Recurse -File -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force
foreach ($pattern in $prunePatterns) {
    Get-ChildItem $publish -File -Filter $pattern -ErrorAction SilentlyContinue | Remove-Item -Force
}
Get-ChildItem $publish -Directory | Where-Object { $_.Name -match "^[a-z]{2,3}(-[A-Za-z]{2,8}){1,2}$" -and $_.Name -notin @("zh-CN", "en-us") } |
    ForEach-Object { Remove-PublishDirectory $_.FullName }
$npu = Join-Path $publish "NpuDetect"
Remove-PublishDirectory $npu

$vlc = Join-Path $publish "libvlc/win-x64"
foreach ($architecture in "win-x86", "win-arm64") {
    $path = Join-Path $publish "libvlc/$architecture"
    Remove-PublishDirectory $path
}
foreach ($name in "services_discovery", "lua", "visualization", "logger", "meta_engine", "stream_extractor", "video_splitter") {
    $path = Join-Path $vlc "plugins/$name"
    Remove-PublishDirectory $path
}
# Prune unused disc/capture/broadcast/encoder LibVLC plugins while retaining 100% of HTTP/HLS/DASH/RTSP/file playback,
# GPU hardware decoding (d3d11va/dxva2/qsv), AV1 (dav1d), all subtitle renderers, and AI audio extraction plugins.
$pruneVlcAccess = @(
    "libaccess_srt_plugin.dll", "librist_plugin.dll", "libdcp_plugin.dll",
    "liblibbluray_plugin.dll", "libbluray-j2se-*.jar", "libsftp_plugin.dll",
    "libcdda_plugin.dll", "libdtv_plugin.dll", "libvcd_plugin.dll", "libvdr_plugin.dll",
    "libnfs_plugin.dll", "libdvdnav_plugin.dll", "libdvdread_plugin.dll",
    "libdshow_plugin.dll", "libvnc_plugin.dll", "libsatip_plugin.dll", "libscreen_plugin.dll"
)
foreach ($pattern in $pruneVlcAccess) {
    Get-ChildItem (Join-Path $vlc "plugins/access") -File -Filter $pattern -ErrorAction SilentlyContinue | Remove-Item -Force
}
$pruneVlcCodec = @(
    "libx264_plugin.dll", "libx26410b_plugin.dll", "libx265_plugin.dll",
    "libaom_plugin.dll", "libzvbi_plugin.dll", "libfluidsynth_plugin.dll",
    "libtwolame_plugin.dll", "libschroedinger_plugin.dll", "libtheora_plugin.dll"
)
foreach ($pattern in $pruneVlcCodec) {
    Get-ChildItem (Join-Path $vlc "plugins/codec") -File -Filter $pattern -ErrorAction SilentlyContinue | Remove-Item -Force
}
# Keep only the 6 minimal LibVLC plugins (~380 KB) required for fast headless AI subtitle audio extraction
$keepVlcPlugins = @{
    "stream_out"    = @("libstream_out_transcode_plugin.dll", "libstream_out_standard_plugin.dll")
    "mux"           = @("libmux_ogg_plugin.dll", "libmux_wav_plugin.dll", "libmux_dummy_plugin.dll")
    "access_output" = @("libaccess_output_file_plugin.dll")
}
foreach ($folder in $keepVlcPlugins.Keys) {
    $dir = Join-Path $vlc "plugins/$folder"
    if (Test-Path -LiteralPath $dir) {
        Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Name -notin $keepVlcPlugins[$folder] } | Remove-Item -Force
    }
}
$lua = Join-Path $vlc "lua"
Remove-PublishDirectory $lua
Get-ChildItem $vlc -File -Filter "*.lib" -ErrorAction SilentlyContinue | Remove-Item -Force
foreach ($native in "libvlc.dll", "libvlccore.dll", "plugins", "TV.exe", "vcruntime140.dll", "msvcp140.dll", "runtime/catvod-engine.jar", "runtime/java/bin/java.exe") {
    if ($native -in @("TV.exe", "vcruntime140.dll", "msvcp140.dll")) { $path = Join-Path $publish $native }
    elseif ($native.StartsWith("runtime/")) { $path = Join-Path $publish $native }
    else { $path = Join-Path $vlc $native }
    if (-not (Test-Path $path)) { throw "Publish asset is missing: $path" }
}

$builtXbf = Join-Path $currentOutput "Views/VodPage.xbf"
$publishedXbf = Join-Path $publish "Views/VodPage.xbf"
if (-not (Test-Path $builtXbf)) { throw "Current XAML output is missing: $builtXbf" }
if (-not (Test-Path $publishedXbf)) { throw "Published XAML output is missing: $publishedXbf" }
if ((Get-FileHash $builtXbf).Hash -ne (Get-FileHash $publishedXbf).Hash) { throw "Published XAML is stale; clean rebuild required." }
$builtPri = Join-Path $currentOutput "TV.pri"
$publishedPri = Join-Path $publish "TV.pri"
if ((Get-FileHash $builtPri).Hash -ne (Get-FileHash $publishedPri).Hash) { throw "Published resource index is stale; clean rebuild required." }

# Optional Dual-Layer Authenticode Signing (signs all unsigned inner EXEs/DLLs + Inno uninstaller + final Setup EXE)
$shouldSign = -not [string]::IsNullOrWhiteSpace($CertThumbprint) -or -not [string]::IsNullOrWhiteSpace($PfxPath)
$resolvedSignTool = $null
$isccSignArgs = @()
if ($shouldSign) {
    $resolvedSignTool = Resolve-SignTool $SignToolPath
    if (-not $resolvedSignTool) { throw "Code signing requested, but signtool.exe was not found. Install Windows SDK Signing Tools or pass -SignToolPath." }
    $baseSignArgs = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256")
    if (-not [string]::IsNullOrWhiteSpace($CertThumbprint)) {
        $baseSignArgs += @("/sha1", $CertThumbprint)
    } else {
        if (-not (Test-Path -LiteralPath $PfxPath)) { throw "Signing PFX file not found: $PfxPath" }
        $baseSignArgs += @("/f", (Resolve-Path -LiteralPath $PfxPath).Path)
        if (-not [string]::IsNullOrWhiteSpace($PfxPassword)) { $baseSignArgs += @("/p", $PfxPassword) }
    }

    $unsignedInner = @(Get-ChildItem -LiteralPath $publish -Recurse -File -Include *.exe,*.dll |
        Where-Object { (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -ne 'Valid' })
    if ($unsignedInner.Count -gt 0) {
        Write-Host "Signing $($unsignedInner.Count) unsigned payload binaries..." -ForegroundColor Cyan
        foreach ($bin in $unsignedInner) {
            & $resolvedSignTool @baseSignArgs $bin.FullName
            if ($LASTEXITCODE -ne 0) { throw "signtool failed on $($bin.FullName) with exit code $LASTEXITCODE" }
        }
    }
    $signCmdParts = @("`"$resolvedSignTool`"") + ($baseSignArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) + @('$f')
    $isccSignArgs = @("/DEnableSignTool=1", "/Ssigntool=$($signCmdParts -join ' ')")
}

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { throw "Inno Setup 6 is required. Install JRSoftware.InnoSetup with winget." }

$notice = Join-Path $publish "licenses"
New-Item -ItemType Directory -Force -Path $notice | Out-Null
Copy-Item (Join-Path $root "LICENSE") (Join-Path $notice "LiuguangCinema.txt")
Copy-Item (Join-Path $root "THIRD_PARTY.md") (Join-Path $notice "THIRD_PARTY.md")
Copy-Item (Join-Path $root "third_party/bizhangjie/CatVodSpider-main/LICENSE") (Join-Path $notice "CatVodSpider.txt")
Copy-Item (Join-Path $root "third_party/VideoLAN") $notice -Recurse

$assets = Get-Content (Join-Path $root "src/TV/obj/project.assets.json") -Raw | ConvertFrom-Json
$packagePaths = @($assets.libraries.PSObject.Properties |
    Where-Object { $_.Value.type -eq 'package' } |
    ForEach-Object { $_.Value.path })
$packagePaths += @($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies |
    Where-Object { $_.name -like '*.Runtime.win-x64' } |
    ForEach-Object { '{0}/{1}' -f $_.name.ToLowerInvariant(), ($_.version.Trim('[', ']') -split ',')[0].Trim() })
foreach ($packagePath in ($packagePaths | Sort-Object -Unique)) {
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $package = Join-Path $folder $packagePath
        if (-not (Test-Path -LiteralPath $package)) { continue }
        $documents = Get-ChildItem -LiteralPath $package -File |
            Where-Object { $_.Name -match '^(LICENSE|LICENCE|NOTICE|COPYING|THIRD[-_. ]?PARTY[-_. ]?NOTICES?)(\.(txt|md|html))?$' }
        if ($documents) {
            $destination = Join-Path $notice $packagePath
            New-Item -ItemType Directory -Force -Path $destination | Out-Null
            $documents | Copy-Item -Destination $destination
        }
        break
    }
}

$setupStage = Join-Path $root "artifacts/installer-$publishId"
New-Item -ItemType Directory -Path $setupStage | Out-Null
$setupOutput = Join-Path $setupStage "$installerName.exe"
$isccArgs = @("/DMyAppVersion=$version", "/DPublishRoot=$publish", "/O$setupStage", "/F$installerName") + $isccSignArgs + @($issPath)
& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
try {
    Assert-ReleaseIntegrity $setupOutput
    Move-Item -LiteralPath $setupOutput -Destination $installer -Force
    Assert-ReleaseIntegrity $installer
} finally {
    if (Test-Path -LiteralPath $setupOutput -PathType Leaf) { Remove-Item -LiteralPath $setupOutput }
}
if ($shouldSign) {
    $finalSig = Get-AuthenticodeSignature -LiteralPath $installer
    if ($finalSig.Status -ne 'Valid') {
        throw "Final installer Authenticode verification failed: $($finalSig.Status)"
    }
    Write-Host "Installer Authenticode signature: VALID ($($finalSig.SignerCertificate.Subject))" -ForegroundColor Green
}

if ($release -eq $defaultRelease) {
    $legacyOutput = (New-Item -ItemType Directory -Force -Path (Join-Path $root 'installer/Output')).FullName
    Assert-ReleaseIntegrity $legacyOutput
    foreach ($alias in (Join-Path $legacyOutput 'TV-Setup-x64.exe'), (Join-Path $release 'TV-Setup-x64.exe')) {
        Copy-Item -LiteralPath $installer -Destination $alias -Force
        Assert-ReleaseIntegrity $alias
    }
}
$file = Get-Item $installer
$hash = Get-FileHash $file.FullName -Algorithm SHA256
Write-Host "Installer created: $($file.FullName)" -ForegroundColor Green
Write-Host ("Size: {0:N0} bytes ({1:N2} MB); SHA256: {2}" -f $file.Length, ($file.Length / 1MB), $hash.Hash) -ForegroundColor Green
"$($hash.Hash.ToLowerInvariant()) *$installerName.exe" | Set-Content "$installer.sha256" -Encoding ascii

if (-not $SkipPortableZip) {
    if (Test-Path -LiteralPath $portableZip) { Remove-Item -LiteralPath $portableZip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $portableZip, [System.IO.Compression.CompressionLevel]::Fastest, $false)
    Assert-ReleaseIntegrity $portableZip
    $zipFile = Get-Item $portableZip
    $zipHash = Get-FileHash $zipFile.FullName -Algorithm SHA256
    "$($zipHash.Hash.ToLowerInvariant()) *$portableName.zip" | Set-Content "$portableZip.sha256" -Encoding ascii
    Write-Host ("Portable ZIP created: {0} ({1:N2} MB)" -f $zipFile.FullName, ($zipFile.Length / 1MB)) -ForegroundColor Green
}

Write-Host "Publish directory: $publish"
