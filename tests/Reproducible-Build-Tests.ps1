$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ('agent-beacon-repro-' + [Guid]::NewGuid().ToString('N'))
$first = Join-Path $work 'first'
$second = Join-Path $work 'second'

try {
  & (Join-Path $root 'build.ps1') -OutputDirectory $first | Out-Null
  & (Join-Path $root 'build.ps1') -OutputDirectory $second | Out-Null
  $versionMatch = Select-String -LiteralPath (Join-Path $root 'src\Core\AppInfo.cs') -Pattern 'public const string Version = "([0-9]+\.[0-9]+\.[0-9]+)"' | Select-Object -First 1
  if (-not $versionMatch) { throw 'Version not found.' }
  $version = $versionMatch.Matches[0].Groups[1].Value
  foreach ($name in @("Agent-Beacon-$version.exe", "Agent-Beacon-Setup-$version.exe", "Agent-Beacon-Portable-$version.zip")) {
    $left = (Get-FileHash -LiteralPath (Join-Path $first $name) -Algorithm SHA256).Hash
    $right = (Get-FileHash -LiteralPath (Join-Path $second $name) -Algorithm SHA256).Hash
    if ($left -ne $right) { throw "$name is not reproducible: $left != $right" }
  }
  $setup = Join-Path $first "Agent-Beacon-Setup-$version.exe"
  if ((Get-Item -LiteralPath $setup).VersionInfo.ProductVersion.Split('+')[0] -ne $version) { throw 'Installer version metadata is incorrect.' }
  $setupAssembly = [Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes($setup))
  if ($setupAssembly.GetManifestResourceNames() -notcontains 'agent-beacon.exe') { throw 'Installer does not embed the desktop application.' }
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $portable = Join-Path $first "Agent-Beacon-Portable-$version.zip"
  $archive = [IO.Compression.ZipFile]::OpenRead($portable)
  try {
    $entries = @($archive.Entries | Select-Object -ExpandProperty Name)
    if ($entries -notcontains "Agent-Beacon-$version.exe" -or $entries -notcontains "Agent-Beacon-$version.sha256" -or $entries -notcontains 'README.md') { throw 'Portable package contents are incomplete.' }
  } finally { $archive.Dispose() }
  Write-Host 'PASS deterministic application and installer builds'
} finally {
  if (Test-Path -LiteralPath $work) {
    $resolved = [IO.Path]::GetFullPath($work)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
  }
}
