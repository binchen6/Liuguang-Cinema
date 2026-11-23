[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Python,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$WindowBaseline = 'artifacts/publish-fa7a2376/TV.exe'
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
$Python = (Get-Item -LiteralPath $Python).FullName
$WindowBaseline = (Get-Item -LiteralPath $WindowBaseline).FullName
$app = (Get-Item -LiteralPath 'src/TV/bin/Release/net10.0-windows10.0.19041.0/win-x64/TV.exe').FullName
$env:TMP = (Get-Item -LiteralPath 'artifacts/tmp').FullName
$env:TEMP = $env:TMP
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONDONTWRITEBYTECODE = '1'
$report = New-Item -ItemType Directory -Path $OutputDirectory
$app, $WindowBaseline | ForEach-Object {
    $dll = Join-Path (Split-Path $_) 'TV.dll'
    [pscustomobject]@{
        Executable = $_
        Version = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
        Sha256 = (Get-FileHash -LiteralPath $dll).Hash
    }
} | Export-Csv (Join-Path $report.FullName 'binaries.csv') -NoTypeInformation -Encoding UTF8

$summary = Join-Path $report.FullName 'results.csv'
'script,status,exit_code,elapsed_seconds' | Set-Content -LiteralPath $summary -Encoding UTF8
$failed = $false
foreach ($name in 'workspace_layout','search','cast','window','close_dialog','recommendation','library','poster','update','popup','hdr_desktop') {
    $arguments = @("artifacts/qa-fixture/${name}_regression.py", $app)
    if ($name -eq 'window') { $arguments += $WindowBaseline }
    $started = Get-Date
    $log = Join-Path $report.FullName "$name.log"
    # Redirect both native streams without turning expected script failures into terminating errors.
    $process = Start-Process -FilePath $Python -ArgumentList ($arguments.ForEach({ '"' + $_ + '"' }) -join ' ') `
        -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.stderr" -PassThru -Wait
    Get-Content -LiteralPath "$log.stderr" -Encoding UTF8 | Add-Content -LiteralPath $log -Encoding UTF8
    Remove-Item -LiteralPath "$log.stderr"
    $status = if ($process.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }
    $failed = $failed -or $process.ExitCode -ne 0
    $seconds = ((Get-Date) - $started).TotalSeconds.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture)
    $row = "$name,$status,$($process.ExitCode),$seconds"
    Add-Content -LiteralPath $summary -Value $row -Encoding UTF8
    Write-Output $row
}
if ($failed) { exit 1 }
