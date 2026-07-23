$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read-SourceText([string[]]$relativePaths) {
  return ($relativePaths | ForEach-Object { Get-Content -LiteralPath (Join-Path $root $_) -Encoding UTF8 -Raw }) -join "`n"
}
$ui = Read-SourceText @('src\UI\PixelPoleControl.cs','src\UI\SettingsForm.cs','src\UI\NotificationSettingsForm.cs','src\UI\MainForm.cs')
$history = Read-SourceText @('src\UI\StateHistory.cs')
$theme = Read-SourceText @('src\UI\PixelTheme.cs')
$update = Read-SourceText @('src\Services\UpdateService.cs')
$build = Get-Content -LiteralPath (Join-Path $root 'build.ps1') -Encoding UTF8 -Raw
$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Encoding UTF8 -Raw
$logoGenerator = Get-Content -LiteralPath (Join-Path $root 'tools\generate_pixel_logo.py') -Encoding UTF8 -Raw
$dpi = Read-SourceText @('src\UI\DpiSupport.cs')
$stats = Read-SourceText @('src\Services\UsageStatistics.cs')
$taskCenter = Read-SourceText @('src\Features\TaskCenter.cs')
$installer = Read-SourceText @('installer\InstallerStub.cs')
$processes = Read-SourceText @('src\Monitoring\AgentProcesses.cs')
$diagnostics = Read-SourceText @('src\Core\DiagnosticsHub.cs')
$models = Read-SourceText @('src\Core\AgentModels.cs')
$appSources = & (Join-Path $root 'tools\Get-AppSourceFiles.ps1') -Root $root
$allPaths = @($appSources) + @((Join-Path $root 'installer\InstallerStub.cs'))
$all = $allPaths | ForEach-Object { Get-Content -LiteralPath $_ -Encoding UTF8 -Raw }

