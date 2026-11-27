[CmdletBinding()]
param(
    [string]$AppDirectory = 'src/TV/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64',
    [string]$OutputDirectory = 'artifacts/qa-release',
    [switch]$VerifyDefaultService,
    [switch]$VerifyPython
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$app = (Get-Item -LiteralPath $AppDirectory).FullName
$output = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$profile = (New-Item -ItemType Directory -Path (Join-Path $output ([Guid]::NewGuid().ToString('N')))).FullName
$dotnet = if (Test-Path '.dotnet/dotnet.exe') { (Get-Item '.dotnet/dotnet.exe').FullName } else { (Get-Command dotnet).Source }
$sdk = & $dotnet --version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$dotnetHome = Split-Path -Parent $dotnet
$ref = Get-ChildItem -LiteralPath (Join-Path $dotnetHome 'packs/Microsoft.NETCore.App.Ref') -Directory |
    Where-Object { $_.Name -like '10.*' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$references = Get-ChildItem -LiteralPath (Join-Path $ref.FullName 'ref/net10.0') -Filter '*.dll' |
    ForEach-Object { '/reference:"{0}"' -f $_.FullName }
$testBuild = Join-Path $profile 'TV.ReleaseRegression.dll'
$assembly = Join-Path $app 'TV.ReleaseRegression.dll'
$responseFile = Join-Path $profile 'compile.rsp'
@('/nologo', '/target:exe', '/nullable:enable', '/langversion:latest',
    ('/out:"{0}"' -f $testBuild), ('/reference:"{0}/TV.dll"' -f $app), ('/reference:"{0}/LibVLCSharp.dll"' -f $app)) +
    $references + ('"{0}/release-regressions.cs"' -f $PSScriptRoot) |
    Set-Content -LiteralPath $responseFile -Encoding utf8
& $dotnet (Join-Path $dotnetHome "sdk/$sdk/Roslyn/bincore/csc.dll") /noconfig "@$responseFile"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
[IO.File]::Copy($testBuild, $assembly, $false)
try {
    $launchArgs = @('exec', '--runtimeconfig', (Join-Path $app 'TV.runtimeconfig.json'),
        '--depsfile', (Join-Path $app 'TV.deps.json'), $assembly, $profile)
    if ($VerifyDefaultService) { $launchArgs += '--verify-default-service' }
    if ($VerifyPython) { $launchArgs += '--verify-python' }
    & $dotnet @launchArgs
    $regressionExitCode = $LASTEXITCODE
}
finally { Remove-Item -LiteralPath $assembly }
exit $regressionExitCode
