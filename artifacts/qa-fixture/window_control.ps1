param([long]$Hwnd, [string]$Name, [ValidateSet('find','invoke','toggle','expand','select','set','bounds','menu-bounds','menu-enabled','flyout-bounds','menu-flyout-bounds','read','focus','scroll-bottom','scroll-percent','scroll-set')] [string]$Action = 'find', [string]$Value = '')

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$window = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Hwnd)
$condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$pattern = switch ($Action) {
    'invoke' { [System.Windows.Automation.InvokePattern]::Pattern }
    'toggle' { [System.Windows.Automation.TogglePattern]::Pattern }
    'expand' { [System.Windows.Automation.ExpandCollapsePattern]::Pattern }
    'select' { [System.Windows.Automation.SelectionItemPattern]::Pattern }
    'set' { [System.Windows.Automation.ValuePattern]::Pattern }
    'read' { [System.Windows.Automation.ValuePattern]::Pattern }
    'scroll-bottom' { [System.Windows.Automation.ScrollPattern]::Pattern }
    'scroll-percent' { [System.Windows.Automation.ScrollPattern]::Pattern }
    'scroll-set' { [System.Windows.Automation.ScrollPattern]::Pattern }
}
function Find-MatchingControl($surface) {
    $fallback = $null
    foreach ($candidate in $surface.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        $matched = $null
        if ($Action -in 'menu-bounds','menu-enabled','menu-flyout-bounds' -and $candidate.Current.ControlType -ne [System.Windows.Automation.ControlType]::MenuItem) { continue }
        if ($Action -in 'find','bounds','menu-bounds','menu-enabled','flyout-bounds','menu-flyout-bounds','focus' -or $candidate.TryGetCurrentPattern($pattern, [ref]$matched)) {
            $result = [pscustomobject]@{ Element = $candidate; Pattern = $matched }
            if ($Action -eq 'bounds' -and $candidate.Current.IsOffscreen) {
                if ($null -eq $fallback) { $fallback = $result }
                continue
            }
            return $result
        }
    }
    if ($null -ne $fallback) { return $fallback }
}
$owner = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $window.Current.ProcessId)
$until = [DateTime]::UtcNow.AddSeconds(5)
do {
    $found = Find-MatchingControl $window
    if (!$found) {
        foreach ($surface in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $owner)) {
            if ($surface.Current.NativeWindowHandle -eq $Hwnd) { continue }
            $found = Find-MatchingControl $surface
            if ($found) { break }
        }
    }
    if ($found) { break }
    Start-Sleep -Milliseconds 80
} while ([DateTime]::UtcNow -lt $until)
if (!$found) {
    $items = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $names = $items | Where-Object { $_.Current.Name -and -not $_.Current.IsOffscreen } |
        Select-Object -First 35 | ForEach-Object { "$($_.Current.Name) [$($_.Current.ClassName)]" }
    throw "Missing control: $Name. Visible controls: $($names -join ', ')"
}
$control = $found.Element
$matched = $found.Pattern

switch ($Action) {
    'invoke' { $matched.Invoke() }
    'toggle' { $matched.Toggle() }
    'expand' { $matched.Expand() }
    'select' { $matched.Select() }
    'set' { $matched.SetValue($Value) }
    'read' { Write-Output $matched.Current.Value }
    'menu-enabled' { Write-Output $control.Current.IsEnabled }
    'focus' { $control.SetFocus() }
    'scroll-bottom' { $matched.SetScrollPercent(-1, 100) }
    'scroll-set' { $matched.SetScrollPercent(-1, [double]$Value) }
    'scroll-percent' { $matched.Current.VerticalScrollPercent.ToString([Globalization.CultureInfo]::InvariantCulture) }
    { $_ -in 'bounds','menu-bounds','flyout-bounds','menu-flyout-bounds' } {
        if ($Action -in 'flyout-bounds','menu-flyout-bounds') {
            $parent = $control
            $classes = @()
            for ($level = 0; $level -lt 10 -and $null -ne $parent; $level++) {
                $classes += $parent.Current.ClassName
                if ($parent.Current.ClassName -match '^(Menu)?Flyout(Presenter)?$') { $control = $parent; break }
                $parent = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($parent)
            }
            if ($null -eq $parent -or $control.Current.ClassName -notmatch '^(Menu)?Flyout(Presenter)?$') { throw "Flyout presenter missing for $Name ($classes)" }
        }
        $rect = $control.Current.BoundingRectangle
        @{x=$rect.X;y=$rect.Y;w=$rect.Width;h=$rect.Height;visible=(-not $control.Current.IsOffscreen)} | ConvertTo-Json -Compress
    }
}
