$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ('atl-replay-' + [Guid]::NewGuid().ToString('N'))
$homePath = Join-Path $work 'home'
$traeTarget = Join-Path $homePath 'AppData\Roaming\TRAE SOLO CN\logs\20260718\main.log'
$codexTarget = Join-Path $homePath '.codex\sessions\2026\07\18\replay.jsonl'
$claudeTarget = Join-Path $homePath '.claude\projects\replay\claude-replay.jsonl'
$bridge = Join-Path $homePath '.agent-traffic-light\events'
New-Item -ItemType Directory -Force -Path (Split-Path $traeTarget), (Split-Path $codexTarget), (Split-Path $claudeTarget), $bridge | Out-Null
$dll = Join-Path $work 'AgentTrafficLight.Tests.dll'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:library /out:$dll /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll' /reference:'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll' (Join-Path $root 'AgentTrafficLight.cs')
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
$processType = $assembly.GetType('AgentTrafficLightNative.AgentProcesses')
$traeUiText = $processType.GetMethod('IsTraeAttentionText', [Reflection.BindingFlags]'Static,NonPublic,Public')
$stateRulesType = $assembly.GetType('AgentTrafficLightNative.AgentStateRules')
$latestForSource = $stateRulesType.GetMethod('LatestForSource', [Reflection.BindingFlags]'Static,NonPublic,Public')
$resolveForRuntime = $stateRulesType.GetMethod('ResolveForRuntime', [Reflection.BindingFlags]'Static,NonPublic,Public')

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
$patchEngine = [Activator]::CreateInstance($engineType, $true)
$patchTasks = $parseTraeWork.Invoke($patchEngine, @([string](Join-Path $fixtures 'trae-work-jsonl-nested-patch.jsonl'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()))
if ($statusField.GetValue(($patchTasks | Select-Object -First 1)) -ne 'complete') { throw 'TRAE JSONL nested modelState patch was not applied' }
Write-Host 'PASS TRAE JSONL nested state patches are reconstructed'
$historyEngine = [Activator]::CreateInstance($engineType, $true)
$historyTasks = $parseTraeWork.Invoke($historyEngine, @([string](Join-Path $fixtures 'trae-work-jsonl-terminal-history-rewrite.jsonl'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()))
if ($statusField.GetValue(($historyTasks | Select-Object -First 1)) -ne 'complete') { throw 'TRAE JSONL terminal history was overwritten by background running state' }
Write-Host 'PASS TRAE JSONL terminal history survives restart reconstruction'
Invoke-Replay 'Codex' (1..4 | ForEach-Object { Join-Path $fixtures ('codex-0' + $_ + '-' + @('running','attention','complete','running')[$_-1] + '.jsonl') }) $codexTarget
Invoke-Replay 'Claude Code' (1..4 | ForEach-Object { Join-Path $fixtures ('claude-transcript-0' + $_ + '-' + @('running','attention','complete','running')[$_-1] + '.jsonl') }) $claudeTarget
$traeAttentionNames = $processType.GetField('TraeAttentionNames', [Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
foreach ($sample in @($traeAttentionNames[0], $traeAttentionNames[1], $traeAttentionNames[2], 'Waiting for your response')) { if (-not $traeUiText.Invoke($null, @($sample))) { throw "TRAE UI attention text not detected: $sample" } }
if ($traeUiText.Invoke($null, @('Task completed normally'))) { throw 'TRAE UI completion text was misclassified as attention' }
Write-Host 'PASS TRAE popup UI attention detection'
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
foreach ($source in @('TRAE','Codex','Claude Code')) { $candidate = New-StateTask $source ("multi:"+$source) 'running' ([long]$runtimeNow + 2000); if ($statusField.GetValue((Resolve-State $source $candidate $runtimeNow $runtimeNow $null)) -ne 'running') { throw "Concurrent state failed: $source" } }
Write-Host 'PASS three-agent concurrent state resolution'
