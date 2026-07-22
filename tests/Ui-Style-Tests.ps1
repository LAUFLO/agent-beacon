$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$ui = Get-Content -LiteralPath (Join-Path $root 'AgentUi.cs') -Encoding UTF8 -Raw
$history = Get-Content -LiteralPath (Join-Path $root 'StateHistory.cs') -Encoding UTF8 -Raw
$theme = Get-Content -LiteralPath (Join-Path $root 'PixelTheme.cs') -Encoding UTF8 -Raw
$update = Get-Content -LiteralPath (Join-Path $root 'UpdateService.cs') -Encoding UTF8 -Raw
$build = Get-Content -LiteralPath (Join-Path $root 'build.ps1') -Encoding UTF8 -Raw
$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Encoding UTF8 -Raw
$logoGenerator = Get-Content -LiteralPath (Join-Path $root 'tools\generate_pixel_logo.py') -Encoding UTF8 -Raw
$all = @('AgentTrafficLight.cs','AgentUi.cs','StateHistory.cs','UpdateService.cs','PixelTheme.cs') | ForEach-Object { Get-Content -LiteralPath (Join-Path $root $_) -Encoding UTF8 -Raw }

if (($all -join "`n") -match 'MessageBox\.Show') { throw 'A native MessageBox remains and breaks the unified pixel style.' }
if ($ui -notmatch 'BackColor = PixelTheme\.Paper' -or $ui -notmatch 'PixelTheme\.PaintWindow') { throw 'Settings page is not using the white pixel theme.' }
if ($ui -notmatch 'ClientSize = new Size\(760, 500\)' -or $ui -match 'PixelTheme\.Label\(') { throw 'Settings page still contains redundant explanatory labels or excess height.' }
if ($history -notmatch 'ClientSize = new Size\(760, 600\)' -or $history -notmatch 'FormBorderStyle = FormBorderStyle\.None' -or $history -notmatch 'MaximizeBox = false; MinimizeBox = false') { throw 'Status center size/style is not fixed.' }
if ($history -notmatch 'DiagnosticsHub\.Report\(\)' -or $ui -match 'DiagnosticsHub\.Report\(\)') { throw 'Live diagnostics were not moved into the status center.' }
if ($theme -notmatch 'sealed class PixelDialog' -or $theme -notmatch 'Paper = Color\.FromArgb\(255, 255, 255\)' -or $theme -notmatch 'FillRectangle\(ink, 0, 0, width, 6\)') { throw 'Heavy white pixel dialog theme is incomplete.' }
if ($theme -notmatch 'sealed class PixelMenuRenderer' -or $ui -notmatch 'PixelTheme\.StyleMenu\(trayMenu\)' -or $ui -notmatch 'PixelTheme\.StyleMenu\(taskbarMenu\)' -or $ui -notmatch 'widget\.ContextMenuStrip = trayMenu') { throw 'Tray menus do not use the pixel renderer.' }
if ($ui -notmatch 'spacious' -and $theme -notmatch 'spacious') { throw 'Pixel dialogs do not support a compact layout.' }
$ownedUi = $ui + $history + $theme
if ($ownedUi -match 'Microsoft YaHei UI|Consolas' -or $theme -notmatch 'FontName = "SimSun"' -or $theme -notmatch 'MonoFontName = "NSimSun"') { throw 'App-owned UI does not use the unified CJK pixel font family.' }
if ($theme -notmatch 'TextAlign = ContentAlignment\.MiddleCenter' -or $ui -notmatch 'TextAlign = ContentAlignment\.MiddleCenter') { throw 'Centerable UI text is not centered.' }
if ($theme -notmatch 'sealed class PixelProgressForm' -or $theme -notmatch 'sealed class PixelProgressBar' -or $ui -notmatch 'UpdateService\.Download\(info, progress\.Report\)' -or $update -notmatch 'Action<int, string> report') { throw 'Automatic update does not use the pixel progress UI with real progress callbacks.' }
if ($history -match 'ScrollBars\.Vertical' -or $history -notmatch 'sealed class PixelScrollBar' -or $history -notmatch 'sealed class PixelLogBox' -or $history -notmatch 'EM_LINESCROLL') { throw 'Status center still uses native scrollbars instead of the functional pixel scrollbar.' }
if ($theme -notmatch 'AppIcon = LoadAppIcon\(\)' -or $theme -notmatch 'Icon\.ExtractAssociatedIcon\(Application\.ExecutablePath\)' -or ([regex]::Matches($ownedUi, 'Icon = PixelTheme\.AppIcon')).Count -lt 5) { throw 'App-owned windows are not consistently using the embedded application icon.' }
if ($build -notmatch 'assets\\Agent-Beacon\.ico' -or $readme -notmatch 'assets/Agent-Beacon\.png' -or $logoGenerator -notmatch 'Image\.Resampling\.NEAREST' -or $logoGenerator -notmatch 'SIZES = \(16, 20, 24, 32, 40, 48, 64, 128, 256\)') { throw 'Pixel logo is not consistently generated and consumed by the EXE and README.' }
Write-Host 'PASS unified pixel fonts, centered text, pixel logo/window icons, fixed status center, pixel dialogs/menus and update progress'
