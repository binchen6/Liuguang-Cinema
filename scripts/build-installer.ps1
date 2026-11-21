[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$MavenOffline,
    [switch]$NoRestore,
    [string]$ReleaseDirectory
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

Assert-ReleaseIntegrity $release
$dotnet = Join-Path $root ".dotnet/dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$publishId = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$publish = Join-Path $root "artifacts/publish-$publishId"
$prerequisites = Join-Path $root "artifacts/prerequisites"
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid application version: $version" }
$installerName = "TV-Setup-$version-x64"
$installer = Join-Path $release "$installerName.exe"
New-Item -ItemType Directory -Force -Path $publish, $prerequisites | Out-Null
if (-not $env:ProgramFiles) { $env:ProgramFiles = "C:\Program Files" }
if (-not ${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} = "C:\Program Files (x86)" }
if (-not $env:ProgramData) { $env:ProgramData = "C:\ProgramData" }
if (-not $env:LOCALAPPDATA -or -not (Test-Path $env:LOCALAPPDATA)) {
    $env:LOCALAPPDATA = [Environment]::GetFolderPath("LocalApplicationData")
}

$jdk17 = Join-Path $env:USERPROFILE ".jdks/jdk-17"
if (Test-Path (Join-Path $jdk17 "bin/jlink.exe")) { $javaHome = Get-Item $jdk17 }
else {
    $javaHome = Get-ChildItem "$env:ProgramFiles\Microsoft\jdk-*" -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "bin/jlink.exe") } |
        Sort-Object Name -Descending | Select-Object -First 1
}
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

$webview = Join-Path $prerequisites "MicrosoftEdgeWebView2Setup.exe"
if (-not (Test-Path $webview)) {
    Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $webview -UseBasicParsing
}
$vcredist = Join-Path $prerequisites "vc_redist.x64.exe"
if (-not (Test-Path $vcredist)) {
    Invoke-WebRequest -Uri "https://aka.ms/vs/17/release/vc_redist.x64.exe" -OutFile $vcredist -UseBasicParsing
}

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
    "PushNotificationsLongRunningTask.ProxyStub.dll", "createdump.exe", "mscordbi.dll", "mscordaccore*", "Microsoft.DiaSymReader.Native.amd64.dll"
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
foreach ($name in "access_output", "mux", "stream_out", "services_discovery", "lua", "visualization", "logger", "meta_engine", "stream_extractor") {
    $path = Join-Path $vlc "plugins/$name"
    Remove-PublishDirectory $path
}
$lua = Join-Path $vlc "lua"
Remove-PublishDirectory $lua
Get-ChildItem $vlc -File -Filter "*.lib" -ErrorAction SilentlyContinue | Remove-Item -Force
foreach ($native in "libvlc.dll", "libvlccore.dll", "plugins", "TV.exe", "runtime/catvod-engine.jar", "runtime/java/bin/java.exe") {
    if ($native -eq "TV.exe") { $path = Join-Path $publish "TV.exe" }
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
$setupPayload = Join-Path $setupStage 'setup-payload.exe'
$setupOutput = Join-Path $release "$installerName-build-$publishId.exe"
& $iscc "/DMyAppVersion=$version" "/DPublishRoot=$publish" "/O$setupStage" '/Fsetup-payload' (Join-Path $root "installer/TV.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
try {
    & (Join-Path $PSScriptRoot 'build-setup-launcher.ps1') -Payload $setupPayload -OutputPath $setupOutput -Version $version
    Assert-ReleaseIntegrity $setupOutput
    Move-Item -LiteralPath $setupOutput -Destination $installer -Force
    Assert-ReleaseIntegrity $installer
} finally {
    if (Test-Path -LiteralPath $setupOutput -PathType Leaf) { Remove-Item -LiteralPath $setupOutput }
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
Write-Host "Size: $($file.Length) bytes; SHA256: $($hash.Hash)" -ForegroundColor Green
"$($hash.Hash.ToLowerInvariant()) *$installerName.exe" | Set-Content "$installer.sha256" -Encoding ascii
Write-Host "Publish directory: $publish"
