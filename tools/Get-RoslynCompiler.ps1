$ErrorActionPreference = 'Stop'
$version = '4.10.0'
$expectedHash = 'EB18B8B9FE5021AEAA1D7C7C1B5041269B171CF1151703E14E5429F0053F7CFB'
$cache = Join-Path $PSScriptRoot ".cache\roslyn-$version"
$package = Join-Path $cache 'package.zip'
$compiler = Join-Path $cache 'tasks\net472\csc.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
  New-Item -ItemType Directory -Force -Path $cache | Out-Null
  if (-not (Test-Path -LiteralPath $package)) {
    $url = "https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/$version/microsoft.net.compilers.toolset.$version.nupkg"
    Invoke-WebRequest -Uri $url -OutFile $package
  }
  $actualHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
  if ($actualHash -ne $expectedHash) { throw "Roslyn package hash mismatch: $actualHash" }
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [IO.Compression.ZipFile]::ExtractToDirectory($package, $cache)
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Roslyn compiler was not found after package extraction.' }
$compiler
