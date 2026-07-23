param([string]$Root = (Split-Path -Parent $PSScriptRoot))

$relativePaths = @(
  'src\Core\AppInfo.cs'
  'src\Core\AgentModels.cs'
  'src\Core\DiagnosticsHub.cs'
  'src\Core\Util.cs'
  'src\Monitoring\CodexEventCompatibility.cs'
  'src\Monitoring\MonitorEngine.cs'
  'src\Monitoring\MonitorWatchers.cs'
  'src\Monitoring\AgentProcesses.cs'
  'src\Monitoring\AgentStateRules.cs'
  'src\Services\DesktopFeatures.cs'
  'src\Services\UpdateService.cs'
  'src\Services\UsageStatistics.cs'
  'src\Features\TaskCenter.cs'
  'src\Integrations\Integrations.cs'
  'src\UI\PixelTheme.cs'
  'src\UI\DpiSupport.cs'
  'src\UI\StateHistory.cs'
  'src\UI\PixelPoleControl.cs'
  'src\UI\SettingsForm.cs'
  'src\UI\NotificationSettingsForm.cs'
  'src\UI\MainForm.cs'
  'src\Program.cs'
)

foreach ($relativePath in $relativePaths) {
  $sourcePath = Join-Path $Root $relativePath
  if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "Application source file is missing: $relativePath"
  }
  $sourcePath
}
