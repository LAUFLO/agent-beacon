param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'))

$ErrorActionPreference = 'Stop'
$compiler = & (Join-Path $PSScriptRoot 'tools\Get-RoslynCompiler.ps1')
if (-not $compiler -or -not (Test-Path -LiteralPath $compiler)) { throw 'Deterministic Roslyn compiler is unavailable.' }
$versionMatch = Select-String -LiteralPath (Join-Path $PSScriptRoot 'AppInfo.cs') -Pattern 'public const string Version = "([0-9]+\.[0-9]+\.[0-9]+)"' | Select-Object -First 1
if (-not $versionMatch) { throw 'App version was not found in AppInfo.cs.' }
$version = $versionMatch.Matches[0].Groups[1].Value
$helper = Join-Path $PSScriptRoot 'integrations\Agent-Beacon-MCP.exe'
$output = Join-Path $OutputDirectory "Agent-Beacon-$version.exe"
$setup = Join-Path $OutputDirectory "Agent-Beacon-Setup-$version.exe"
$sources = @('AppInfo.cs','PixelTheme.cs','DpiSupport.cs','StateHistory.cs','UsageStatistics.cs','DesktopFeatures.cs','UpdateService.cs','AgentUi.cs','Integrations.cs','CodexEventCompatibility.cs','AgentTrafficLight.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
New-Item -ItemType Directory -Force -Path $OutputDirectory, (Split-Path $helper) | Out-Null

$deterministic = @('/deterministic+',("/pathmap:" + $PSScriptRoot + '=/_/AgentBeacon'))
$helperArguments = @('/nologo','/target:exe','/optimize+','/platform:anycpu',"/out:$helper",'/reference:System.dll','/reference:System.Core.dll','/reference:System.Web.Extensions.dll') + $deterministic + @((Join-Path $PSScriptRoot 'TraeMcpHost.cs'))
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
) + $deterministic + $sources
& $compiler $appArguments
if ($LASTEXITCODE -ne 0) { throw 'Agent Beacon compilation failed.' }

$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = Join-Path $OutputDirectory "Agent-Beacon-$version.sha256"
[IO.File]::WriteAllText($hashPath, "$hash  Agent-Beacon-$version.exe`n", (New-Object Text.UTF8Encoding($false)))

$installerArguments = @(
  '/nologo','/target:winexe','/optimize+','/platform:anycpu',"/out:$setup","/win32icon:$icon",
  '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:Microsoft.CSharp.dll','/reference:System.Windows.Forms.dll',
  "/resource:$output,agent-beacon.exe"
) + $deterministic + @((Join-Path $PSScriptRoot 'AppInfo.cs'), (Join-Path $PSScriptRoot 'DpiSupport.cs'), (Join-Path $PSScriptRoot 'PixelTheme.cs'), (Join-Path $PSScriptRoot 'InstallerStub.cs'))
& $compiler $installerArguments
if ($LASTEXITCODE -ne 0) { throw 'Agent Beacon installer compilation failed.' }
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputDirectory "Agent-Beacon-Setup-$version.sha256"), "$setupHash  Agent-Beacon-Setup-$version.exe`n", (New-Object Text.UTF8Encoding($false)))

$portable = Join-Path $OutputDirectory "Agent-Beacon-Portable-$version.zip"
$portableStage = Join-Path ([IO.Path]::GetTempPath()) ('agent-beacon-portable-' + [Guid]::NewGuid().ToString('N'))
try {
  New-Item -ItemType Directory -Force -Path $portableStage | Out-Null
  Copy-Item -LiteralPath $output, $hashPath, (Join-Path $PSScriptRoot 'README.md') -Destination $portableStage
  if (Test-Path -LiteralPath $portable) { Remove-Item -LiteralPath $portable -Force }
  Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
  $zipStream = [IO.File]::Create($portable)
  $archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
  try {
    foreach ($file in Get-ChildItem -LiteralPath $portableStage -File | Sort-Object Name) {
      $entry = $archive.CreateEntry($file.Name, [IO.Compression.CompressionLevel]::Optimal); $entry.LastWriteTime = [DateTimeOffset]'2000-01-01T00:00:00Z'
      $input = $file.OpenRead(); $entryStream = $entry.Open()
      try { $input.CopyTo($entryStream) } finally { $entryStream.Dispose(); $input.Dispose() }
    }
  } finally { $archive.Dispose(); $zipStream.Dispose() }
} finally { if (Test-Path -LiteralPath $portableStage) { Remove-Item -LiteralPath $portableStage -Recurse -Force } }
$portableHash = (Get-FileHash -LiteralPath $portable -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputDirectory "Agent-Beacon-Portable-$version.sha256"), "$portableHash  Agent-Beacon-Portable-$version.zip`n", (New-Object Text.UTF8Encoding($false)))

[pscustomobject]@{ Path = $output; Installer = $setup; Portable = $portable; Version = $version; SHA256 = $hash; InstallerSHA256 = $setupHash; PortableSHA256 = $portableHash }
