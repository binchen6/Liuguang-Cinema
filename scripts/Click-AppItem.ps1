param([int]$Index, [string]$Name)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class MouseInput {
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
"@
$process = Get-Process -Name TV | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$item = $null
if ($PSBoundParameters.ContainsKey('Name')) {
    for ($i = 0; $i -lt $all.Count; $i++) { if ($all.Item($i).Current.Name -eq $Name) { $item = $all.Item($i); break } }
} else { $item = $all.Item($Index) }
if (-not $item) { throw "UI item not found" }
try { $point = $item.GetClickablePoint() } catch {
    $rect = $item.Current.BoundingRectangle
    if ($rect.Width -le 0 -or $rect.Height -le 0) {
        $pattern = $item.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)
        if ($pattern) { $pattern.ScrollIntoView(); Start-Sleep -Milliseconds 500; $rect = $item.Current.BoundingRectangle }
    }
    if ($rect.Width -le 0 -or $rect.Height -le 0) { throw "UI item is outside the viewport" }
    $point = New-Object System.Windows.Point
    $point.X = $rect.X + $rect.Width / 2
    $point.Y = $rect.Y + $rect.Height / 2
}
[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]$point.X, [int]$point.Y)
Start-Sleep -Milliseconds 150
[MouseInput]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
[MouseInput]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
Write-Output "Clicked $($item.Current.Name) at $point"
