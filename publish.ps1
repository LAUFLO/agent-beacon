param([switch]$NoWait)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

function Invoke-Git {
  $gitArguments = @($args)
  & git @gitArguments
  if ($LASTEXITCODE -ne 0) { throw "git $($gitArguments -join ' ') failed with exit code $LASTEXITCODE." }
}

$repoRootText = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRootText)) { throw 'Unable to resolve the Agent Beacon repository.' }
$repoRoot = [IO.Path]::GetFullPath($repoRootText).TrimEnd([char[]]'\/')
$scriptRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([char[]]'\/')
if (-not [string]::Equals($repoRoot, $scriptRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Run publish.ps1 from the Agent Beacon repository.' }

$versionMatch = Select-String -LiteralPath (Join-Path $PSScriptRoot 'AppInfo.cs') -Pattern 'public const string Version = "([0-9]+\.[0-9]+\.[0-9]+)"' | Select-Object -First 1
if (-not $versionMatch) { throw 'App version was not found in AppInfo.cs.' }
$version = $versionMatch.Matches[0].Groups[1].Value
$tag = "v$version"
$notes = Join-Path $PSScriptRoot "releases\$tag.md"
if (-not (Test-Path -LiteralPath $notes)) { throw "Missing release notes: $notes" }

$remoteTag = & git ls-remote --exit-code --tags origin "refs/tags/$tag"
if ($LASTEXITCODE -eq 0 -or -not [string]::IsNullOrWhiteSpace(($remoteTag -join ''))) { throw "Tag $tag already exists. Increase AppInfo.Version and add releases/$tag.md before publishing." }
if ($LASTEXITCODE -ne 2) { throw 'Unable to check remote tags.' }

$authorName = [string](& git config --get user.name)
$authorNameExit = $LASTEXITCODE
if ($authorNameExit -ne 0 -or [string]::IsNullOrWhiteSpace($authorName)) { Invoke-Git config user.name 'LAUFLO' }
$authorEmail = [string](& git config --get user.email)
$authorEmailExit = $LASTEXITCODE
if ($authorEmailExit -ne 0 -or [string]::IsNullOrWhiteSpace($authorEmail)) { Invoke-Git config user.email '49666043+LAUFLO@users.noreply.github.com' }

Invoke-Git add -A
$stagedFiles = @(& git diff --cached --name-only)
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect staged files.' }
$sensitiveFiles = @($stagedFiles | Where-Object { $_ -match '(?i)(^|/)\.env($|\.)|\.(pfx|p12|pem|key)$|(^|/)(credentials|secrets?)(\.|/|$)' })
if ($sensitiveFiles.Count -gt 0) {
  & git restore --staged -- @sensitiveFiles
  throw "Sensitive files were not published: $($sensitiveFiles -join ', ')"
}

& git diff --cached --quiet
if ($LASTEXITCODE -eq 1) { Invoke-Git commit -m "release: $tag" }
elseif ($LASTEXITCODE -ne 0) { throw 'Unable to inspect staged changes.' }

$remaining = @(& git status --porcelain)
if ($LASTEXITCODE -ne 0 -or $remaining.Count -gt 0) { throw 'The working tree is not clean after creating the release commit.' }

$sourceBranch = (& git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceBranch)) { throw 'Publishing from a detached HEAD is not supported.' }
$sourceSha = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve the source commit.' }

Invoke-Git fetch origin main
if ($sourceBranch -eq 'main') {
  & git rebase origin/main
  if ($LASTEXITCODE -ne 0) { & git rebase --abort; throw 'Local main could not be rebased onto origin/main. Resolve the conflict and publish again.' }
} else {
  & git show-ref --verify --quiet refs/heads/main
  if ($LASTEXITCODE -eq 0) { Invoke-Git switch main }
  elseif ($LASTEXITCODE -eq 1) { Invoke-Git switch -c main --track origin/main }
  else { throw 'Unable to inspect the local main branch.' }

  & git merge --ff-only origin/main
  if ($LASTEXITCODE -ne 0) { Invoke-Git switch $sourceBranch; throw 'Local main has unpublished divergent commits. Reconcile main before publishing.' }
  & git merge --no-ff $sourceSha -m "merge: $sourceBranch for $tag"
  if ($LASTEXITCODE -ne 0) { & git merge --abort; Invoke-Git switch $sourceBranch; throw 'The source branch conflicts with main. Resolve the conflict and publish again.' }
}

Invoke-Git push origin main
$publishedSha = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve the published main commit.' }

& gh auth status
if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI is not authenticated. Run gh auth login once and publish again.' }
$dispatched = $false
for ($attempt = 0; $attempt -lt 6 -and -not $dispatched; $attempt++) {
  & gh workflow run release.yml --ref main -f "commit_sha=$publishedSha"
  $dispatched = $LASTEXITCODE -eq 0
  if (-not $dispatched) { Start-Sleep -Seconds 2 }
}
if (-not $dispatched) { throw 'Unable to start the GitHub release workflow.' }

$run = $null
for ($attempt = 0; $attempt -lt 20 -and -not $run; $attempt++) {
  Start-Sleep -Seconds 2
  $json = & gh run list --workflow release.yml --branch main --event workflow_dispatch --limit 10 --json databaseId,headSha,status,url
  if ($LASTEXITCODE -ne 0) { throw 'Unable to locate the GitHub release workflow run.' }
  $run = @($json | ConvertFrom-Json | Where-Object { $_.headSha -eq $publishedSha }) | Select-Object -First 1
}
if (-not $run) { throw 'The release workflow was dispatched but its run could not be located.' }

Write-Host "Release workflow: $($run.url)"
if (-not $NoWait) {
  & gh run watch $run.databaseId --exit-status
  if ($LASTEXITCODE -ne 0) { throw "Release workflow failed: $($run.url)" }
  Write-Host "Published Agent Beacon $tag from main commit $publishedSha"
}
