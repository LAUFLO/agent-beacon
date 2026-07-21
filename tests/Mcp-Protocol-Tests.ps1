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
  $tools = Invoke-Mcp '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
  if ($tools.result.tools[0].name -ne 'agent_beacon_report_state') { throw 'TRAE MCP status tool was not advertised.' }

  $states = @()
  foreach ($spec in @(
    @('running','3'), @('waiting','4'), @('running','5'), @('completed','6')
  )) {
    $payload = '{"jsonrpc":"2.0","id":' + $spec[1] + ',"method":"tools/call","params":{"name":"agent_beacon_report_state","arguments":{"state":"' + $spec[0] + '","session_id":"protocol-session","task_id":"protocol-task"}}}'
    $reply = Invoke-Mcp $payload
    if ($reply.result.isError) { throw "TRAE MCP tool failed for state $($spec[0])." }
    $eventFile = Get-ChildItem -LiteralPath (Join-Path $homePath '.agent-traffic-light\events') -Filter 'trae-mcp-*.json' | Select-Object -First 1 -ExpandProperty FullName
    $event = Get-Content -LiteralPath $eventFile -Encoding UTF8 -Raw | ConvertFrom-Json
    $states += $event.status
  }
  if (($states -join ',') -ne 'running,attention,running,complete') { throw "TRAE MCP protocol lifecycle failed: $($states -join ' -> ')" }
  Write-Host 'PASS TRAE MCP stdio handshake and green -> yellow -> green -> red lifecycle'

  $process.StandardInput.Close()
  if (-not $process.WaitForExit(5000)) { $process.Kill(); throw 'TRAE MCP helper did not exit after stdin closed.' }
} finally {
  if ($process -and -not $process.HasExited) { $process.Kill() }
  $env:AGENT_TRAFFIC_LIGHT_HOME = $oldHome
  if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
