Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$p = Get-Process MiLauncher -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { "未运行"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
"窗口矩形: " + $root.Current.BoundingRectangle

foreach ($id in @("DetailContent", "EmptyState", "TxtName", "TxtVersion", "BtnPlay", "BtnLauncher", "TxtPath", "GameList")) {
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $e = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if ($e) {
        "{0,-14} offscreen={1,-5} rect={2}" -f $id, $e.Current.IsOffscreen, $e.Current.BoundingRectangle
    }
    else {
        "{0,-14} 不在自动化树（可能 Collapsed）" -f $id
    }
}
