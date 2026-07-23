$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ('agent-beacon-mcp-' + [Guid]::NewGuid().ToString('N'))
$homePath = Join-Path $work 'home'
$exe = Join-Path $work 'Agent-Beacon-TRAE-MCP.exe'
$oldHome = $env:AGENT_TRAFFIC_LIGHT_HOME
New-Item -ItemType Directory -Force -Path $homePath | Out-Null

try {
  & 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /optimize+ /platform:anycpu /out:$exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll (Join-Path $root 'TraeMcpHost.cs')
  if ($LASTEXITCODE -ne 0) { throw 'TRAE MCP helper compilation failed.' }

  $start = New-Object System.Diagnostics.ProcessStartInfo
  $start.FileName = $exe; $start.UseShellExecute = $false
  $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
  $env:AGENT_TRAFFIC_LIGHT_HOME = $homePath
  $process = [Diagnostics.Process]::Start($start)

  function Invoke-Mcp([string]$message) {
    $process.StandardInput.WriteLine($message); $process.StandardInput.Flush()
    $line = $process.StandardOutput.ReadLine()
    if ([string]::IsNullOrWhiteSpace($line)) { throw 'TRAE MCP returned no JSON-RPC response.' }
    return $line | ConvertFrom-Json
  }

  $initialize = Invoke-Mcp '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}'
  if ($initialize.result.serverInfo.name -ne 'agent-beacon-trae') { throw 'TRAE MCP initialize response is invalid.' }
  if ($initialize.result.serverInfo.version -ne '1.6.0') { throw 'TRAE MCP version is not 1.6.0.' }
  $healthPath = Join-Path $homePath '.agent-traffic-light\events\trae-mcp-health.json'
  $health = Get-Content -LiteralPath $healthPath -Encoding UTF8 -Raw | ConvertFrom-Json
  if (-not $health.connected -or $health.helperVersion -ne '1.6.0') { throw 'TRAE MCP live health handshake was not recorded.' }
  $tools = Invoke-Mcp '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
  if ($tools.result.tools[0].name -ne 'agent_beacon_report_state') { throw 'TRAE MCP status tool was not advertised.' }
  $propertyNames = @($tools.result.tools[0].inputSchema.properties.PSObject.Properties.Name | Sort-Object)
  if (($propertyNames -join ',') -ne 'session_id,state') { throw "TRAE MCP schema was not minimized: $($propertyNames -join ',')" }
  if (($tools.result.tools[0].inputSchema.required -join ',') -ne 'state,session_id') { throw 'TRAE MCP schema does not require state and session_id.' }

  $states = @()
  foreach ($spec in @(
    @('running','3'), @('waiting','4'), @('running','5'), @('completed','6')
  )) {
    $payload = '{"jsonrpc":"2.0","id":' + $spec[1] + ',"method":"tools/call","params":{"name":"agent_beacon_report_state","arguments":{"state":"' + $spec[0] + '","session_id":"protocol-task"}}}'
    $reply = Invoke-Mcp $payload
    if ($reply.result.isError) { throw "TRAE MCP tool failed for state $($spec[0])." }
    $replyText = [string]$reply.result.content[0].text
    if ($spec[0] -eq 'waiting') {
      if (-not $replyText.StartsWith('ok;') -or $replyText -notmatch 'running') { throw 'TRAE MCP waiting response did not reinforce running-after-reply.' }
    } elseif ($spec[0] -eq 'completed') {
      if (-not $replyText.StartsWith('ok;') -or $replyText.Length -le 4) { throw 'TRAE MCP completed response did not reinforce immediate final output.' }
    } elseif ($replyText -ne 'ok') { throw 'TRAE MCP normal response was not minimized to ok.' }
    $eventFile = Get-ChildItem -LiteralPath (Join-Path $homePath '.agent-traffic-light\events') -Filter 'trae-mcp-*.json' | Select-Object -First 1 -ExpandProperty FullName
    $event = Get-Content -LiteralPath $eventFile -Encoding UTF8 -Raw | ConvertFrom-Json
    $states += $event.status
  }
  if (($states -join ',') -ne 'running,attention,running,complete') { throw "TRAE MCP protocol lifecycle failed: $($states -join ' -> ')" }
  Write-Host 'PASS minimized TRAE MCP schema, prompt/final timing reminders and green -> yellow -> green -> red lifecycle'

  $process.StandardInput.Close()
  if (-not $process.WaitForExit(5000)) { $process.Kill(); throw 'TRAE MCP helper did not exit after stdin closed.' }
  $health = Get-Content -LiteralPath $healthPath -Encoding UTF8 -Raw | ConvertFrom-Json
  if ($health.connected -or $health.activity -ne 'disconnect') { throw 'TRAE MCP disconnect health was not recorded.' }
  Write-Host 'PASS TRAE MCP health handshake and disconnect freshness record'
} finally {
  if ($process -and -not $process.HasExited) { $process.Kill() }
  $env:AGENT_TRAFFIC_LIGHT_HOME = $oldHome
  if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
