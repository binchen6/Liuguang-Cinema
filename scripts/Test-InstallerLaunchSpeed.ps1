<#
.SYNOPSIS
    Measures Inno Setup installer launch-to-wizard-window latency and enforces non-regression thresholds.
.DESCRIPTION
    Launches the target installer executable, polls Win32 top-level window visibility at 5ms intervals until
    the Setup Wizard window becomes visible, immediately sends WM_CLOSE (canceling without installing),
    and verifies the launch latency stays within the strict "instant open" budget.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [int]$Runs = 3,
    [int]$MaxColdMs = 2500,
    [int]$MaxWarmMs = 1000,
    [switch]$AssertIssSettings
)

$ErrorActionPreference = "Stop"
$resolved = (Resolve-Path -LiteralPath $InstallerPath).Path

if ($AssertIssSettings) {
    $root = Split-Path -Parent $PSScriptRoot
    $issPath = Join-Path $root "installer/TV.iss"
    $iss = Get-Content -LiteralPath $issPath -Raw
    if ($iss -notmatch '(?m)^InternalCompressLevel=none\s*$') {
        throw "Regression check failed: installer/TV.iss must keep InternalCompressLevel=none for instant launch."
    }
    if ($iss -notmatch '(?m)^SolidCompression=no\s*$') {
        throw "Regression check failed: installer/TV.iss must keep SolidCompression=no for instant launch."
    }
    if ($iss -match 'vc_redist\.x64\.exe') {
        throw "Regression check failed: installer/TV.iss must not re-embed vc_redist.x64.exe (use app-local CRT DLLs)."
    }
    Write-Host "ISS fast-launch guard: PASS" -ForegroundColor Green
}

if (-not ('TV.InstallerBench.Win32Probe' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TV.InstallerBench
{
    public static class Win32Probe
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const uint WM_CLOSE = 0x0010;

        public static IntPtr FindVisibleWindowForProcessTree(int rootPid, out int ownerPid)
        {
            var pids = new HashSet<int> { rootPid };
            try
            {
                foreach (var p in Process.GetProcessesByName(Process.GetProcessById(rootPid).ProcessName))
                {
                    // Inno Setup spawns a child .tmp process with a different name, so collect any visible TSelectLanguageForm/TWizardForm owned by our root or recently started tmp setup
                    pids.Add(p.Id);
                    p.Dispose();
                }
            }
            catch { }

            IntPtr found = IntPtr.Zero;
            int foundPid = 0;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out var pid);
                if (!GetWindowRect(hWnd, out var r)) return true;
                if ((r.Right - r.Left) < 200 || (r.Bottom - r.Top) < 120) return true;

                if (pids.Contains((int)pid))
                {
                    found = hWnd;
                    foundPid = (int)pid;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            ownerPid = foundPid;
            return found;
        }

        public static IntPtr FindNewVisibleSetupWindow(HashSet<IntPtr> baselineWindows, out int ownerPid)
        {
            IntPtr found = IntPtr.Zero;
            int foundPid = 0;
            EnumWindows((hWnd, _) =>
            {
                if (baselineWindows.Contains(hWnd) || !IsWindowVisible(hWnd)) return true;
                if (!GetWindowRect(hWnd, out var r)) return true;
                if ((r.Right - r.Left) < 200 || (r.Bottom - r.Top) < 120) return true;
                GetWindowThreadProcessId(hWnd, out var pid);
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    var name = proc.ProcessName;
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("TV-Setup", StringComparison.OrdinalIgnoreCase))
                    {
                        found = hWnd;
                        foundPid = (int)pid;
                        return false;
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            ownerPid = foundPid;
            return found;
        }

        public static HashSet<IntPtr> SnapshotVisibleWindows()
        {
            var set = new HashSet<IntPtr>();
            EnumWindows((hWnd, _) =>
            {
                if (IsWindowVisible(hWnd)) set.Add(hWnd);
                return true;
            }, IntPtr.Zero);
            return set;
        }

        public static void CloseWindow(IntPtr hWnd) => PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }
}
"@
}

$sizeMb = [math]::Round((Get-Item -LiteralPath $resolved).Length / 1MB, 2)
Write-Host "Benchmarking installer: $resolved ($sizeMb MB), runs=$Runs" -ForegroundColor Cyan

$samples = @()
for ($i = 1; $i -le $Runs; $i++) {
    $baseline = [TV.InstallerBench.Win32Probe]::SnapshotVisibleWindows()
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $resolved -ArgumentList "/NORESTART" -PassThru
    $hwnd = [IntPtr]::Zero
    $ownerPid = 0
    $deadlineMs = 15000

    while ($sw.ElapsedMilliseconds -lt $deadlineMs) {
        $hwnd = [TV.InstallerBench.Win32Probe]::FindNewVisibleSetupWindow($baseline, [ref]$ownerPid)
        if ($hwnd -ne [IntPtr]::Zero) { break }
        if ($proc.HasExited) { break }
        Start-Sleep -Milliseconds 10
    }
    $sw.Stop()
    $elapsed = [int]$sw.ElapsedMilliseconds

    # Cleanly terminate wizard & loader without installing
    if ($hwnd -ne [IntPtr]::Zero) {
        [TV.InstallerBench.Win32Probe]::CloseWindow($hwnd)
        Start-Sleep -Milliseconds 120
    }
    if ($ownerPid -gt 0) {
        try { Stop-Process -Id $ownerPid -Force -ErrorAction SilentlyContinue } catch { }
    }
    try { if (-not $proc.HasExited) { $proc.Kill($true) } } catch { }
    try { $proc.WaitForExit(2000) | Out-Null } catch { }
    $proc.Dispose()

    if ($hwnd -eq [IntPtr]::Zero) {
        throw "Installer wizard window did not appear within ${deadlineMs}ms on run $i."
    }
    $samples += $elapsed
    Write-Host ("  Run {0}: {1} ms" -f $i, $elapsed)
    Start-Sleep -Milliseconds 350
}

$cold = $samples[0]
$warm = if ($samples.Count -gt 1) { [int](($samples | Select-Object -Skip 1 | Measure-Object -Minimum).Minimum) } else { $cold }
Write-Host ("Summary: cold={0} ms (limit {1} ms), best warm={2} ms (limit {3} ms)" -f $cold, $MaxColdMs, $warm, $MaxWarmMs) -ForegroundColor Cyan

if ($cold -gt $MaxColdMs) {
    throw "Installer cold launch regression: ${cold} ms > ${MaxColdMs} ms"
}
if ($warm -gt $MaxWarmMs) {
    throw "Installer warm launch regression: ${warm} ms > ${MaxWarmMs} ms"
}
Write-Host "Installer launch speed gate: PASS" -ForegroundColor Green
