$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$workflowPath = Join-Path $root '.github\workflows\release.yml'
$buildPath = Join-Path $root 'build.ps1'
$publishPath = Join-Path $root 'publish.ps1'
$workflow = Get-Content -LiteralPath $workflowPath -Encoding UTF8 -Raw
$build = Get-Content -LiteralPath $buildPath -Encoding UTF8 -Raw
$publish = Get-Content -LiteralPath $publishPath -Encoding UTF8 -Raw

foreach ($required in @('workflow_dispatch:','commit_sha:','ref: main','Replay-State-Tests.ps1','Mcp-Protocol-Tests.ps1','integration-event-tests.mjs','Release-Workflow-Tests.ps1','Ui-Style-Tests.ps1','actions/upload-artifact@v4','Prepare release notes with package hash','Get-FileHash','steps.notes.outputs.file','Create tag, release, and upload EXE','--target','$env:GITHUB_SHA')) {
  if (-not $workflow.Contains($required)) { throw "Release workflow is missing: $required" }
}
if ($workflow -match "tags: \['v\*'\]" -or $workflow -match '(?m)^  push:$') { throw 'Release workflow must be dispatched once by publish.ps1, not recursively by branch or tag pushes.' }
if (-not $workflow.Contains('@(''release'',''create'',$env:RELEASE_TAG)') -or $workflow -notmatch '& gh @arguments') { throw 'Release workflow does not create the tag and GitHub release.' }
if ($workflow -match 'CERTIFICATE|signtool|RequireSignature') { throw 'Code-signing requirements were not fully removed.' }
foreach ($required in @('Invoke-Git add -A','git merge --no-ff','Invoke-Git push origin main','gh workflow run release.yml','gh run watch')) {
  if (-not $publish.Contains($required)) { throw "Local publish script is missing: $required" }
}
if ($publish -notmatch 'git config --get user\.name' -or $publish -notmatch 'users\.noreply\.github\.com') { throw 'Local publish script does not prevent missing Git author identity.' }
if ($publish -notmatch '\[IO\.Path\]::GetFullPath' -or $publish -notmatch '\[StringComparison\]::OrdinalIgnoreCase') { throw 'Local publish script does not normalize Windows repository paths.' }
if ($publish -notmatch '\$gitArguments = @\(\$args\)' -or $publish -match 'ValueFromRemainingArguments') { throw 'Local publish script does not safely forward git flags such as -A.' }
if ($build -notmatch 'Select-String.+AppInfo\.cs' -or $build -notmatch 'Get-FileHash.+SHA256' -or $build -notmatch 'Agent-Beacon-\$version\.sha256') { throw 'Build script does not derive the version and emit SHA-256.' }
foreach ($module in @('AppInfo.cs','PixelTheme.cs','StateHistory.cs','DesktopFeatures.cs','UpdateService.cs','AgentUi.cs','Integrations.cs')) {
  if (-not (Test-Path -LiteralPath (Join-Path $root $module))) { throw "Modular source file is missing: $module" }
}
Write-Host 'PASS one-command local merge and automatic GitHub tag/release workflow'
