# Set (or clear) the search box text through UIA ValuePattern, so no foreground
# activation is needed. The caret/typing path is exercised separately.
param(
    [string]$Text = 'abc',
    [switch]$Clear
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$value = if ($Clear) { '' } else { $Text }

$proc = Get-Process WingetManager -ErrorAction Stop | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SearchBox')
$el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $el) { "element not found"; exit 1 }

$vp = $null
if (-not $el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
    "ValuePattern unavailable"; exit 1
}
$vp.SetValue($value)
Start-Sleep -Milliseconds 600
"set to    : <{0}>   length={1}" -f $vp.Current.Value, $vp.Current.Value.Length
