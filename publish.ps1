param(
  [string]$CommitSha = '',
  [switch]$NoWait,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$repoRootText = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRootText)) {
  throw 'Unable to resolve the Agent Beacon repository.'
}

$repoRoot = [IO.Path]::GetFullPath($repoRootText).TrimEnd([char[]]'\/')
$scriptRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([char[]]'\/')
if (-not [string]::Equals($repoRoot, $scriptRoot, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'Run publish.ps1 from the Agent Beacon repository.'
}

$workingChanges = @(& git status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
  throw 'Unable to inspect the working tree.'
}
if ($workingChanges.Count -gt 0) {
  throw 'The working tree is not clean. Commit and deliver the intended changes through a pull request before publishing.'
}

$currentBranch = (& git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($currentBranch)) {
  throw 'Publishing from a detached HEAD is not supported.'
}
if ($currentBranch -ne 'main') {
  throw "The current branch is '$currentBranch'. Switch to main before publishing."
}

& gh auth status
if ($LASTEXITCODE -ne 0) {
  throw 'GitHub CLI is not authenticated. Run gh auth login once and publish again.'
}

$repoJson = & gh repo view --json nameWithOwner,defaultBranchRef
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($repoJson -join ''))) {
  throw 'Unable to resolve the GitHub repository.'
}
$repoInfo = $repoJson | ConvertFrom-Json
$repoName = [string]$repoInfo.nameWithOwner
$defaultBranch = [string]$repoInfo.defaultBranchRef.name
if ([string]::IsNullOrWhiteSpace($repoName)) {
  throw 'The GitHub repository name is missing.'
}
if ($defaultBranch -ne 'main') {
  throw "The GitHub default branch is '$defaultBranch', but this release workflow requires main."
}

$localSha = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $localSha -notmatch '^[0-9a-fA-F]{40}$') {
  throw 'Unable to resolve the local main commit.'
}

$remoteMainSha = (& gh api "repos/$repoName/commits/main" --jq .sha).Trim()
if ($LASTEXITCODE -ne 0 -or $remoteMainSha -notmatch '^[0-9a-fA-F]{40}$') {
  throw 'Unable to resolve the remote main commit.'
}
if (-not [string]::Equals($localSha, $remoteMainSha, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'Local main is not aligned with remote main. Run git pull --ff-only origin main and verify the branch before publishing.'
}

if (-not [string]::IsNullOrWhiteSpace($CommitSha)) {
  $requestedSha = $CommitSha.Trim()
  if ($requestedSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'CommitSha must be a complete 40-character Git commit SHA.'
  }
  if (-not [string]::Equals($requestedSha, $remoteMainSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'CommitSha does not match the current remote main commit.'
  }
}
$publishedSha = $remoteMainSha.ToLowerInvariant()

$encodedAppInfo = @(& gh api "repos/$repoName/contents/src/Core/AppInfo.cs?ref=$publishedSha" --jq .content)
if ($LASTEXITCODE -ne 0 -or $encodedAppInfo.Count -eq 0) {
  throw 'Unable to read AppInfo.cs from the remote main commit.'
}
$base64Text = (($encodedAppInfo -join '') -replace '\s', '')
try {
  $appInfoText = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($base64Text))
} catch {
  throw 'Unable to decode AppInfo.cs from the remote main commit.'
}

$versionMatch = [regex]::Match($appInfoText, 'public const string Version = "([0-9]+\.[0-9]+\.[0-9]+)"')
if (-not $versionMatch.Success) {
  throw 'App version was not found in the remote AppInfo.cs.'
}
$version = $versionMatch.Groups[1].Value
$tag = "v$version"

& gh api "repos/$repoName/git/ref/tags/$tag" --silent 2>$null
$tagLookupExit = $LASTEXITCODE
if ($tagLookupExit -eq 0) {
  throw "Tag $tag already exists. Increase AppInfo.Version and add releases/$tag.md through a pull request before publishing."
}
if ($tagLookupExit -ne 1) {
  throw "Unable to check whether tag $tag exists."
}

& gh api "repos/$repoName/contents/releases/$tag.md`?ref=$publishedSha" --silent 2>$null
if ($LASTEXITCODE -ne 0) {
  throw "Remote main does not contain releases/$tag.md."
}

Write-Host "Repository: $repoName"
Write-Host "Release: Agent Beacon $tag"
Write-Host "Remote main: $publishedSha"

if ($DryRun) {
  Write-Host 'Dry run complete. No workflow was dispatched and no repository state was changed.'
  return
}

$dispatchStartedAt = [DateTimeOffset]::UtcNow.AddSeconds(-5)
& gh workflow run release.yml --repo $repoName --ref main -f "commit_sha=$publishedSha"
if ($LASTEXITCODE -ne 0) {
  throw 'Unable to start the GitHub release workflow. Check GitHub Actions before retrying to avoid a duplicate dispatch.'
}

$run = $null
for ($attempt = 0; $attempt -lt 20 -and -not $run; $attempt++) {
  Start-Sleep -Seconds 2
  $runJson = & gh run list --repo $repoName --workflow release.yml --branch main --event workflow_dispatch --limit 10 --json databaseId,headSha,status,url,createdAt
  if ($LASTEXITCODE -ne 0) {
    throw 'Unable to locate the GitHub release workflow run.'
  }
  $matchingRuns = @(
    $runJson |
      ConvertFrom-Json |
      Where-Object {
        $_.headSha -eq $publishedSha -and
        [DateTimeOffset]$_.createdAt -ge $dispatchStartedAt
      } |
      Sort-Object -Property createdAt -Descending
  )
  $run = $matchingRuns | Select-Object -First 1
}
if (-not $run) {
  throw 'The release workflow was dispatched but its run could not be located.'
}

Write-Host "Release workflow: $($run.url)"
if ($NoWait) {
  Write-Host 'The workflow is running asynchronously. No local repository state was changed.'
  return
}

& gh run watch $run.databaseId --repo $repoName --exit-status
if ($LASTEXITCODE -ne 0) {
  throw "Release workflow failed: $($run.url)"
}

$releaseJson = & gh release view $tag --repo $repoName --json tagName,targetCommitish,url,assets
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($releaseJson -join ''))) {
  throw "The workflow succeeded, but release $tag could not be verified."
}
$release = $releaseJson | ConvertFrom-Json
$assetNames = @($release.assets | ForEach-Object { [string]$_.name })
$expectedAssets = @(
  "Agent-Beacon-$version.exe",
  "Agent-Beacon-Setup-$version.exe",
  "Agent-Beacon-Portable-$version.zip"
)
$missingAssets = @($expectedAssets | Where-Object { $_ -notin $assetNames })
if ($missingAssets.Count -gt 0) {
  throw "Release $tag is missing assets: $($missingAssets -join ', ')"
}

Write-Host "Published Agent Beacon $tag from remote main commit $publishedSha"
Write-Host "Release: $($release.url)"
