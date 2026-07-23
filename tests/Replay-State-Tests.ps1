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
$appSources = @('AppInfo.cs','PixelTheme.cs','DpiSupport.cs','StateHistory.cs','UsageStatistics.cs','DesktopFeatures.cs','TaskCenter.cs','UpdateService.cs','AgentUi.cs','Integrations.cs','CodexEventCompatibility.cs','AgentTrafficLight.cs') | ForEach-Object { Join-Path $root $_ }

& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /optimize+ /platform:anycpu /out:$mcpExe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll (Join-Path $root 'TraeMcpHost.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test MCP helper compilation failed.' }
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:library /out:$dll /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll' /reference:'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll' ('/resource:' + $mcpExe + ',trae-mcp-host.exe') $appSources
if ($LASTEXITCODE -ne 0) { throw 'Test assembly compilation failed.' }

$oldHome = $env:AGENT_TRAFFIC_LIGHT_HOME
$oldDismissedPath = $env:AGENT_BEACON_DISMISSED_TASKS_PATH
$env:AGENT_TRAFFIC_LIGHT_HOME = $homePath
$env:AGENT_BEACON_DISMISSED_TASKS_PATH = Join-Path $work 'dismissed-tasks.json'
Add-Type -LiteralPath $dll
$assembly = [Reflection.Assembly]::LoadFrom($dll)
$engineType = $assembly.GetType('AgentTrafficLightNative.MonitorEngine')
$taskType = $assembly.GetType('AgentTrafficLightNative.AgentTask')
$statusField = $taskType.GetField('Status', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$sourceField = $taskType.GetField('Source', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$idField = $taskType.GetField('Id', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$startedField = $taskType.GetField('StartedAt', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$updatedField = $taskType.GetField('UpdatedAt', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$pendingExecField = $taskType.GetField('PendingExec', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$lastActivityField = $taskType.GetField('LastActivityAt', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$progressField = $taskType.GetField('Progress', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$phaseField = $taskType.GetField('Phase', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$cwdField = $taskType.GetField('Cwd', [Reflection.BindingFlags]'Instance,NonPublic,Public')
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
if (-not $codexPromptText.Invoke($null, @('Do you want to allow ChatGPT to run this command?')) -or -not $codexPromptText.Invoke($null, @('Approval required by Codex')) -or -not $codexPromptText.Invoke($null, @('是否允许补充当前用户 PATH，使安装后的 kdocs-cli 可在新终端中直接运行？'))) { throw 'Codex approval prompt text was not recognized.' }
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

$ruleLibrary = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'state-rule-cases.json') -Encoding UTF8 -Raw | ConvertFrom-Json
if ($ruleLibrary.version -ne 1 -or @($ruleLibrary.cases).Count -lt 5) { throw 'State rule regression library is incomplete.' }
foreach ($case in $ruleLibrary.cases) {
  $target = if ($case.source -eq 'Codex') { $codexTarget } elseif ($case.source -eq 'Claude Code') { $claudeTarget } elseif ($case.source -eq 'OpenCode') { $openTarget } else { throw "Unsupported replay source: $($case.source)" }
  $caseFiles = @($case.fixtures | ForEach-Object { Join-Path $fixtures $_ })
  Invoke-Replay ([string]$case.source) $caseFiles $target @($case.expected)
}
Write-Host "PASS state rule regression library: $(@($ruleLibrary.cases).Count) cases"
$keywordCodex = $parseCodex.Invoke(([Activator]::CreateInstance($engineType, $true)), @([string](Join-Path $fixtures 'codex-keywords-no-attention.jsonl'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) | Select-Object -First 1
if ($statusField.GetValue($keywordCodex) -ne 'running') { throw 'Codex keywords inside a normal tool call were misclassified as attention.' }
if (-not $pendingExecField.GetValue($keywordCodex)) { throw 'Codex pending exec was not marked for the conditional UI probe.' }
Write-Host 'PASS Codex JSONL structural attention and pending-exec probe gating'

$postCompleteRows = $parseCodex.Invoke(([Activator]::CreateInstance($engineType, $true)), @([string](Join-Path $fixtures 'codex-post-complete-trailing-events.jsonl'), [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()))
$postCompleteLatest = $postCompleteRows | Sort-Object { [long]$updatedField.GetValue($_) } -Descending | Select-Object -First 1
if ($postCompleteRows.Count -ne 1 -or $statusField.GetValue($postCompleteLatest) -ne 'complete') { throw 'Codex token-count or response postamble reopened a completed turn as a phantom green task.' }
Write-Host 'PASS Codex post-completion token and response events cannot reopen the task as green'

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

$compatibilityType = $assembly.GetType('AgentTrafficLightNative.CodexEventCompatibility')
$isToolCall = $compatibilityType.GetMethod('IsToolCall', [Reflection.BindingFlags]'Static,NonPublic,Public')
$isToolOutput = $compatibilityType.GetMethod('IsToolOutput', [Reflection.BindingFlags]'Static,NonPublic,Public')
$isExec = $compatibilityType.GetMethod('IsExec', [Reflection.BindingFlags]'Static,NonPublic,Public')
$isComputerUseAction = $compatibilityType.GetMethod('IsComputerUseAction', [Reflection.BindingFlags]'Static,NonPublic,Public')
if (-not $isToolCall.Invoke($null, @('custom_tool_call')) -or -not $isToolCall.Invoke($null, @('function_call')) -or -not $isToolOutput.Invoke($null, @('function_call_output')) -or -not $isExec.Invoke($null, @('exec')) -or -not $isExec.Invoke($null, @('exec_command')) -or -not $isComputerUseAction.Invoke($null, @('js','{"code":"await sky.launch_app({app:\"Agent-Beacon.exe\"});"}')) -or $isComputerUseAction.Invoke($null, @('js','{"code":"await sky.get_window_state({window:target});"}'))) { throw 'Codex event compatibility mappings are incomplete.' }
Write-Host 'PASS centralized Codex event compatibility mappings'

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

$runtimeType = $assembly.GetType('AgentTrafficLightNative.AgentRuntimeSnapshot')
$runtime = [Activator]::CreateInstance($runtimeType, $true)
$runtimeType.GetField('Sources').GetValue($runtime).Add('Codex') | Out-Null
$runtimeType.GetField('StartedAt').GetValue($runtime).Add('Codex', [long]($runtimeNow - 60 * 60 * 1000))
$activeRules = $assembly.GetType('AgentTrafficLightNative.ActiveTaskRules')
$aggregate = $activeRules.GetMethod('Aggregate', [Reflection.BindingFlags]'Static,NonPublic,Public')
$multiList = [Activator]::CreateInstance($taskListType)
$olderAttention = New-StateTask 'Codex' 'multi:waiting' 'attention' ([long]$runtimeNow - 2000)
$newerRunning = New-StateTask 'Codex' 'multi:running' 'running' ([long]$runtimeNow - 1000)
$multiList.Add($newerRunning); $multiList.Add($olderAttention)
$aggregateArgs = New-Object 'object[]' 6; $aggregateArgs[0]='Codex'; $aggregateArgs[1]=$multiList; $aggregateArgs[2]=$newerRunning; $aggregateArgs[3]=0L; $aggregateArgs[4]=0L; $aggregateArgs[5]=$null
$aggregateResult = $aggregate.Invoke($null, $aggregateArgs)
if ($statusField.GetValue($aggregateResult) -ne 'attention' -or $idField.GetValue($aggregateResult) -ne 'multi:waiting') { throw 'Multi-task aggregation did not prioritize an older waiting task over a newer running task.' }
if (-not $pixelType.GetEvent('CenterClicked', [Reflection.BindingFlags]'Instance,NonPublic,Public') -or -not $assembly.GetType('AgentTrafficLightNative.TaskQueuePopup')) { throw 'Pole status-center button or compact task queue popup is missing.' }
Write-Host 'PASS multi-task attention priority and pole task-center popup'

$sessionField = $taskType.GetField('SessionId', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$codexRules = $assembly.GetType('AgentTrafficLightNative.AgentStateRules')
$selectUiTarget = $codexRules.GetMethod('SelectCodexUiAttentionTarget', [Reflection.BindingFlags]'Static,NonPublic,Public')
$pendingOlder = New-StateTask 'Codex' 'codex:session-wait:turn-1' 'running' ([long]$runtimeNow - 4000)
$runningNewer = New-StateTask 'Codex' 'codex:session-run:turn-2' 'running' ([long]$runtimeNow - 1000)
$sessionField.SetValue($pendingOlder, 'session-wait'); $sessionField.SetValue($runningNewer, 'session-run'); $pendingExecField.SetValue($pendingOlder, $true)
$uiTargetInput = [Activator]::CreateInstance($taskListType); $uiTargetInput.Add($runningNewer); $uiTargetInput.Add($pendingOlder)
$uiTargetArgs = New-Object 'object[]' 1; $uiTargetArgs[0] = $uiTargetInput
$uiTarget = $selectUiTarget.Invoke($null, $uiTargetArgs)
if ($idField.GetValue($uiTarget) -ne 'codex:session-wait:turn-1') { throw 'Visible Codex approval was not assigned to the pending older session when another session was newer.' }
$uiOverlay = New-StateTask 'Codex' 'codex-ui-attention:codex:session-wait:turn-1' 'attention' ([long]$runtimeNow)
$sessionField.SetValue($uiOverlay, 'session-wait')
$overlayInput = [Activator]::CreateInstance($taskListType); $overlayInput.Add($pendingOlder); $overlayInput.Add($runningNewer); $overlayInput.Add($uiOverlay)
$overlayArgs = New-Object 'object[]' 1; $overlayArgs[0] = $overlayInput
$overlayResult = $activeRules.GetMethod('Collapse', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, $overlayArgs)
$overlayAttention = @($overlayResult | Where-Object { $statusField.GetValue($_) -eq 'attention' }).Count
$overlayRunning = @($overlayResult | Where-Object { $statusField.GetValue($_) -eq 'running' }).Count
if ($overlayResult.Count -ne 2 -or $overlayAttention -ne 1 -or $overlayRunning -ne 1) { throw 'Codex UI approval overlay did not preserve two concurrent sessions as one waiting and one running task.' }
$overlayAggregateArgs = New-Object 'object[]' 6; $overlayAggregateArgs[0]='Codex'; $overlayAggregateArgs[1]=$overlayResult; $overlayAggregateArgs[2]=$runningNewer; $overlayAggregateArgs[3]=0L; $overlayAggregateArgs[4]=0L; $overlayAggregateArgs[5]=$null
$overlayAggregate = $aggregate.Invoke($null, $overlayAggregateArgs)
if ($statusField.GetValue($overlayAggregate) -ne 'attention') { throw 'Codex pole did not become yellow when one of multiple sessions required attention.' }
Write-Host 'PASS concurrent Codex UI approval preserves task count and forces yellow aggregate'

$singleClaude = [Activator]::CreateInstance($taskListType); $singleClaude.Add((New-StateTask 'Claude Code' 'claude:one' 'attention' $runtimeNow))
$multiClaude = [Activator]::CreateInstance($taskListType); $multiClaude.Add((New-StateTask 'Claude Code' 'claude:wait' 'attention' $runtimeNow)); $multiClaude.Add((New-StateTask 'Claude Code' 'claude:run' 'running' $runtimeNow))
$allowClaudeOverride = $activeRules.GetMethod('AllowGlobalClaudeToolOverride', [Reflection.BindingFlags]'Static,NonPublic,Public')
$singleClaudeArgs = New-Object 'object[]' 1; $singleClaudeArgs[0] = $singleClaude
$multiClaudeArgs = New-Object 'object[]' 1; $multiClaudeArgs[0] = $multiClaude
if (-not $allowClaudeOverride.Invoke($null, $singleClaudeArgs) -or $allowClaudeOverride.Invoke($null, $multiClaudeArgs)) { throw 'Claude global child-process fallback can still overwrite a waiting session while another session runs.' }
Write-Host 'PASS concurrent Claude sessions cannot overwrite each other through the global tool-process fallback'

$runtimeType.GetField('Sources').GetValue($runtime).Add('TRAE') | Out-Null
$runtimeType.GetField('StartedAt').GetValue($runtime).Add('TRAE', [long]($runtimeNow - 60 * 60 * 1000))
$dismissedTask = New-StateTask 'TRAE' 'trae-mcp:stale-wait' 'attention' ([long]$runtimeNow - 3000)
$sessionField.SetValue($dismissedTask, 'stale-wait'); $lastActivityField.SetValue($dismissedTask, [long]$runtimeNow - 3000)
$dismissalType = $assembly.GetType('AgentTrafficLightNative.TaskDismissalStore')
$dismissalType.GetMethod('Dismiss', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @($dismissedTask)) | Out-Null
$dismissedInput = [Activator]::CreateInstance($taskListType); $dismissedInput.Add($dismissedTask)
$dismissedRelevant = $activeRules.GetMethod('Relevant', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @($dismissedInput, $runtime, $false))
if ($dismissedRelevant.Count -ne 0) { throw 'Manually cleared stale waiting task was immediately restored from the unchanged source event.' }
$newTraeEvent = New-StateTask 'TRAE' 'trae-mcp:stale-wait' 'running' ([long]$runtimeNow + 1000)
$sessionField.SetValue($newTraeEvent, 'stale-wait'); $lastActivityField.SetValue($newTraeEvent, [long]$runtimeNow + 1000)
$newTraeInput = [Activator]::CreateInstance($taskListType); $newTraeInput.Add($newTraeEvent)
$newTraeRelevant = $activeRules.GetMethod('Relevant', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @($newTraeInput, $runtime, $false))
if ($newTraeRelevant.Count -ne 1 -or $statusField.GetValue($newTraeRelevant[0]) -ne 'running') { throw 'A newer TRAE event did not automatically resume monitoring after manual stale-state cleanup.' }
$runtimeType.GetField('Sources').GetValue($runtime).Remove('TRAE') | Out-Null
$runtimeType.GetField('StartedAt').GetValue($runtime).Remove('TRAE') | Out-Null
Write-Host 'PASS manual stale-wait cleanup is persistent until a newer same-session event resumes monitoring'

$sameSessionAttention = New-StateTask 'Codex' 'codex-ui-attention:turn-1' 'attention' ([long]$runtimeNow - 2000)
$sameSessionRunning = New-StateTask 'Codex' 'codex:session-1:turn-1' 'running' ([long]$runtimeNow - 1000)
$sessionField.SetValue($sameSessionAttention, 'session-1'); $sessionField.SetValue($sameSessionRunning, 'session-1')
$duplicateInput = [Activator]::CreateInstance($taskListType); $duplicateInput.Add($sameSessionAttention); $duplicateInput.Add($sameSessionRunning)
$collapseArgs = New-Object 'object[]' 1; $collapseArgs[0] = $duplicateInput
$collapsed = $activeRules.GetMethod('Collapse', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, $collapseArgs)
if ($collapsed.Count -ne 1 -or $statusField.GetValue($collapsed[0]) -ne 'running' -or $idField.GetValue($collapsed[0]) -ne 'codex:session-1:turn-1') { throw 'A newer running event did not replace stale Codex UI attention for the same session.' }
Write-Host 'PASS same-session Codex attention is deduplicated and resolved by newer activity'

$oldSameSessionRunning = New-StateTask 'Codex' 'codex:session-terminal:old-running' 'running' ([long]$runtimeNow - 3000)
$newSameSessionComplete = New-StateTask 'Codex' 'codex:session-terminal:new-complete' 'complete' ([long]$runtimeNow - 1000)
$otherSessionRunning = New-StateTask 'Codex' 'codex:session-other:running' 'running' ([long]$runtimeNow - 4000)
$sessionField.SetValue($oldSameSessionRunning, 'session-terminal'); $sessionField.SetValue($newSameSessionComplete, 'session-terminal'); $sessionField.SetValue($otherSessionRunning, 'session-other')
$terminalInput = [Activator]::CreateInstance($taskListType); $terminalInput.Add($oldSameSessionRunning); $terminalInput.Add($newSameSessionComplete)
$terminalRelevant = $activeRules.GetMethod('Relevant', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @($terminalInput, $runtime, $false))
if ($terminalRelevant.Count -ne 0) { throw 'A completed Codex turn did not suppress an older running turn from the same session.' }
$terminalInput.Add($otherSessionRunning)
$parallelRelevant = $activeRules.GetMethod('Relevant', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @($terminalInput, $runtime, $false))
if ($parallelRelevant.Count -ne 1 -or $idField.GetValue($parallelRelevant[0]) -ne 'codex:session-other:running') { throw 'Suppressing same-session stale green also removed a genuinely running parallel Codex session.' }
Write-Host 'PASS newer Codex completion suppresses stale same-session green while preserving other sessions'

$staleTask = New-StateTask 'Codex' 'multi:stale' 'running' ([long]$runtimeNow - 11 * 60 * 1000)
$lastActivityField.SetValue($staleTask, [long]$runtimeNow - 11 * 60 * 1000)
$staleInput = [Activator]::CreateInstance($taskListType); $staleInput.Add($staleTask)
$relevant = $activeRules.GetMethod('Relevant', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @($staleInput, $runtime, $false))
$stalledField = $taskType.GetField('Stalled', [Reflection.BindingFlags]'Instance,NonPublic,Public')
if ($relevant.Count -ne 1 -or -not $stalledField.GetValue($relevant[0]) -or $statusField.GetValue($relevant[0]) -ne 'running') { throw 'Stuck detection must flag stale progress without inventing a red/completed state.' }
$healthRows = $activeRules.GetMethod('Health', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @($runtime, $staleInput, $relevant))
$healthStateField = $healthRows[0].GetType().GetField('State')
if ($healthStateField.GetValue($healthRows[0]) -ne 'stale') { throw 'Data-source health did not separate a stale running task from a trusted green state.' }
Write-Host 'PASS trusted progress freshness and non-terminal stuck indication'

$oldProjectTask = New-StateTask 'Codex' 'codex:old-project:turn' 'running' ([long]$runtimeNow - 25 * 60 * 1000)
$freshProjectTask = New-StateTask 'Codex' 'codex:fresh-project:turn' 'running' ([long]$runtimeNow - 60 * 1000)
$otherProjectTask = New-StateTask 'Codex' 'codex:other-project:turn' 'running' ([long]$runtimeNow - 90 * 1000)
$sessionField.SetValue($oldProjectTask, 'old-project'); $sessionField.SetValue($freshProjectTask, 'fresh-project'); $sessionField.SetValue($otherProjectTask, 'other-project')
$cwdField.SetValue($oldProjectTask, 'D:\agent-beacon'); $cwdField.SetValue($freshProjectTask, 'D:\agent-beacon'); $cwdField.SetValue($otherProjectTask, 'D:\other-project')
$lastActivityField.SetValue($oldProjectTask, [long]$runtimeNow - 25 * 60 * 1000)
$lastActivityField.SetValue($freshProjectTask, [long]$runtimeNow - 60 * 1000)
$lastActivityField.SetValue($otherProjectTask, [long]$runtimeNow - 90 * 1000)
$projectInput = [Activator]::CreateInstance($taskListType); $projectInput.Add($oldProjectTask); $projectInput.Add($freshProjectTask); $projectInput.Add($otherProjectTask)
$suppressedProjectTasks = $activeRules.GetMethod('SuppressSupersededStaleSessions', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @($projectInput, [long]$runtimeNow))
if ($suppressedProjectTasks.Count -ne 2 -or @($suppressedProjectTasks | Where-Object { $idField.GetValue($_) -eq 'codex:old-project:turn' }).Count -ne 0 -or @($suppressedProjectTasks | Where-Object { $idField.GetValue($_) -eq 'codex:other-project:turn' }).Count -ne 1) { throw 'A stale superseded Codex session was not retired without affecting another project.' }
$lastActivityField.SetValue($oldProjectTask, [long]$runtimeNow - 2 * 60 * 1000)
$updatedField.SetValue($oldProjectTask, [long]$runtimeNow - 2 * 60 * 1000)
$recentProjectTasks = $activeRules.GetMethod('SuppressSupersededStaleSessions', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @($projectInput, [long]$runtimeNow))
if ($recentProjectTasks.Count -ne 3) { throw 'Two genuinely active Codex sessions in the same project were incorrectly merged.' }
Write-Host 'PASS stale superseded Codex session cleanup preserves genuinely concurrent work'

$historyTasks = [Activator]::CreateInstance($taskListType)
foreach ($index in 1..3) {
  $historyWait = New-StateTask 'TRAE' ("history:wait:$index") 'attention' ([long]$runtimeNow - $index * 1000)
  $sessionField.SetValue($historyWait, "history-wait-$index"); $historyTasks.Add($historyWait)
}
$historyTasks.Add((New-StateTask 'Codex' 'history:running:1' 'running' ([long]$runtimeNow)))
$healthType = $assembly.GetType('AgentTrafficLightNative.TaskSourceHealth')
$healthListType = [Collections.Generic.List``1].MakeGenericType($healthType)
$historyHealth = [Activator]::CreateInstance($healthListType)
$taskCenterState = $assembly.GetType('AgentTrafficLightNative.TaskCenterState')
$updateCenterArgs = New-Object 'object[]' 2; $updateCenterArgs[0] = $historyTasks; $updateCenterArgs[1] = $historyHealth
$taskCenterState.GetMethod('Update', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, $updateCenterArgs) | Out-Null
$historyFormType = $assembly.GetType('AgentTrafficLightNative.HistoryForm')
$historyForm = [Activator]::CreateInstance($historyFormType, $true)
try { $historyFormType.GetMethod('ReloadOverview', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($historyForm, @()) | Out-Null }
finally { $historyForm.Dispose() }
Write-Host 'PASS status center safely renders three waiting tasks with only one running task'

$progressBridge = Join-Path $work 'opencode-progress.json'
[IO.File]::WriteAllText($progressBridge, '{"source":"OpenCode","id":"opencode:progress-session","sessionId":"progress-session","title":"OpenCode","status":"running","detail":"正在执行","phase":"执行测试","progress":42,"cwd":"D:\\work\\demo","startedAt":' + $runtimeNow + ',"lastActivityAt":' + $runtimeNow + ',"updatedAt":' + $runtimeNow + '}', [Text.Encoding]::UTF8)
$progressTask = $parseBridge.Invoke($bridgeEngine, @([string]$progressBridge, [long]$runtimeNow)) | Select-Object -First 1
if ($progressField.GetValue($progressTask) -ne 42 -or $phaseField.GetValue($progressTask) -ne '执行测试' -or $cwdField.GetValue($progressTask) -notmatch 'demo$') { throw 'Source-reported progress, phase or project path was not preserved.' }
Write-Host 'PASS source-reported progress and project metadata without invented percentages'

$activePath = Join-Path $work 'active-tasks.json'; $oldActivePath = $env:AGENT_BEACON_ACTIVE_TASKS_PATH; $env:AGENT_BEACON_ACTIVE_TASKS_PATH = $activePath
$pendingStore = $assembly.GetType('AgentTrafficLightNative.PendingTaskStore')
$attentionForStore = New-StateTask 'Codex' 'restore:attention' 'attention' ([long]$runtimeNow)
$taskType.GetField('Title', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($attentionForStore, 'SECRET CHAT BODY')
$cwdField.SetValue($attentionForStore, 'D:\private\project')
$storeInput = [Activator]::CreateInstance($taskListType); $storeInput.Add($attentionForStore)
$pendingStore.GetMethod('Merge', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @($storeInput, $runtime, [long]$runtimeNow)) | Out-Null
$activeRaw = Get-Content -LiteralPath $activePath -Encoding UTF8 -Raw
if ($activeRaw -match 'SECRET CHAT BODY|private|project') { throw 'Restart recovery snapshot leaked task title or project path.' }
$pendingStore.GetField('loaded', [Reflection.BindingFlags]'Static,NonPublic').SetValue($null, $false)
$pendingStore.GetField('firstMerge', [Reflection.BindingFlags]'Static,NonPublic').SetValue($null, $true)
$emptyTasks = [Activator]::CreateInstance($taskListType)
$restored = $pendingStore.GetMethod('Merge', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @($emptyTasks, $runtime, [long]($runtimeNow + 5000)))
$restoredField = $taskType.GetField('Restored', [Reflection.BindingFlags]'Instance,NonPublic,Public')
if ($restored.Count -ne 1 -or -not $restoredField.GetValue($restored[0]) -or $statusField.GetValue($restored[0]) -ne 'attention') { throw 'Unresolved attention was not restored after monitor restart.' }
$env:AGENT_BEACON_ACTIVE_TASKS_PATH = $oldActivePath
Write-Host 'PASS privacy-safe unresolved-attention recovery after restart'

$adaptiveType = $assembly.GetType('AgentTrafficLightNative.AdaptiveScanPolicy')
$intervalMethod = $adaptiveType.GetMethod('Interval', [Reflection.BindingFlags]'Static,NonPublic,Public')
$settingsType = $assembly.GetType('AgentTrafficLightNative.SettingsData')
$adaptiveSettings = [Activator]::CreateInstance($settingsType, $true)
$settingsType.GetField('RefreshMs', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($adaptiveSettings, 10000)
$attentionList = [Activator]::CreateInstance($taskListType); $attentionList.Add((New-StateTask 'Codex' 'adaptive:attention' 'attention' $runtimeNow))
$runningList = [Activator]::CreateInstance($taskListType); $runningList.Add((New-StateTask 'Codex' 'adaptive:running' 'running' $runtimeNow))
$emptyList = [Activator]::CreateInstance($taskListType)
if ($intervalMethod.Invoke($null, @($adaptiveSettings, $attentionList)) -ne 800 -or $intervalMethod.Invoke($null, @($adaptiveSettings, $runningList)) -ne 1500 -or $intervalMethod.Invoke($null, @($adaptiveSettings, $emptyList)) -ne 10000) { throw 'Adaptive scan intervals are incorrect.' }
Write-Host 'PASS adaptive active, attention, and idle scan intervals'

$statsPath = Join-Path $work 'usage-stats.json'; $oldStatsPath = $env:AGENT_BEACON_STATS_PATH; $env:AGENT_BEACON_STATS_PATH = $statsPath
$statsType = $assembly.GetType('AgentTrafficLightNative.UsageStatistics')
$statsUpdate = $statsType.GetMethod('Update', [Reflection.BindingFlags]'Static,NonPublic,Public')
$statsSnapshot = $statsType.GetMethod('Snapshot', [Reflection.BindingFlags]'Static,NonPublic,Public')
$statsTask = New-StateTask 'Codex' 'SECRET-TASK-ID' 'running' $runtimeNow
$statsList = [Activator]::CreateInstance($taskListType); $statsList.Add($statsTask)
function Update-Stats($list, [long]$at) { $arguments = New-Object 'object[]' 2; $arguments[0] = $list; $arguments[1] = $at; $statsUpdate.Invoke($null, $arguments) | Out-Null }
Update-Stats $statsList $runtimeNow
Update-Stats $statsList ([long]$runtimeNow + 60000)
$statusField.SetValue($statsTask, 'attention'); Update-Stats $statsList ([long]$runtimeNow + 60000)
Update-Stats $statsList ([long]$runtimeNow + 120000)
$statusField.SetValue($statsTask, 'complete'); Update-Stats $statsList ([long]$runtimeNow + 120000)
$statsType.GetMethod('Flush', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @()) | Out-Null
$snapshot = $statsSnapshot.Invoke($null, @())
if ($snapshot.GetType().GetField('CompletedTasks').GetValue($snapshot) -ne 1 -or $snapshot.GetType().GetField('RunningMs').GetValue($snapshot) -lt 60000 -or $snapshot.GetType().GetField('AttentionMs').GetValue($snapshot) -lt 60000) { throw 'Privacy-safe usage statistics are incorrect.' }
if ((Get-Content -LiteralPath $statsPath -Encoding UTF8 -Raw) -match 'SECRET-TASK-ID') { throw 'Usage statistics leaked a task identifier.' }
$env:AGENT_BEACON_STATS_PATH = $oldStatsPath
Write-Host 'PASS privacy-safe daily completion and duration statistics'

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

$freshness = $integrationType.GetMethod('IsTraeMcpRecent', [Reflection.BindingFlags]'Static,NonPublic,Public')
$freshNow = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
if (-not $freshness.Invoke($null, @([long]($freshNow - 9 * 60 * 1000), [long]$freshNow)) -or $freshness.Invoke($null, @([long]($freshNow - 11 * 60 * 1000), [long]$freshNow))) { throw 'TRAE MCP freshness window is invalid.' }
Write-Host 'PASS TRAE MCP recent/stale connection classification'

$settings = [Activator]::CreateInstance($settingsType, $true)
$notificationType = $assembly.GetType('AgentTrafficLightNative.NotificationPolicy')
$agentEnabled = $notificationType.GetMethod('AgentEnabled', [Reflection.BindingFlags]'Static,NonPublic,Public')
$setAgent = $notificationType.GetMethod('SetAgent', [Reflection.BindingFlags]'Static,NonPublic,Public')
if (-not $agentEnabled.Invoke($null, @($settings, 'TRAE'))) { throw 'TRAE notification should be enabled by default.' }
$setAgent.Invoke($null, @($settings, 'TRAE', $false)) | Out-Null
if ($agentEnabled.Invoke($null, @($settings, 'TRAE'))) { throw 'Per-agent notification toggle did not persist in settings.' }
$settingsType.GetField('QuietHoursEnabled', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($settings, $true)
$settingsType.GetField('QuietStartHour', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($settings, [DateTime]::Now.Hour)
$settingsType.GetField('QuietEndHour', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($settings, [DateTime]::Now.Hour)
$isQuiet = $notificationType.GetMethod('IsQuiet', [Reflection.BindingFlags]'Static,NonPublic,Public')
if (-not $isQuiet.Invoke($null, @($settings, [DateTime]::Now))) { throw 'Quiet-hours notification suppression failed.' }
$policySettings = [Activator]::CreateInstance($settingsType, $true)
$settingsType.GetField('AttentionNotifyDelaySeconds', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($policySettings, 10)
$settingsType.GetField('LongRunningReminderMinutes', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($policySettings, 10)
$policyTask = New-StateTask 'Codex' 'notification:long' 'running' $runtimeNow; $startedField.SetValue($policyTask, [long]$runtimeNow)
$attentionDelay = $notificationType.GetMethod('AttentionDelayMs', [Reflection.BindingFlags]'Static,NonPublic,Public')
$longReminder = $notificationType.GetMethod('ShouldRemindLongRunning', [Reflection.BindingFlags]'Static,NonPublic,Public')
if ($attentionDelay.Invoke($null, @($policySettings)) -ne 10000 -or -not $longReminder.Invoke($null, @($policySettings, $policyTask, ([long]$runtimeNow + 600000)))) { throw 'Delayed attention or long-running notification policy failed.' }
Write-Host 'PASS per-agent, quiet-hours, delayed attention, and long-running notification policy'

$historyType = $assembly.GetType('AgentTrafficLightNative.StateHistory')
$historyPath = Join-Path $work 'state-history.jsonl'; $oldHistoryPath = $env:AGENT_BEACON_HISTORY_PATH; $env:AGENT_BEACON_HISTORY_PATH = $historyPath
$historyTask = New-StateTask 'Codex' 'history:1' 'attention' $freshNow
$taskType.GetField('Title', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($historyTask, 'SECRET CHAT BODY')
$taskType.GetField('Detail', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($historyTask, '等待你的确认')
$taskType.GetField('Evidence', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($historyTask, 'Codex 会话事件')
$historyType.GetMethod('Record', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @($historyTask)) | Out-Null
$historyRaw = Get-Content -LiteralPath $historyPath -Encoding UTF8 -Raw
if ($historyRaw -match 'SECRET CHAT BODY') { throw 'State history leaked a task title/chat body.' }
$recentHistory = $historyType.GetMethod('Recent', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @([int]10))
if ($recentHistory.Count -ne 1) { throw 'State history did not return the recorded transition.' }
$historyType.GetMethod('Clear', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @()) | Out-Null
$env:AGENT_BEACON_HISTORY_PATH = $oldHistoryPath
Write-Host 'PASS privacy-safe bounded state history'

$windowType = $assembly.GetType('AgentTrafficLightNative.AgentWindowActivator')
$matchesWindow = $windowType.GetMethod('Matches', [Reflection.BindingFlags]'Static,NonPublic')
$focusTask = $windowType.GetMethods([Reflection.BindingFlags]'Static,NonPublic,Public') | Where-Object { $_.Name -eq 'Focus' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType -eq $taskType } | Select-Object -First 1
if (-not $matchesWindow.Invoke($null, @('Codex','ChatGPT','')) -or -not $matchesWindow.Invoke($null, @('TRAE','TRAE','')) -or -not $focusTask) { throw 'Agent window/task matching rules failed.' }
$updateType = $assembly.GetType('AgentTrafficLightNative.UpdateService')
$safeTarget = $updateType.GetMethod('IsSafeUpdateTarget', [Reflection.BindingFlags]'Static,NonPublic,Public')
if (-not $safeTarget.Invoke($null, @('D:\Agent-Beacon-1.6.0.exe')) -or $safeTarget.Invoke($null, @('C:\Windows\System32\notepad.exe'))) { throw 'Automatic update target validation failed.' }
$parseRelease = $updateType.GetMethod('ParseRelease', [Reflection.BindingFlags]'Static,NonPublic,Public')
$releaseJson = '{"tag_name":"v9.9.9","html_url":"https://github.com/LAUFLO/agent-beacon/releases/tag/v9.9.9","assets":[{"name":"Agent-Beacon-9.9.9.exe","browser_download_url":"https://example.invalid/Agent-Beacon-9.9.9.exe","digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}'
$releaseInfo = $parseRelease.Invoke($null, @($releaseJson))
$updateInfoType = $assembly.GetType('AgentTrafficLightNative.UpdateInfo')
if ($updateInfoType.GetField('Version').GetValue($releaseInfo) -ne '9.9.9' -or $updateInfoType.GetField('Sha256').GetValue($releaseInfo).Length -ne 64) { throw 'GitHub release asset/digest parsing failed.' }
$appInfoType = $assembly.GetType('AgentTrafficLightNative.AppInfo')
if ($appInfoType.GetField('Version', [Reflection.BindingFlags]'Static,NonPublic,Public').GetRawConstantValue() -ne '1.6.0') { throw 'Application version metadata is not 1.6.0.' }
Write-Host 'PASS click-to-focus matching, safe GitHub updater and centralized version metadata'

$integrationType = $assembly.GetType('AgentTrafficLightNative.Integration')
$health = $integrationType.GetMethod('HealthSummary', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @())
$repair = $integrationType.GetMethod('RepairConfiguredIntegrations', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @())
if ($health -notmatch 'TRAE:|Claude Code:|OpenCode:' -or $repair -notmatch '集成健康检查完成') { throw 'Integration health check and automatic repair are incomplete.' }
Write-Host 'PASS configured integration health check and automatic repair'

$env:AGENT_BEACON_TRAE_MCP_DIR = $oldUpgradeDir
$env:AGENT_BEACON_DISMISSED_TASKS_PATH = $oldDismissedPath
$env:AGENT_TRAFFIC_LIGHT_HOME = $oldHome
Write-Host 'PASS TRAE MCP stable-path upgrade and hash refresh'
