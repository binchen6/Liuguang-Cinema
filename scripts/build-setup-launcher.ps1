[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Payload,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$inputFile = (Get-Item -LiteralPath $Payload).FullName
$output = [IO.Path]::GetFullPath($OutputPath)
$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The Windows .NET Framework C# compiler is required.' }
$attributes = Join-Path ([IO.Path]::GetDirectoryName($output)) ('setup-version-' + [guid]::NewGuid().ToString('N') + '.cs')
try {
    [IO.File]::WriteAllText($attributes, @"
using System.Reflection;
[assembly: AssemblyTitle("流光影院安装程序")]
[assembly: AssemblyProduct("流光影院")]
[assembly: AssemblyCompany("TV")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyInformationalVersion("$Version")]
"@, [Text.UTF8Encoding]::new($true))
    $icon = Join-Path $root 'src/TV/Assets/App.ico'
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$output" "/win32icon:$icon" `
        "/resource:$inputFile,TV.Setup.InnoPayload" (Join-Path $root 'installer/SetupLauncher.cs') $attributes
    if ($LASTEXITCODE -ne 0) { throw "Setup launcher compilation failed with exit code $LASTEXITCODE" }
} finally {
    if (Test-Path -LiteralPath $attributes -PathType Leaf) { Remove-Item -LiteralPath $attributes }
}
