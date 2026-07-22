$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ('atl-replay-' + [Guid]::NewGuid().ToString('N'))
$homePath = Join-Path $work 'home'
$codexTarget = Join-Path $homePath '.codex\sessions\2026\07\18\replay.jsonl'
$claudeTarget = Join-Path $homePath '.claude\projects\replay\claude-replay.jsonl'
$bridge = Join-Path $homePath '.agent-traffic-light\events'
$openTarget = Join-Path $bridge 'opencode-replay.json'
New-Item -ItemType Directory -Force -Path (Split-Path $codexTarget), (Split-Path $claudeTarget), $bridge | Out-Null
$dll = Join-Path $work 'AgentTrafficLight.Tests.dll'
$mcpExe = Join-Path $work 'Agent-Beacon-MCP.exe'

& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /optimize+ /platform:anycpu /out:$mcpExe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll (Join-Path $root 'TraeMcpHost.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test MCP helper compilation failed.' }
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:library /out:$dll /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll' /reference:'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll' ('/resource:' + $mcpExe + ',trae-mcp-host.exe') (Join-Path $root 'AgentTrafficLight.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test assembly compilation failed.' }

$oldHome = $env:AGENT_TRAFFIC_LIGHT_HOME
$env:AGENT_TRAFFIC_LIGHT_HOME = $homePath
Add-Type -LiteralPath $dll
$assembly = [Reflection.Assembly]::LoadFrom($dll)
$engineType = $assembly.GetType('AgentTrafficLightNative.MonitorEngine')
$taskType = $assembly.GetType('AgentTrafficLightNative.AgentTask')
$statusField = $taskType.GetField('Status', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$sourceField = $taskType.GetField('Source', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$idField = $taskType.GetField('Id', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$updatedField = $taskType.GetField('UpdatedAt', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$pendingExecField = $taskType.GetField('PendingExec', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$scan = $engineType.GetMethod('Scan', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$parseCodex = $engineType.GetMethod('ParseCodex', [Reflection.BindingFlags]'Instance,NonPublic')
$parseBridge = $engineType.GetMethod('ParseBridge', [Reflection.BindingFlags]'Instance,NonPublic')
$stateRulesType = $assembly.GetType('AgentTrafficLightNative.AgentStateRules')
$resolveForRuntime = $stateRulesType.GetMethod('ResolveForRuntime', [Reflection.BindingFlags]'Static,NonPublic,Public')
$fixtures = Join-Path $PSScriptRoot 'fixtures'

if ($assembly.GetType('AgentTrafficLightNative.TraeStateEngine') -or $engineType.GetMethod('ParseTrae', [Reflection.BindingFlags]'Instance,NonPublic') -or $engineType.GetMethod('ParseTraeChatSession', [Reflection.BindingFlags]'Instance,NonPublic')) { throw 'TRAE log/session fallback code is still present.' }
$processType = $assembly.GetType('AgentTrafficLightNative.AgentProcesses')
foreach ($name in @('TraeNeedsUserAttention','TraeHasVisualAttention')) {
  if ($processType.GetMethod($name, [Reflection.BindingFlags]'Static,NonPublic,Public')) { throw "$name fallback is still present." }
}
$codexUiProbe = $processType.GetMethod('CodexNeedsUserAttention', [Reflection.BindingFlags]'Static,NonPublic,Public')
$codexPromptText = $processType.GetMethod('IsCodexApprovalPromptText', [Reflection.BindingFlags]'Static,NonPublic,Public')
if (-not $codexUiProbe -or -not $codexPromptText -or -not ($assembly.GetReferencedAssemblies().Name -match '^UIAutomation')) { throw 'Minimal Codex approval probe is missing.' }
if (-not $codexPromptText.Invoke($null, @('Do you want to allow ChatGPT to run this command?')) -or -not $codexPromptText.Invoke($null, @('Approval required by Codex'))) { throw 'Codex approval prompt text was not recognized.' }
Write-Host 'PASS TRAE fallbacks are absent and minimal Codex approval probe is present'

function Invoke-Replay([string]$source, [string[]]$files, [string]$target, [string[]]$expected) {
  $engine = [Activator]::CreateInstance($engineType, $true)
  $actual = @()
  foreach ($file in $files) {
    Copy-Item -LiteralPath $file -Destination $target -Force
    $arguments = @(0)
    $tasks = $scan.Invoke($engine, $arguments)
    $latest = $tasks | Where-Object { $sourceField.GetValue($_) -eq $source } | Sort-Object { [long]$updatedField.GetValue($_) } -Descending | Select-Object -First 1
    $actual += $statusField.GetValue($latest)
  }
  if (($actual -join ',') -ne ($expected -join ',')) { throw "$source replay failed: $($actual -join ' -> ')" }
  Write-Host "PASS ${source}: $($actual -join ' -> ')"
}

Invoke-Replay 'Codex' (1..4 | ForEach-Object { Join-Path $fixtures ('codex-0' + $_ + '-' + @('running','attention','complete','running')[$_-1] + '.jsonl') }) $codexTarget @('running','attention','complete','running')
Invoke-Replay 'Codex' (1..4 | ForEach-Object { Join-Path $fixtures ('codex-edit-0' + $_ + '-' + @('running','attention','running-after-approval','complete')[$_-1] + '.jsonl') }) $codexTarget @('running','running','running','complete')
Invoke-Replay 'Codex' (1..4 | ForEach-Object { Join-Path $fixtures ('codex-escalation-0' + $_ + '-' + @('running','attention','running-after-approval','complete')[$_-1] + '.jsonl') }) $codexTarget @('running','attention','running','complete')
$keywordCodex = $parseCodex.Invoke(([Activator]::CreateInstance($engineType, $true)), @([string](Join-Path $fixtures 'codex-keywords-no-attention.jsonl'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($keywordCodex) -ne 'running') { throw 'Codex keywords inside a normal tool call were misclassified as attention.' }
if (-not $pendingExecField.GetValue($keywordCodex)) { throw 'Codex pending exec was not marked for the conditional UI probe.' }
Write-Host 'PASS Codex JSONL structural attention and pending-exec probe gating'

Invoke-Replay 'Claude Code' (1..4 | ForEach-Object { Join-Path $fixtures ('claude-transcript-0' + $_ + '-' + @('running','attention','complete','running')[$_-1] + '.jsonl') }) $claudeTarget @('running','attention','complete','running')
Invoke-Replay 'OpenCode' (1..4 | ForEach-Object { Join-Path $fixtures ('opencode-0' + $_ + '-' + @('running','attention','complete','running')[$_-1] + '.json') }) $openTarget @('running','attention','complete','running')

$bridgeEngine = [Activator]::CreateInstance($engineType, $true)
$invalidOpenCode = $parseBridge.Invoke($bridgeEngine, @([string](Join-Path $fixtures 'opencode-invalid-session.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()))
if ($invalidOpenCode.Count -ne 0) { throw 'OpenCode bridge accepted an invalid session.' }
$nonMcpTrae = Join-Path $work 'trae-non-mcp.json'
[IO.File]::WriteAllText($nonMcpTrae, '{"source":"TRAE","id":"trae-log:legacy","sessionId":"legacy-session","status":"running","updatedAt":1784607000000}', [Text.Encoding]::UTF8)
if ($parseBridge.Invoke($bridgeEngine, @([string]$nonMcpTrae, [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())).Count -ne 0) { throw 'TRAE accepted a non-MCP bridge event.' }
Write-Host 'PASS TRAE accepts MCP bridge events only'

$taskListType = [Collections.Generic.List``1].MakeGenericType($taskType)
function New-StateTask([string]$source, [string]$id, [string]$status, [long]$updated) {
  $item = [Activator]::CreateInstance($taskType, $true)
  $idField.SetValue($item,$id); $sourceField.SetValue($item,$source); $statusField.SetValue($item,$status); $updatedField.SetValue($item,$updated)
  return $item
}
function Resolve-State([string]$source, $candidate, [long]$runtimeStart, [long]$seenAt, $previous) {
  $args = New-Object 'object[]' 5; $args[0]=$source; $args[1]=$candidate; $args[2]=$runtimeStart; $args[3]=$seenAt; $args[4]=$previous
  return $resolveForRuntime.Invoke($null,$args)
}

$mcpEngine = [Activator]::CreateInstance($engineType, $true)
$mcpStates = @(); $mcpRows = @()
foreach ($name in @('trae-mcp-01-running.json','trae-mcp-02-attention.json','trae-mcp-03-complete.json','trae-mcp-04-running.json')) {
  $task = $parseBridge.Invoke($mcpEngine, @([string](Join-Path $fixtures $name), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
  if ($null -eq $task) { throw "TRAE MCP fixture was rejected: $name" }
  $mcpRows += $task.PSObject.BaseObject; $mcpStates += $statusField.GetValue($task)
}
if (($mcpStates -join ',') -ne 'running,attention,complete,running') { throw "TRAE MCP lifecycle failed: $($mcpStates -join ' -> ')" }
$runtimeNow = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$mcpRunning = $mcpRows[0]; $mcpComplete = $mcpRows[2]
if ($statusField.GetValue((Resolve-State 'TRAE' $mcpRunning ([long]$runtimeNow + 60000) ([long]$runtimeNow + 60000) $null)) -ne 'running') { throw 'TRAE MCP running state did not survive monitor restart.' }
$completedAt = [long]$updatedField.GetValue($mcpComplete)
$sameTaskRunning = New-StateTask 'TRAE' 'trae-mcp:task-1' 'running' ([long]$completedAt + 1)
if ($statusField.GetValue((Resolve-State 'TRAE' $sameTaskRunning 0 0 $mcpComplete)) -ne 'complete') { throw 'TRAE MCP terminal lock reopened for the same task.' }
$newTaskRunning = New-StateTask 'TRAE' 'trae-mcp:task-2' 'running' ([long]$completedAt + 2)
if ($statusField.GetValue((Resolve-State 'TRAE' $newTaskRunning 0 0 $mcpComplete)) -ne 'running') { throw 'TRAE MCP terminal lock did not open for a new task.' }
Write-Host 'PASS TRAE MCP lifecycle, restart recovery and terminal lock'

foreach ($source in @('TRAE','Codex','Claude Code','OpenCode')) {
  $id = if ($source -eq 'TRAE') { 'trae-mcp:multi-trae' } else { 'multi:' + $source }
  $candidate = New-StateTask $source $id 'running' ([long]$runtimeNow + 2000)
  if ($statusField.GetValue((Resolve-State $source $candidate $runtimeNow $runtimeNow $null)) -ne 'running') { throw "Concurrent state failed: $source" }
}
$pixelType = $assembly.GetType('AgentTrafficLightNative.PixelPoleControl')
$pixel = [Activator]::CreateInstance($pixelType, $true)
$centers = $pixelType.GetMethod('HeadCenters', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($pixel, @(4, 150))
if ($centers.Count -ne 4 -or ($centers -join ',') -ne '48,116,184,252') { throw "Four-light layout failed: $($centers -join ',')" }
$pixel.Dispose()
Write-Host 'PASS four-agent concurrent state and pixel layout'

$integrationType = $assembly.GetType('AgentTrafficLightNative.Integration')
$ensureTraeMcp = $integrationType.GetMethod('EnsureTraeMcpHelper', [Reflection.BindingFlags]'Static,NonPublic')
$isTraeMcpPrepared = $integrationType.GetMethod('IsTraeMcpPrepared', [Reflection.BindingFlags]'Static,NonPublic,Public')
$upgradeDir = Join-Path $work 'trae-mcp-upgrade'
$oldUpgradeDir = $env:AGENT_BEACON_TRAE_MCP_DIR
$env:AGENT_BEACON_TRAE_MCP_DIR = $upgradeDir
New-Item -ItemType Directory -Force -Path $upgradeDir | Out-Null
$legacyHelper = Join-Path $upgradeDir 'Agent-Beacon-MCP-1.3.0.exe'
$stableHelper = Join-Path $upgradeDir 'Agent-Beacon-MCP.exe'
[IO.File]::WriteAllText($legacyHelper, 'legacy-1.3.0', [Text.Encoding]::UTF8)
if ($ensureTraeMcp.Invoke($null, @($true)) -ne 'updated' -or -not (Test-Path -LiteralPath $stableHelper) -or (Test-Path -LiteralPath $legacyHelper)) { throw 'TRAE MCP legacy helper migration failed.' }
$expectedHelperHash = (Get-FileHash -LiteralPath $stableHelper -Algorithm SHA256).Hash
if ($ensureTraeMcp.Invoke($null, @($true)) -ne 'current') { throw 'TRAE MCP identical helper was replaced.' }
[IO.File]::WriteAllText($stableHelper, 'stale-helper', [Text.Encoding]::UTF8)
if ($ensureTraeMcp.Invoke($null, @($true)) -ne 'updated' -or (Get-FileHash -LiteralPath $stableHelper -Algorithm SHA256).Hash -ne $expectedHelperHash) { throw 'TRAE MCP hash refresh failed.' }
$escapedStableHelper = $stableHelper.Replace('\', '\\')
[IO.File]::WriteAllText((Join-Path $upgradeDir 'trae-mcp-config.json'), '{"mcpServers":{"agent_beacon":{"command":"' + $escapedStableHelper + '","args":["--mcp-server"]}}}', [Text.Encoding]::UTF8)
if (-not $isTraeMcpPrepared.Invoke($null, @())) { throw 'TRAE MCP stable configuration was not recognized.' }
$env:AGENT_BEACON_TRAE_MCP_DIR = $oldUpgradeDir
$env:AGENT_TRAFFIC_LIGHT_HOME = $oldHome
Write-Host 'PASS TRAE MCP stable-path upgrade and hash refresh'