if (($all -join "`n") -match 'MessageBox\.Show') { throw 'A native MessageBox remains and breaks the unified pixel style.' }
if ($ui -notmatch 'BackColor = PixelTheme\.Paper' -or $ui -notmatch 'PixelTheme\.PaintWindow') { throw 'Settings page is not using the white pixel theme.' }
if ($ui -notmatch 'ClientSize = new Size\(760, 438\)' -or $ui -match '仅统计状态时长与完成数量，不保存聊天正文' -or $ui -match 'PixelTheme\.Label\(') { throw 'Settings page still contains redundant explanatory labels or excess height.' }
if ($ui -notmatch 'AGENT BEACON v" \+ AppInfo\.Version \+ " // 设置' -or $ui -notmatch '今日统计' -or $ui -notmatch 'UsageStatistics\.Duration' -or $ui -notmatch '检查 / 修复') { throw 'Settings page does not show the current version, daily statistics, and integration repair entry.' }
if ($ui -notmatch '复制 TRAE MCP.+Size = new Size\(188, 32\)' -or $ui -notmatch '检查 / 修复集成.+Location = new Point\(22, 350\)' -or $ui -notmatch '状态中心.+Location = new Point\(220, 350\)') { throw 'Settings integrations are not using the compact two-column layout.' }
if ($ui -notmatch 'sealed class NotificationSettingsForm' -or $ui -notmatch '黄灯通知延迟' -or $ui -notmatch '长任务提醒') { throw 'Pixel notification policy page is missing.' }
if ($history -notmatch 'ClientSize = new Size\(620, 358\)' -or $history -notmatch 'overviewPage\.Location = new Point\(6, 46\)' -or $history -notmatch 'overviewPage\.Size = new Size\(608, 306\)' -or $history -notmatch 'ShowOverview\(\)' -or $history -notmatch 'ShowMore\(\)' -or $history -notmatch 'Text = "更多"' -or $history -notmatch 'Text = "返回"' -or $history -notmatch 'FormBorderStyle = FormBorderStyle\.None' -or $history -notmatch 'MaximizeBox = false; MinimizeBox = false') { throw 'Reduced bordered status center overview/more navigation is missing.' }
if ($history -notmatch 'Text = "清除"' -or $history -notmatch 'Text = "清除全部"' -or $history -notmatch 'TaskCenterState\.Dismiss\(task\)' -or $history -notmatch 'TaskCenterState\.DismissAll\(tasks\)' -or $history -notmatch 'TaskRowsPerSection = 2' -or $history -notmatch 'AddTaskSection\("待我处理".+62' -or $history -notmatch 'AddTaskSection\("正在运行".+170' -or $history -notmatch 'scroll\.Maximum = Math\.Max\(0, tasks\.Count - TaskRowsPerSection\)' -or $history -notmatch 'int visibleRows = Math\.Min\(TaskRowsPerSection, tasks\.Count - offset\)' -or $history -notmatch 'RenderAtomically\(overviewPage' -or $history -notmatch 'if \(reloading' -or $taskCenter -notmatch 'TaskDismissalStore' -or $taskCenter -notmatch 'SuppressSupersededStaleSessions' -or $history -notmatch '同一会话有新的 MCP 或 Agent 事件时会自动恢复监控') { throw 'Status center does not provide equal scrollable task sections, stale-wait cleanup, flicker-free refresh, or stale Codex session retirement.' }
if ($taskCenter -notmatch 'sealed class TaskQueuePopup' -or $taskCenter -notmatch 'Math\.Min\(438, height\)' -or $taskCenter -notmatch 'MouseDown \+= BeginDrag' -or $taskCenter -notmatch 'renderedSignature' -or $taskCenter -notmatch 'IsTransient' -or $ui -notmatch 'widget\.CenterClicked \+= delegate \{ ShowFullStatusCenter\(\); \}' -or $ui -notmatch 'widget\.SettingsClicked' -or $ui -notmatch 'Math\.Round\(88 \* scale\)' -or $ui -match '等待.+运行.+计数牌') { throw 'Compact direct status-center pole entry, original pole height, or stable task queue is missing.' }
if ($ui -notmatch 'ControlButtonWidth = 48' -or $ui -notmatch 'ControlButtonHeight = 34' -or $ui -notmatch 'ControlButtonGap = 10' -or $ui -notmatch 'center - ControlButtonWidth / 2' -or $ui -notmatch 'CenterRect\.Width / 2') { throw 'Pole action buttons are not using the enlarged centered hit areas.' }
if ($taskCenter -notmatch 'PendingTaskStore' -or $taskCenter -notmatch 'StalledAfterMs' -or $taskCenter -notmatch '超过 10 分钟无进展' -or $taskCenter -notmatch 'safe\.Title = safe\.Source \+ " 任务"; safe\.Cwd = ""') { throw 'Restart recovery, stuck detection or privacy-safe task snapshot is incomplete.' }
if ($history -notmatch 'DiagnosticsHub\.Report\(\)' -or $ui -match 'DiagnosticsHub\.Report\(\)') { throw 'Live diagnostics were not moved into the status center.' }
if ($theme -notmatch 'sealed class PixelDialog' -or $theme -notmatch 'Paper = Color\.FromArgb\(255, 255, 255\)' -or $theme -notmatch 'FillRectangle\(ink, 0, 0, width, 6\)') { throw 'Heavy white pixel dialog theme is incomplete.' }
if ($theme -notmatch 'sealed class PixelMenuRenderer' -or $ui -notmatch 'PixelTheme\.StyleMenu\(trayMenu\)' -or $ui -notmatch 'PixelTheme\.StyleMenu\(taskbarMenu\)' -or $ui -notmatch 'widget\.ContextMenuStrip = trayMenu') { throw 'Tray menus do not use the pixel renderer.' }
if ($theme -notmatch 'item\.AutoSize = false' -or $theme -notmatch 'item\.Size = new Size\(136, 29\)' -or $theme -notmatch 'Rectangle area = new Rectangle\(2, 1, e\.Item\.Width - 4, e\.Item\.Height - 2\)') { throw 'Pixel menu width and selected background are not compact and aligned.' }
if ($ui -notmatch 'spacious' -and $theme -notmatch 'spacious') { throw 'Pixel dialogs do not support a compact layout.' }
$ownedUi = $ui + $history + $theme
if ($ownedUi -match 'Microsoft YaHei UI|Consolas' -or $theme -notmatch 'FontName = "SimSun"' -or $theme -notmatch 'MonoFontName = "NSimSun"') { throw 'App-owned UI does not use the unified CJK pixel font family.' }
if ($dpi -notmatch 'SetProcessDpiAwarenessContext' -or $dpi -notmatch 'Screen\.FromRectangle' -or $ui -notmatch 'SystemEvents\.DisplaySettingsChanged' -or ($all -join "`n") -notmatch 'AutoScaleMode = AutoScaleMode\.Dpi') { throw 'Per-monitor DPI and multi-display protection are incomplete.' }
if ($stats -notmatch 'AnonymousKey' -or $stats -match 'Title|Detail|Evidence') { throw 'Usage statistics are not privacy-safe.' }
if ($theme -notmatch 'TextAlign = ContentAlignment\.MiddleCenter' -or $ui -notmatch 'TextAlign = ContentAlignment\.MiddleCenter') { throw 'Centerable UI text is not centered.' }
if ($theme -notmatch 'sealed class PixelProgressForm' -or $theme -notmatch 'sealed class PixelProgressBar' -or $ui -notmatch 'UpdateService\.Download\(info, progress\.Report\)' -or $update -notmatch 'Action<int, string> report') { throw 'Automatic update does not use the pixel progress UI with real progress callbacks.' }
if ($history -match 'ScrollBars\.Vertical' -or $history -notmatch 'sealed class PixelScrollBar' -or $history -notmatch 'sealed class PixelLogBox' -or $history -notmatch 'AccessibleName = name \+ "任务滚动条"' -or $history -notmatch 'scroll\.ValueChanged \+= delegate \{ renderRows\(\); \}' -or $history -notmatch 'EM_LINESCROLL') { throw 'Status center still lacks functional pixel scrollbars for task lists or logs.' }
if ($theme -notmatch 'DisposeChildren\(Control parent\)' -or $history -match '\.Controls\.Clear\(\)' -or $taskCenter -match '\bControls\.Clear\(\)') { throw 'Dynamic pixel UI controls are removed without deterministic disposal.' }
if ($processes -notmatch 'RuntimeSnapshotCacheMs = 3000' -or $processes -notmatch 'CodexUiScanDelay' -or $processes -notmatch 'new CacheRequest\(\)' -or $processes -notmatch 'AutomationElementMode\.None' -or $processes -notmatch '\.Cached' -or $ui -notmatch 'codexUiTarget\.PendingExec') { throw 'Process and Codex UI Automation scans are not using bounded cached polling.' }
if ($models -notmatch 'ManagedMemoryMb' -or $models -notmatch 'WorkingSetMb' -or $models -notmatch 'HandleCount' -or $ui -notmatch 'GC\.GetTotalMemory\(false\)' -or $diagnostics -notmatch '托管.+私有.+工作集.+句柄') { throw 'Diagnostics do not separate managed, private, working-set, and handle usage.' }
if ($theme -notmatch 'AppIcon = LoadAppIcon\(\)' -or $theme -notmatch 'Icon\.ExtractAssociatedIcon\(Application\.ExecutablePath\)' -or ([regex]::Matches($ownedUi, 'Icon = PixelTheme\.AppIcon')).Count -lt 5) { throw 'App-owned windows are not consistently using the embedded application icon.' }
if ($build -notmatch 'assets\\Agent-Beacon\.ico' -or $build -notmatch 'Agent-Beacon-Setup-\$version\.exe' -or $build -notmatch 'Agent-Beacon-Portable-\$version\.zip' -or $readme -notmatch 'assets/Agent-Beacon\.png' -or $logoGenerator -notmatch 'Image\.Resampling\.NEAREST' -or $logoGenerator -notmatch 'SIZES = \(16, 20, 24, 32, 40, 48, 64, 128, 256\)') { throw 'Pixel logo and portable/installer packages are not consistently generated.' }
if ($installer -notmatch '安装位置（支持自定义）' -or $installer -notmatch 'new FolderBrowserDialog' -or $installer -notmatch 'NormalizeInstallDirectory' -or $installer -notmatch 'Program\.Install\(autoStart\.Checked, installPath\.Text\)') { throw 'Installer does not support a validated custom installation directory.' }
Write-Host 'PASS unified pixel fonts, centered text, pixel logo/window icons, fixed status center, pixel dialogs/menus and update progress'
