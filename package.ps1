# Builds the release zips into dist/. File names carry no version, so
# https://github.com/<owner>/MelonLoader-HotReload/releases/latest/download/<file> always points at the newest release:
#   MelonLoader-HotReload.zip   Plugins/HotReload.dll (+pdb), README.md, LICENSE. One DLL for Mono and IL2CPP games.
#   HotReloadCheck.zip          the compatibility scanner (needs the .NET 8 runtime; run: dotnet HotReloadCheck.dll)
#   SHA256SUMS.txt              SHA256 of every file inside the zips and the .NET SDK used, for comparing a rebuild
#                               (Release builds are reproducible: docs/building.md, "Reproducing a release")
# Needs no game: everything builds from NuGet.
# Usage: pwsh ./package.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.HotReloadVersion | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <HotReloadVersion> in Directory.Build.props" }

$dist = Join-Path $PSScriptRoot "dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null
$sums = New-Object System.Collections.Generic.List[string]

# SHA256 of each file in a staging folder, as "<hash>  <zip>/<path>" (zips carry timestamps; the files inside do not).
function Add-Sums($folder, $zipName) {
    Get-ChildItem $folder -Recurse -File | Sort-Object FullName | ForEach-Object {
        $rel = $_.FullName.Substring((Resolve-Path $folder).Path.Length + 1).Replace('\', '/')
        $sums.Add(("{0}  {1}/{2}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $zipName, $rel))
    }
}

dotnet build HotReload.csproj -c Release -p:DeployToGame=false --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed: HotReload.csproj" }

$stage = Join-Path $dist "stage"
New-Item -ItemType Directory (Join-Path $stage "Plugins") -Force | Out-Null
Copy-Item "bin/Release/HotReload.dll", "bin/Release/HotReload.pdb" (Join-Path $stage "Plugins")
Copy-Item README.md, LICENSE $stage
$zip = Join-Path $dist "MelonLoader-HotReload.zip"
Add-Sums $stage "MelonLoader-HotReload.zip"
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Remove-Item $stage -Recurse -Force
Write-Host $zip

dotnet publish checker -c Release -o (Join-Path $dist "stage-check") --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed: checker" }
Copy-Item LICENSE (Join-Path $dist "stage-check")
$checkZip = Join-Path $dist "HotReloadCheck.zip"
Add-Sums (Join-Path $dist "stage-check") "HotReloadCheck.zip"
Compress-Archive -Path (Join-Path $dist "stage-check/*") -DestinationPath $checkZip
Remove-Item (Join-Path $dist "stage-check") -Recurse -Force
Write-Host $checkZip

$sdk = (dotnet --version).Trim()
$commit = (git rev-parse HEAD 2>$null)
$header = "# HotReload $version" + $(if ($commit) { ", commit $commit" } else { "" }) + ", .NET SDK $sdk"
Set-Content (Join-Path $dist "SHA256SUMS.txt") (@($header) + $sums) -Encoding ascii

Write-Host "Version $version"
Get-ChildItem $dist | ForEach-Object { "{0,-40} {1,8:N0} KB  SHA256 {2}" -f $_.Name, ($_.Length / 1KB), (Get-FileHash $_.FullName).Hash.Substring(0, 16) }
