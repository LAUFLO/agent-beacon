param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'))

$ErrorActionPreference = 'Stop'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$versionMatch = Select-String -LiteralPath (Join-Path $PSScriptRoot 'AppInfo.cs') -Pattern 'public const string Version = "([0-9]+\.[0-9]+\.[0-9]+)"' | Select-Object -First 1
if (-not $versionMatch) { throw 'App version was not found in AppInfo.cs.' }
$version = $versionMatch.Matches[0].Groups[1].Value
$helper = Join-Path $PSScriptRoot 'integrations\Agent-Beacon-MCP.exe'
$output = Join-Path $OutputDirectory "Agent-Beacon-$version.exe"
$sources = @('AppInfo.cs','PixelTheme.cs','StateHistory.cs','DesktopFeatures.cs','UpdateService.cs','AgentUi.cs','Integrations.cs','AgentTrafficLight.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
New-Item -ItemType Directory -Force -Path $OutputDirectory, (Split-Path $helper) | Out-Null

$helperArguments = @('/nologo','/target:exe','/optimize+','/platform:anycpu',"/out:$helper",'/reference:System.dll','/reference:System.Core.dll','/reference:System.Web.Extensions.dll',(Join-Path $PSScriptRoot 'TraeMcpHost.cs'))
& $compiler $helperArguments
if ($LASTEXITCODE -ne 0) { throw 'TRAE MCP Helper compilation failed.' }

$icon = Join-Path $PSScriptRoot 'assets\Agent-Beacon.ico'
$claude = Join-Path $PSScriptRoot 'integrations\claude-hook.cjs'
$openCode = Join-Path $PSScriptRoot 'integrations\opencode-plugin.js'
$appArguments = @(
  '/nologo','/target:winexe','/optimize+','/platform:x64',"/out:$output","/win32icon:$icon",
  '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll',
  '/reference:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll',
  '/reference:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll',
  "/resource:$claude,claude-hook.cjs","/resource:$openCode,opencode-plugin.js","/resource:$helper,trae-mcp-host.exe"
) + $sources
& $compiler $appArguments
if ($LASTEXITCODE -ne 0) { throw 'Agent Beacon compilation failed.' }

$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = Join-Path $OutputDirectory "Agent-Beacon-$version.sha256"
[IO.File]::WriteAllText($hashPath, "$hash  Agent-Beacon-$version.exe`n", (New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{ Path = $output; Version = $version; SHA256 = $hash }
