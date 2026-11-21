[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Config = '-',
    [string[]]$SiteKey,
    [string[]]$ImportUrl,
    [string]$LiveChannel,
    [string]$VodId,
    [switch]$DecodeSample
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
$dotnet = (Get-Item -LiteralPath '.dotnet/dotnet.exe').FullName
$app = (Get-Item -LiteralPath 'src/TV/bin/Release/net10.0-windows10.0.19041.0/win-x64').FullName
if ($Config -ne '-') { $Config = (Get-Item -LiteralPath $Config).FullName }
$report = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$sdk = & $dotnet --version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$ref = Get-ChildItem '.dotnet/packs/Microsoft.NETCore.App.Ref' -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$platformReferences = Get-ChildItem (Join-Path $ref.FullName 'ref/net10.0') -Filter '*.dll' |
    ForEach-Object { '/reference:"{0}"' -f $_.FullName }
$assembly = Join-Path $app "SiteAudit-$([guid]::NewGuid().ToString('N')).dll"
$responseFile = Join-Path $report 'compiler.rsp'
$arguments = @('/nologo','/target:exe','/nullable:enable','/langversion:latest',
    ('/out:"{0}"' -f $assembly), ('/reference:"{0}/TV.dll"' -f $app),
    ('/reference:"{0}/CommunityToolkit.Mvvm.dll"' -f $app), ('/reference:"{0}/LibVLCSharp.dll"' -f $app))
$arguments + $platformReferences + ('"{0}/site-probe.cs"' -f $PSScriptRoot) |
    Set-Content -LiteralPath $responseFile -Encoding UTF8
try {
    & $dotnet ".dotnet/sdk/$sdk/Roslyn/bincore/csc.dll" /noconfig "@$responseFile"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $hostArguments = @('exec', '--runtimeconfig', "$app/TV.runtimeconfig.json", '--depsfile', "$app/TV.deps.json",
        $assembly, $Config, (Join-Path $report 'profile')) + $SiteKey
    foreach ($url in $ImportUrl) { $hostArguments += "--import=$url" }
    if ($LiveChannel) { $hostArguments += "--live-channel=$LiveChannel" }
    if ($VodId) { $hostArguments += "--vod-id=$VodId" }
    if ($DecodeSample) { $hostArguments += '--decode-sample' }
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $dotnet @hostArguments 1> (Join-Path $report 'probe.jsonl') 2> (Join-Path $report 'probe.stderr')
    $probeExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    exit $probeExitCode
}
finally {
    if (Test-Path -LiteralPath $assembly) { Remove-Item -LiteralPath $assembly }
}
