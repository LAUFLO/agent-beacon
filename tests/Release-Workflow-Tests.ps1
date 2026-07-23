$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$workflowPath = Join-Path $root '.github\workflows\release.yml'
$buildPath = Join-Path $root 'build.ps1'
$publishPath = Join-Path $root 'publish.ps1'
$workflow = Get-Content -LiteralPath $workflowPath -Encoding UTF8 -Raw
$build = Get-Content -LiteralPath $buildPath -Encoding UTF8 -Raw
$publish = Get-Content -LiteralPath $publishPath -Encoding UTF8 -Raw

foreach ($required in @('workflow_dispatch:','commit_sha:','ref: main','Replay-State-Tests.ps1','Mcp-Protocol-Tests.ps1','integration-event-tests.mjs','Release-Workflow-Tests.ps1','Ui-Style-Tests.ps1','Reproducible-Build-Tests.ps1','actions/checkout@v6','actions/upload-artifact@v6','Prepare release notes with package hash','Get-FileHash','steps.notes.outputs.file','Create tag, release, and upload EXE','--target','$env:GITHUB_SHA')) {
  if (-not $workflow.Contains($required)) { throw "Release workflow is missing: $required" }
}
if (-not $workflow.Contains("Join-Path `$PWD 'src\Core\AppInfo.cs'")) { throw 'Release workflow does not read the reorganized AppInfo.cs path.' }
if ($workflow -notmatch '\$tagCheckExitCode = \$LASTEXITCODE' -or $workflow -notmatch '\$global:LASTEXITCODE = 0') { throw 'Release workflow does not safely handle the expected missing-tag exit code.' }
if ($workflow -match "tags: \['v\*'\]" -or $workflow -match '(?m)^  push:$') { throw 'Release workflow must be dispatched once by publish.ps1, not recursively by branch or tag pushes.' }
if (-not $workflow.Contains('@(''release'',''create'',$env:RELEASE_TAG)') -or $workflow -notmatch '& gh @arguments') { throw 'Release workflow does not create the tag and GitHub release.' }
if ($workflow -match 'CERTIFICATE|signtool|RequireSignature') { throw 'Code-signing requirements were not fully removed.' }
foreach ($required in @(
  'git status --porcelain=v1 --untracked-files=all'
  'git branch --show-current'
  'gh repo view --json nameWithOwner,defaultBranchRef'
  'repos/$repoName/commits/main'
  'CommitSha does not match the current remote main commit.'
  'gh workflow run release.yml'
  'gh run watch'
  'gh release view'
  '[switch]$DryRun'
  'Dry run complete. No workflow was dispatched and no repository state was changed.'
)) {
  if (-not $publish.Contains($required)) { throw "Safe publish dispatcher is missing: $required" }
}
if ($publish -notmatch '\[IO\.Path\]::GetFullPath' -or $publish -notmatch '\[StringComparison\]::OrdinalIgnoreCase') { throw 'Local publish script does not normalize Windows repository paths.' }
$gitMutationPattern = '(?im)^\s*(?:&\s+)?git\s+(add|commit|switch|checkout|merge|rebase|push|tag|config|restore)\b'
if ($publish -match $gitMutationPattern -or $publish -match '\bInvoke-Git\b') { throw 'Publish dispatcher must not mutate local or remote Git state.' }
[scriptblock]::Create($publish) | Out-Null
if ($build -notmatch 'src\\Core\\AppInfo\.cs' -or $build -notmatch 'Select-String.+\$appInfoPath' -or $build -notmatch 'Get-FileHash.+SHA256' -or $build -notmatch 'Agent-Beacon-\$version\.sha256' -or $build -notmatch '/deterministic\+' -or $build -notmatch 'Agent-Beacon-Setup-\$version\.exe' -or $build -notmatch 'Agent-Beacon-Portable-\$version\.zip') { throw 'Build script does not produce deterministic portable and installer packages.' }
foreach ($module in @(
  'src\Core\AppInfo.cs'
  'src\Core\AgentModels.cs'
  'src\Core\DiagnosticsHub.cs'
  'src\Core\Util.cs'
  'src\Monitoring\MonitorEngine.cs'
  'src\Monitoring\AgentProcesses.cs'
  'src\Monitoring\AgentStateRules.cs'
  'src\UI\PixelTheme.cs'
  'src\UI\MainForm.cs'
  'src\Features\TaskCenter.cs'
  'src\Integrations\Integrations.cs'
  'src\Services\UpdateService.cs'
  'src\Program.cs'
  'installer\InstallerStub.cs'
  'tools\Get-AppSourceFiles.ps1'
)) {
  if (-not (Test-Path -LiteralPath (Join-Path $root $module))) { throw "Modular source file is missing: $module" }
}
if (@(Get-ChildItem -LiteralPath $root -File -Filter '*.cs').Count -ne 0) { throw 'C# source files remain in the repository root.' }
Write-Host 'PASS safe remote-main release dispatcher and automatic GitHub tag/release workflow'
