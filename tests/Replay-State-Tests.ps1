$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ('atl-replay-' + [Guid]::NewGuid().ToString('N'))
$homePath = Join-Path $work 'home'
$traeTarget = Join-Path $homePath 'AppData\Roaming\TRAE SOLO CN\logs\20260718\main.log'
$codexTarget = Join-Path $homePath '.codex\sessions\2026\07\18\replay.jsonl'
$claudeTarget = Join-Path $homePath '.claude\projects\replay\claude-replay.jsonl'
$bridge = Join-Path $homePath '.agent-traffic-light\events'
$openTarget = Join-Path $bridge 'opencode-replay.json'
New-Item -ItemType Directory -Force -Path (Split-Path $traeTarget), (Split-Path $codexTarget), (Split-Path $claudeTarget), $bridge | Out-Null
$dll = Join-Path $work 'AgentTrafficLight.Tests.dll'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:library /out:$dll /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll' /reference:'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll' (Join-Path $root 'AgentTrafficLight.cs') (Join-Path $root 'TraeStateEngine.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test assembly compilation failed.' }
$env:AGENT_TRAFFIC_LIGHT_HOME = $homePath
Add-Type -LiteralPath $dll
$assembly = [Reflection.Assembly]::LoadFrom($dll)
$engineType = $assembly.GetType('AgentTrafficLightNative.MonitorEngine')
$taskType = $assembly.GetType('AgentTrafficLightNative.AgentTask')
$statusField = $taskType.GetField('Status', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$sourceField = $taskType.GetField('Source', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$updatedField = $taskType.GetField('UpdatedAt', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$scan = $engineType.GetMethod('Scan', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$parseTraeWork = $engineType.GetMethod('ParseTraeChatSession', [Reflection.BindingFlags]'Instance,NonPublic')
$parseTraeLog = $engineType.GetMethod('ParseTrae', [Reflection.BindingFlags]'Instance,NonPublic')
$parseCodex = $engineType.GetMethod('ParseCodex', [Reflection.BindingFlags]'Instance,NonPublic')
$parseBridge = $engineType.GetMethod('ParseBridge', [Reflection.BindingFlags]'Instance,NonPublic')
$processType = $assembly.GetType('AgentTrafficLightNative.AgentProcesses')
$traeUiText = $processType.GetMethod('IsTraeAttentionText', [Reflection.BindingFlags]'Static,NonPublic,Public')
$codexUiText = $processType.GetMethod('IsCodexAttentionText', [Reflection.BindingFlags]'Static,NonPublic,Public')
$codexUiHost = $processType.GetMethod('IsCodexUiHostProcessName', [Reflection.BindingFlags]'Static,NonPublic,Public')
$codexApprovalSignals = $processType.GetMethod('CodexApprovalSignals', [Reflection.BindingFlags]'Static,NonPublic,Public')
$stateRulesType = $assembly.GetType('AgentTrafficLightNative.AgentStateRules')
$latestForSource = $stateRulesType.GetMethod('LatestForSource', [Reflection.BindingFlags]'Static,NonPublic,Public')
$latestForTrae = $stateRulesType.GetMethod('LatestForTrae', [Reflection.BindingFlags]'Static,NonPublic,Public')
$hasFreshTraeContinuation = $stateRulesType.GetMethod('HasFreshTraeContinuation', [Reflection.BindingFlags]'Static,NonPublic,Public')
$traeResolutionBeatsFallback = $stateRulesType.GetMethod('TraeResolutionBeatsFallback', [Reflection.BindingFlags]'Static,NonPublic,Public')
$nextTraeVisualMissCount = $stateRulesType.GetMethod('NextTraeVisualMissCount', [Reflection.BindingFlags]'Static,NonPublic,Public')
$resolveForRuntime = $stateRulesType.GetMethod('ResolveForRuntime', [Reflection.BindingFlags]'Static,NonPublic,Public')
$traeDisplayType = $assembly.GetType('AgentTrafficLightNative.TraeDisplayStateMachine')
$traeDisplayResolve = $traeDisplayType.GetMethod('Resolve', [Reflection.BindingFlags]'Instance,NonPublic,Public')

function Invoke-Replay([string]$source, [string[]]$files, [string]$target) {
  $engine = [Activator]::CreateInstance($engineType, $true)
  $actual = @()
  foreach ($file in $files) {
    Copy-Item -LiteralPath $file -Destination $target -Force
    $arguments = @(0)
    $tasks = $scan.Invoke($engine, $arguments)
    $latest = $tasks | Where-Object { $sourceField.GetValue($_) -eq $source } | Sort-Object { [long]$updatedField.GetValue($_) } -Descending | Select-Object -First 1
    $actual += $statusField.GetValue($latest)
  }
  $expected = @('running', 'attention', 'complete', 'running')
  if (($actual -join ',') -ne ($expected -join ',')) { throw "$source replay failed: $($actual -join ' -> ')" }
  Write-Host "PASS ${source}: green -> yellow -> red -> green"
}

function Invoke-CodexEditReplay([string[]]$files, [string]$target) {
  $engine = [Activator]::CreateInstance($engineType, $true)
  $actual = @()
  foreach ($file in $files) {
    Copy-Item -LiteralPath $file -Destination $target -Force
    $arguments = @(0)
    $tasks = $scan.Invoke($engine, $arguments)
    $latest = $tasks | Where-Object { $sourceField.GetValue($_) -eq 'Codex' } | Sort-Object { [long]$updatedField.GetValue($_) } -Descending | Select-Object -First 1
    $actual += $statusField.GetValue($latest)
  }
  $expected = @('running', 'running', 'running', 'complete')
  if (($actual -join ',') -ne ($expected -join ',')) { throw "Codex normal edit replay failed: $($actual -join ' -> ')" }
  Write-Host 'PASS Codex normal edits remain green until completion'
}

function Invoke-TraeWorkReplay([string[]]$files) {
  $engine = [Activator]::CreateInstance($engineType, $true)
  $actual = @()
  foreach ($file in $files) {
    $mtime = [long](([DateTime]::UtcNow - [DateTime]'1970-01-01').TotalMilliseconds)
    $tasks = $parseTraeWork.Invoke($engine, @($file, $mtime))
    $latest = $tasks | Select-Object -First 1
    $actual += $statusField.GetValue($latest)
  }
  $expected = @('running', 'attention', 'complete', 'running')
  if (($actual -join ',') -ne ($expected -join ',')) { throw "TRAE Work replay failed: $($actual -join ' -> ')" }
  Write-Host 'PASS TRAE Work model: green -> yellow -> red -> green'
  [string]$rewrite = Join-Path $fixtures ($(if ($files[0].EndsWith('.jsonl')) { 'trae-work-jsonl-05-background-rewrite.jsonl' } else { 'trae-work-05-background-rewrite.json' }))
  $mtime = [long](([DateTime]::UtcNow - [DateTime]'1970-01-01').TotalMilliseconds)
  $rewrittenTasks = $parseTraeWork.Invoke($engine, @([string]$rewrite, [long]$mtime))
  $rewrittenStatus = $statusField.GetValue(($rewrittenTasks | Select-Object -First 1))
  if ($rewrittenStatus -ne 'complete') { throw "TRAE background rewrite regression: expected complete, got $rewrittenStatus" }
  Write-Host 'PASS TRAE Work completed request ignores later background rewrite'
}

$fixtures = Join-Path $PSScriptRoot 'fixtures'
Invoke-Replay 'TRAE' (1..4 | ForEach-Object { Join-Path $fixtures ('trae-0' + $_ + '-' + @('running','attention','complete','running')[$_-1] + '.log') }) $traeTarget
Invoke-TraeWorkReplay (1..4 | ForEach-Object { Join-Path $fixtures ('trae-work-0' + $_ + '-' + @('running','attention','complete','running')[$_-1] + '.json') })
Invoke-TraeWorkReplay (1..4 | ForEach-Object { Join-Path $fixtures ('trae-work-jsonl-0' + $_ + '-' + @('running','attention','complete','running')[$_-1] + '.jsonl') })
$popupEngine = [Activator]::CreateInstance($engineType, $true)
$popupActual = @()
foreach ($name in @('trae-popup-01-attention.json','trae-popup-02-running-after-reply.json','trae-popup-03-complete-with-stale-marker.json')) {
  $popupTasks = $parseTraeWork.Invoke($popupEngine, @([string](Join-Path $fixtures $name), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()))
  $popupActual += $statusField.GetValue(($popupTasks | Select-Object -First 1))
}
if (($popupActual -join ',') -ne 'attention,running,complete') { throw "TRAE popup lifecycle failed: $($popupActual -join ' -> ')" }
Write-Host 'PASS TRAE popup lifecycle: yellow -> green after reply -> red on completion'
$progressPopup = $parseTraeWork.Invoke(([Activator]::CreateInstance($engineType, $true)), @([string](Join-Path $fixtures 'trae-popup-progress-kind.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($progressPopup) -ne 'attention') { throw 'TRAE progressMessage confirmation card was not yellow' }
$progressFinal = $parseTraeWork.Invoke(([Activator]::CreateInstance($engineType, $true)), @([string](Join-Path $fixtures 'trae-final-progress-kind-model0.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($progressFinal) -ne 'complete') { throw 'TRAE progressMessage final response with stale modelState=0 was not complete' }
Write-Host 'PASS TRAE progressMessage renderer: confirmation yellow and final response red'
$sameRequestRunning = $parseTraeWork.Invoke($popupEngine, @([string](Join-Path $fixtures 'trae-popup-04-same-request-running.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($sameRequestRunning) -ne 'running') { throw 'TRAE stale confirmation node masked later same-request execution' }
$sameRequestComplete = $parseTraeWork.Invoke($popupEngine, @([string](Join-Path $fixtures 'trae-popup-05-complete-stale-ui.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($sameRequestComplete) -ne 'complete') { throw 'TRAE stale confirmation node masked same-request completion' }
Write-Host 'PASS TRAE same request ignores stale confirmation after progress and completion'
$patchEngine = [Activator]::CreateInstance($engineType, $true)
$patchTasks = $parseTraeWork.Invoke($patchEngine, @([string](Join-Path $fixtures 'trae-work-jsonl-nested-patch.jsonl'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()))
if ($statusField.GetValue(($patchTasks | Select-Object -First 1)) -ne 'complete') { throw 'TRAE JSONL nested modelState patch was not applied' }
Write-Host 'PASS TRAE JSONL nested state patches are reconstructed'
$historyEngine = [Activator]::CreateInstance($engineType, $true)
$historyTasks = $parseTraeWork.Invoke($historyEngine, @([string](Join-Path $fixtures 'trae-work-jsonl-terminal-history-rewrite.jsonl'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()))
if ($statusField.GetValue(($historyTasks | Select-Object -First 1)) -ne 'complete') { throw 'TRAE JSONL terminal history was overwritten by background running state' }
Write-Host 'PASS TRAE JSONL terminal history survives restart reconstruction'
$explicitTerminalEngine = [Activator]::CreateInstance($engineType, $true)
$explicitTerminal = $parseTraeWork.Invoke($explicitTerminalEngine, @([string](Join-Path $fixtures 'trae-explicit-final-model2.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($explicitTerminal) -ne 'complete') { throw 'TRAE explicit final response with rewritten model state was not completed' }
$partialContinues = $parseTraeWork.Invoke($explicitTerminalEngine, @([string](Join-Path $fixtures 'trae-partial-complete-continues.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($partialContinues) -ne 'running') { throw 'TRAE partial completion with continuation was misclassified as terminal' }
Write-Host 'PASS TRAE explicit final response is red while partial-step continuation stays green'
$stalePhaseFinal = $parseTraeWork.Invoke($explicitTerminalEngine, @([string](Join-Path $fixtures 'trae-final-after-stale-phases.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($stalePhaseFinal) -ne 'complete') { throw 'TRAE final answer was masked by stale running or confirmation nodes' }
Write-Host 'PASS TRAE latest final answer overrides earlier stale running/confirmation nodes'
$genericFinal = $parseTraeWork.Invoke($explicitTerminalEngine, @([string](Join-Path $fixtures 'trae-generic-final-model2.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($genericFinal) -ne 'complete') { throw 'TRAE settled final response without completion keywords stayed green' }
$staleRunningSummaryFinal = $parseTraeWork.Invoke(([Activator]::CreateInstance($engineType, $true)), @([string](Join-Path $fixtures 'trae-final-stale-model0.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($staleRunningSummaryFinal) -ne 'complete') { throw 'TRAE final response was overridden by stale modelState.value=0' }
$genericStaleConfirmation = $parseTraeWork.Invoke($explicitTerminalEngine, @([string](Join-Path $fixtures 'trae-generic-final-stale-confirmation.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($genericStaleConfirmation) -ne 'complete') { throw 'TRAE generic final response was masked by a stale confirmation node' }
Write-Host 'PASS TRAE settled final responses outrank stale modelState=0 without relying on completion keywords'
$outOfOrder = $parseTraeWork.Invoke(([Activator]::CreateInstance($engineType, $true)), @([string](Join-Path $fixtures 'trae-requests-out-of-order.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($outOfOrder) -ne 'complete') { throw 'TRAE selected array order instead of the newest request timestamp' }
Write-Host 'PASS TRAE newest request is selected by real timestamp, not array position'
$resumeEngine = [Activator]::CreateInstance($engineType, $true)
[void]$parseTraeWork.Invoke($resumeEngine, @([string](Join-Path $fixtures 'trae-generic-final-model2.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()))
$resumedAfterTerminal = $parseTraeWork.Invoke($resumeEngine, @([string](Join-Path $fixtures 'trae-terminal-then-new-progress.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($resumedAfterTerminal) -ne 'running') { throw 'TRAE terminal lock rejected genuinely appended progress in the same request' }
Write-Host 'PASS TRAE terminal lock opens only for genuinely appended response evidence'
Invoke-Replay 'Codex' (1..4 | ForEach-Object { Join-Path $fixtures ('codex-0' + $_ + '-' + @('running','attention','complete','running')[$_-1] + '.jsonl') }) $codexTarget
Invoke-CodexEditReplay (1..4 | ForEach-Object { Join-Path $fixtures ('codex-edit-0' + $_ + '-' + @('running','attention','running-after-approval','complete')[$_-1] + '.jsonl') }) $codexTarget
$keywordCodex = $parseCodex.Invoke(([Activator]::CreateInstance($engineType, $true)), @([string](Join-Path $fixtures 'codex-keywords-no-attention.jsonl'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($keywordCodex) -ne 'running') { throw 'Codex keywords inside a normal tool call were misclassified as attention' }
Write-Host 'PASS Codex only structural interaction events trigger yellow'
Invoke-Replay 'Claude Code' (1..4 | ForEach-Object { Join-Path $fixtures ('claude-transcript-0' + $_ + '-' + @('running','attention','complete','running')[$_-1] + '.jsonl') }) $claudeTarget
Invoke-Replay 'OpenCode' (1..4 | ForEach-Object { Join-Path $fixtures ('opencode-0' + $_ + '-' + @('running','attention','complete','running')[$_-1] + '.json') }) $openTarget
$invalidOpenCodeEngine = [Activator]::CreateInstance($engineType, $true)
$invalidOpenCode = $parseBridge.Invoke($invalidOpenCodeEngine, @([string](Join-Path $fixtures 'opencode-invalid-session.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()))
if ($invalidOpenCode.Count -ne 0) { throw 'OpenCode bridge accepted an event without a valid sessionId' }
Write-Host 'PASS OpenCode bridge rejects missing or invalid session IDs'
$traeAttentionNames = $processType.GetField('TraeAttentionNames', [Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
foreach ($sample in @($traeAttentionNames[0], $traeAttentionNames[1], $traeAttentionNames[2], 'Waiting for your response')) { if (-not $traeUiText.Invoke($null, @($sample))) { throw "TRAE UI attention text not detected: $sample" } }
if ($traeUiText.Invoke($null, @('Task completed normally'))) { throw 'TRAE UI completion text was misclassified as attention' }
Write-Host 'PASS TRAE popup UI attention detection'
foreach ($sample in @('是否允许 ChatGPT 编辑以下文件?', '允许一次', '允许一次 展开', 'Do you want to allow Codex to run this command?')) { if (-not $codexUiText.Invoke($null, @($sample))) { throw "Codex UI attention text not detected: $sample" } }
if ($codexUiText.Invoke($null, @('正在编辑文件并运行测试'))) { throw 'Codex normal activity text was misclassified as attention' }
if (-not $codexUiHost.Invoke($null, @('ChatGPT')) -or -not $codexUiHost.Invoke($null, @('codex')) -or $codexUiHost.Invoke($null, @('codex-code-mode-host'))) { throw 'Codex desktop UI host process mapping failed' }
if (-not $codexApprovalSignals.Invoke($null, @($true,$true,$true)) -or $codexApprovalSignals.Invoke($null, @($true,$false,$true)) -or $codexApprovalSignals.Invoke($null, @($false,$true,$true))) { throw 'Codex approval prompt/action combination rule failed' }
Write-Host 'PASS Codex ChatGPT-hosted visible approval detection'
$logEngine = [Activator]::CreateInstance($engineType, $true)
$mtime = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$longRunning = $parseTraeLog.Invoke($logEngine, @([string](Join-Path $fixtures 'trae-long-running.log'), [long]$mtime)) | Select-Object -First 1
if ($statusField.GetValue($longRunning) -ne 'running') { throw 'TRAE running task incorrectly became complete because of inactivity' }
Write-Host 'PASS TRAE inactivity never fabricates a red completion state'
$absorbed = $parseTraeLog.Invoke($logEngine, @([string](Join-Path $fixtures 'trae-terminal-absorbs-background.log'), [long]$mtime)) | Select-Object -First 1
if ($statusField.GetValue($absorbed) -ne 'complete') { throw 'TRAE background activity reopened a completed request' }
Write-Host 'PASS TRAE completed request absorbs later background activity'
$taskListType = [Collections.Generic.List``1].MakeGenericType($taskType)
$mixedTrae = [Activator]::CreateInstance($taskListType)
$now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$oldAt = [long]$now - 1000
foreach ($spec in @(@('trae-chat:old-complete','complete',$oldAt), @('trae-ui-attention','attention',$now))) {
  $item = [Activator]::CreateInstance($taskType, $true)
  $taskType.GetField('Id',[Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($item,[string]$spec[0])
  $sourceField.SetValue($item,'TRAE'); $statusField.SetValue($item,[string]$spec[1]); $updatedField.SetValue($item,[long]$spec[2])
  $mixedTrae.Add($item)
}
$latestArgs = New-Object 'object[]' 2; $latestArgs[0] = 'TRAE'; $latestArgs[1] = $mixedTrae
$mixedLatest = $latestForSource.Invoke($null,$latestArgs)
if ($statusField.GetValue($mixedLatest) -ne 'attention') { throw 'TRAE live popup was masked by a completed chat request' }
Write-Host 'PASS TRAE live popup overrides an older completed chat request'

function New-StateTask([string]$source, [string]$id, [string]$status, [long]$updated) {
  $item = [Activator]::CreateInstance($taskType, $true)
  $taskType.GetField('Id',[Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($item,$id)
  $sourceField.SetValue($item,$source); $statusField.SetValue($item,$status); $updatedField.SetValue($item,$updated)
  return $item
}
function Resolve-State([string]$source, $candidate, [long]$runtimeStart, [long]$seenAt, $previous) {
  $args = New-Object 'object[]' 5; $args[0]=$source; $args[1]=$candidate; $args[2]=$runtimeStart; $args[3]=$seenAt; $args[4]=$previous
  return $resolveForRuntime.Invoke($null,$args)
}
$runtimeNow = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$staleRunning = New-StateTask 'TRAE' 'trae:stale-running' 'running' ([long]$runtimeNow - 60000)
$startupIdle = Resolve-State 'TRAE' $staleRunning $runtimeNow $runtimeNow $null
if ($statusField.GetValue($startupIdle) -ne 'complete' -or -not $taskType.GetField('Id',[Reflection.BindingFlags]'Instance,NonPublic,Public').GetValue($startupIdle).StartsWith('idle:')) { throw 'TRAE startup reused a stale green task' }
Write-Host 'PASS TRAE startup ignores stale running history and shows red idle'
$traeAlreadyOpen = Resolve-State 'TRAE' $staleRunning ([long]$runtimeNow - 3600000) $runtimeNow $null
if ($statusField.GetValue($traeAlreadyOpen) -ne 'complete' -or -not $taskType.GetField('Id',[Reflection.BindingFlags]'Instance,NonPublic,Public').GetValue($traeAlreadyOpen).StartsWith('idle:')) { throw 'TRAE process start time overrode the Agent Beacon startup baseline' }
Write-Host 'PASS TRAE already open before Agent Beacon cannot revive an old green state'
$justBeforeStartup = New-StateTask 'TRAE' 'trae:just-before-startup' 'running' ([long]$runtimeNow - 1)
if ($statusField.GetValue((Resolve-State 'TRAE' $justBeforeStartup ([long]$runtimeNow - 3600000) $runtimeNow $null)) -ne 'complete') { throw 'TRAE one-millisecond-old running state appeared green at startup' }
Write-Host 'PASS TRAE startup has no stale-green grace window'
$pendingPopup = New-StateTask 'TRAE' 'trae:pending-popup' 'attention' ([long]$runtimeNow - 60000)
if ($statusField.GetValue((Resolve-State 'TRAE' $pendingPopup $runtimeNow $runtimeNow $null)) -ne 'attention') { throw 'TRAE pending popup was suppressed by startup baseline' }
Write-Host 'PASS TRAE pending popup remains yellow across app restart'
$freshRunning = New-StateTask 'TRAE' 'trae:fresh-running' 'running' ([long]$runtimeNow + 1000)
if ($statusField.GetValue((Resolve-State 'TRAE' $freshRunning $runtimeNow $runtimeNow $null)) -ne 'running') { throw 'TRAE fresh task did not become green' }
Write-Host 'PASS TRAE fresh task becomes green after startup'
$terminal = New-StateTask 'TRAE' 'trae:terminal-lock' 'complete' $runtimeNow
$background = New-StateTask 'TRAE' 'trae:terminal-lock' 'running' $runtimeNow
if ($statusField.GetValue((Resolve-State 'TRAE' $background ([long]$runtimeNow - 10000) ([long]$runtimeNow - 10000) $terminal)) -ne 'complete') { throw 'TRAE terminal state regressed to green' }
Write-Host 'PASS terminal state cannot regress without a newer event'
$newerBackground = New-StateTask 'TRAE' 'trae:background-heartbeat' 'running' ([long]$runtimeNow + 60000)
if ($statusField.GetValue((Resolve-State 'TRAE' $newerBackground ([long]$runtimeNow - 10000) ([long]$runtimeNow - 10000) $terminal)) -ne 'complete') { throw 'TRAE newer background heartbeat reopened a completed request' }
Write-Host 'PASS TRAE terminal state absorbs newer background heartbeats with a different id'
$explicitField = $taskType.GetField('ExplicitStart',[Reflection.BindingFlags]'Instance,NonPublic,Public')
$reliableField = $taskType.GetField('ReliableStart',[Reflection.BindingFlags]'Instance,NonPublic,Public')
$startedField = $taskType.GetField('StartedAt',[Reflection.BindingFlags]'Instance,NonPublic,Public')
$newRequest = New-StateTask 'TRAE' 'trae-chat:new-request' 'running' ([long]$runtimeNow + 61000)
$explicitField.SetValue($newRequest,$true); $reliableField.SetValue($newRequest,$true); $startedField.SetValue($newRequest,[long]$runtimeNow + 61000)
if ($statusField.GetValue((Resolve-State 'TRAE' $newRequest ([long]$runtimeNow - 10000) ([long]$runtimeNow - 10000) $terminal)) -ne 'running') { throw 'TRAE genuine new request did not release terminal lock' }
Write-Host 'PASS TRAE only a timestamped explicit new request releases terminal lock'
$waitingForReply = New-StateTask 'TRAE' 'trae-ui-attention' 'attention' $runtimeNow
$sameRequestContinues = New-StateTask 'TRAE' 'trae-chat:session:request-1' 'running' ([long]$runtimeNow + 1000)
$explicitField.SetValue($sameRequestContinues,$true); $reliableField.SetValue($sameRequestContinues,$true); $startedField.SetValue($sameRequestContinues,[long]$runtimeNow - 5000)
if ($statusField.GetValue((Resolve-State 'TRAE' $sameRequestContinues ([long]$runtimeNow - 10000) ([long]$runtimeNow - 10000) $waitingForReply)) -ne 'running') { throw 'TRAE same request stayed yellow after confirmation' }
$continuationList = [Activator]::CreateInstance($taskListType); $continuationList.Add($sameRequestContinues)
$continuationArgs = New-Object 'object[]' 2; $continuationArgs[0] = $continuationList; $continuationArgs[1] = [long]$runtimeNow
if (-not $hasFreshTraeContinuation.Invoke($null,$continuationArgs)) { throw 'TRAE fresh structured continuation did not clear the UI attention latch' }
Write-Host 'PASS TRAE confirmation resumes the same structured request: yellow -> green'
$resolutionArgs = New-Object 'object[]' 3; $resolutionArgs[0] = $sameRequestContinues; $resolutionArgs[1] = [long]$runtimeNow; $resolutionArgs[2] = $true
if (-not $traeResolutionBeatsFallback.Invoke($null,$resolutionArgs)) { throw 'TRAE structured continuation did not beat stale UI yellow fallback' }
$resolutionArgs[0] = $terminal; $resolutionArgs[1] = [long]$runtimeNow + 1000; $resolutionArgs[2] = $false
if (-not $traeResolutionBeatsFallback.Invoke($null,$resolutionArgs)) { throw 'TRAE completion did not beat stale UI yellow fallback' }
Write-Host 'PASS TRAE structured running/completion suppress stale UI yellow fallback'
$missArgs = New-Object 'object[]' 4; $missArgs[0]=1; $missArgs[1]=$false; $missArgs[2]=$false; $missArgs[3]=$false
if ([int]$nextTraeVisualMissCount.Invoke($null,$missArgs) -ne 1) { throw 'TRAE unavailable visual observation incorrectly cleared yellow' }
$missArgs[1]=$true; $missArgs[3]=$true
if ([int]$nextTraeVisualMissCount.Invoke($null,$missArgs) -ne 2) { throw 'TRAE second observed missing marker did not clear yellow threshold' }
$missArgs[0]=2; $missArgs[2]=$true
if ([int]$nextTraeVisualMissCount.Invoke($null,$missArgs) -ne 0) { throw 'TRAE visible marker did not reset missing-marker counter' }
Write-Host 'PASS TRAE visual yellow clears only after two observable missing-marker samples'
$staleContinuation = New-StateTask 'TRAE' 'trae-chat:session:request-1' 'running' ([long]$runtimeNow - 1)
if ($statusField.GetValue((Resolve-State 'TRAE' $staleContinuation ([long]$runtimeNow - 10000) ([long]$runtimeNow - 10000) $waitingForReply)) -ne 'attention') { throw 'TRAE stale running history incorrectly cleared yellow' }
Write-Host 'PASS TRAE stale running history cannot clear a current yellow state'
$authorityTasks = [Activator]::CreateInstance($taskListType)
$chatTerminal = New-StateTask 'TRAE' 'trae-chat:session:request-1' 'complete' $runtimeNow
$logHeartbeat = New-StateTask 'TRAE' 'trae:file-heartbeat' 'running' ([long]$runtimeNow + 120000)
$authorityTasks.Add($chatTerminal); $authorityTasks.Add($logHeartbeat)
$authorityArgs = New-Object 'object[]' 1; $authorityArgs[0] = $authorityTasks
if ($statusField.GetValue($latestForTrae.Invoke($null,$authorityArgs)) -ne 'complete') { throw 'TRAE generic log outranked the structured chat terminal record' }
Write-Host 'PASS TRAE structured chat state outranks later generic log activity'
$gateRunning = New-StateTask 'TRAE' 'trae-chat:gate-request' 'running' $runtimeNow
$gateComplete = New-StateTask 'TRAE' 'trae-chat:gate-request' 'complete' ([long]$runtimeNow + 100)
$display = [Activator]::CreateInstance($traeDisplayType,$true)
$displayArgs = New-Object 'object[]' 5; $displayArgs[0]=$gateComplete; $displayArgs[1]=$gateRunning; $displayArgs[2]=[long]$runtimeNow - 10000; $displayArgs[3]=[long]$runtimeNow - 10000; $displayArgs[4]=[long]$runtimeNow
if ($statusField.GetValue($traeDisplayResolve.Invoke($display,$displayArgs)) -ne 'running') { throw 'TRAE display state machine showed transient terminal immediately' }
$displayArgs[0]=New-StateTask 'TRAE' 'trae-ui-attention' 'attention' ([long]$runtimeNow + 1000); $displayArgs[4]=[long]$runtimeNow + 1000
if ($statusField.GetValue($traeDisplayResolve.Invoke($display,$displayArgs)) -ne 'attention') { throw 'TRAE display state machine delayed a confirmation behind terminal debounce' }
$display = [Activator]::CreateInstance($traeDisplayType,$true); $displayArgs[0]=$gateComplete; $displayArgs[4]=[long]$runtimeNow
[void]$traeDisplayResolve.Invoke($display,$displayArgs); $displayArgs[4]=[long]$runtimeNow + 5100
if ($statusField.GetValue($traeDisplayResolve.Invoke($display,$displayArgs)) -ne 'complete') { throw 'TRAE display state machine did not accept a stable terminal' }
Write-Host 'PASS TRAE dedicated display state machine: transient terminal suppressed, yellow immediate, stable red accepted'
foreach ($source in @('TRAE','Codex','Claude Code','OpenCode')) { $candidate = New-StateTask $source ("multi:"+$source) 'running' ([long]$runtimeNow + 2000); if ($statusField.GetValue((Resolve-State $source $candidate $runtimeNow $runtimeNow $null)) -ne 'running') { throw "Concurrent state failed: $source" } }
Write-Host 'PASS four-agent concurrent state resolution'
$pixelType = $assembly.GetType('AgentTrafficLightNative.PixelPoleControl')
$headCenters = $pixelType.GetMethod('HeadCenters', [Reflection.BindingFlags]'Instance,NonPublic')
$pixel = [Activator]::CreateInstance($pixelType, $true)
$centers = $headCenters.Invoke($pixel, @(4, 150))
if ($centers.Count -ne 4 -or ($centers -join ',') -ne '48,116,184,252') { throw "Four-light pixel pole layout failed: $($centers -join ',')" }
$pixel.Dispose()
Write-Host 'PASS four OpenCode-era lights render on one orthogonal pixel pole'

$mcpEngine = [Activator]::CreateInstance($engineType, $true)
$mcpStates = @(); $mcpTasks = [Activator]::CreateInstance($taskListType)
foreach ($name in @('trae-mcp-01-running.json','trae-mcp-02-attention.json','trae-mcp-03-complete.json','trae-mcp-04-running.json')) {
  $parsed = $parseBridge.Invoke($mcpEngine, @([string](Join-Path $fixtures $name), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()))
  $task = $parsed | Select-Object -First 1
  if ($null -eq $task) { throw "TRAE MCP fixture was rejected: $name" }
  $mcpStates += $statusField.GetValue($task); $mcpTasks.Add($task)
}
if (($mcpStates -join ',') -ne 'running,attention,complete,running') { throw "TRAE MCP lifecycle failed: $($mcpStates -join ' -> ')" }
Write-Host 'PASS TRAE local MCP lifecycle: green -> yellow -> red -> green'

$mcpComplete = ($parseBridge.Invoke($mcpEngine, @([string](Join-Path $fixtures 'trae-mcp-03-complete.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1).PSObject.BaseObject
$mcpAuthority = [Activator]::CreateInstance($taskListType); $mcpAuthority.Add($mcpComplete)
$lateHeartbeat = New-StateTask 'TRAE' 'trae:background-heartbeat-after-mcp' 'running' ([long]$runtimeNow + 600000); $mcpAuthority.Add($lateHeartbeat)
$mcpAuthorityArgs = New-Object 'object[]' 1; $mcpAuthorityArgs[0] = $mcpAuthority
if ($statusField.GetValue($latestForTrae.Invoke($null,$mcpAuthorityArgs)) -ne 'complete') { throw 'TRAE background logs overrode an MCP completion' }
Write-Host 'PASS TRAE MCP terminal state outranks later background log activity'

$mcpRunning = ($parseBridge.Invoke($mcpEngine, @([string](Join-Path $fixtures 'trae-mcp-01-running.json'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1).PSObject.BaseObject
$mcpImmediateDisplay = [Activator]::CreateInstance($traeDisplayType,$true)
$mcpDisplayArgs = New-Object 'object[]' 5; $mcpDisplayArgs[0]=$mcpComplete; $mcpDisplayArgs[1]=$mcpRunning; $mcpDisplayArgs[2]=0L; $mcpDisplayArgs[3]=0L; $mcpDisplayArgs[4]=[long]$runtimeNow
if ($statusField.GetValue($traeDisplayResolve.Invoke($mcpImmediateDisplay,$mcpDisplayArgs)) -ne 'complete') { throw 'TRAE explicit MCP completion was delayed by log terminal debounce' }
Write-Host 'PASS TRAE explicit MCP completion turns red immediately'

$mcpStartupDisplay = [Activator]::CreateInstance($traeDisplayType,$true)
$mcpDisplayArgs[0]=$mcpRunning; $mcpDisplayArgs[1]=$null; $mcpDisplayArgs[2]=[long]$runtimeNow + 60000; $mcpDisplayArgs[3]=[long]$runtimeNow + 60000
if ($statusField.GetValue($traeDisplayResolve.Invoke($mcpStartupDisplay,$mcpDisplayArgs)) -ne 'running') { throw 'TRAE explicit MCP running state was discarded after Agent Beacon restart' }
$mcpCompletedAt = [long]$updatedField.GetValue($mcpComplete)
$sameMcpRunning = New-StateTask 'TRAE' 'trae-mcp:task-1' 'running' ([long]$mcpCompletedAt + 1)
$mcpDisplayArgs[0]=$sameMcpRunning; $mcpDisplayArgs[1]=$mcpComplete
if ($statusField.GetValue($traeDisplayResolve.Invoke($mcpStartupDisplay,$mcpDisplayArgs)) -ne 'complete') { throw 'TRAE MCP terminal lock reopened for the same task id' }
$newMcpRunning = New-StateTask 'TRAE' 'trae-mcp:task-2' 'running' ([long]$mcpCompletedAt + 2)
$mcpDisplayArgs[0]=$newMcpRunning
if ($statusField.GetValue($traeDisplayResolve.Invoke($mcpStartupDisplay,$mcpDisplayArgs)) -ne 'running') { throw 'TRAE MCP terminal lock did not open for a new task id' }
Write-Host 'PASS TRAE MCP survives monitor restart and only a new task id releases red'
