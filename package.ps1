# Builds the release zips into dist/. File names carry no version, so
# https://github.com/<owner>/MelonLoader_HotReload/releases/latest/download/<file> always points at the newest release:
#   MelonLoader_HotReload-IL2CPP.zip   Plugins/HotReload.dll (+pdb), README.md, LICENSE
#   MelonLoader_HotReload-Mono.zip     same, Mono build
#   HotReloadCheck.zip                 the compatibility scanner (needs the .NET 8 runtime)
# Needs GameDir (IL2CPP game) and MonoGameDir (Mono game) with MelonLoader, see Local.props.example.
# Usage: pwsh ./package.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$version = ([xml](Get-Content HotReload.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in HotReload.csproj" }
$monoVersion = ([xml](Get-Content mono/HotReload.Mono.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($monoVersion -ne $version) { throw "Version mismatch: HotReload.csproj $version, mono/HotReload.Mono.csproj $monoVersion" }

$dist = Join-Path $PSScriptRoot "dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null

function Build($project) {
    dotnet build $project -c Release -p:DeployToGame=false --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}

function Zip($name, $binDir) {
    $stage = Join-Path $dist "stage-$name"
    New-Item -ItemType Directory (Join-Path $stage "Plugins") -Force | Out-Null
    Copy-Item (Join-Path $binDir "HotReload.dll"), (Join-Path $binDir "HotReload.pdb") (Join-Path $stage "Plugins")
    Copy-Item README.md, LICENSE $stage
    $zip = Join-Path $dist "MelonLoader_HotReload-$name.zip"
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
    Remove-Item $stage -Recurse -Force
    Write-Host "$zip"
}

Build "HotReload.csproj"
Zip "IL2CPP" "bin/Release"
Build "mono/HotReload.Mono.csproj"
Zip "Mono" "mono/bin/Release"

dotnet publish checker -c Release -o (Join-Path $dist "stage-check") --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed: checker" }
Copy-Item LICENSE (Join-Path $dist "stage-check")
$checkZip = Join-Path $dist "HotReloadCheck.zip"
Compress-Archive -Path (Join-Path $dist "stage-check/*") -DestinationPath $checkZip
Remove-Item (Join-Path $dist "stage-check") -Recurse -Force
Write-Host "$checkZip"

Write-Host "Version $version"
Get-ChildItem $dist | ForEach-Object { "{0,-50} {1,8:N0} KB  SHA256 {2}" -f $_.Name, ($_.Length / 1KB), (Get-FileHash $_.FullName).Hash.Substring(0, 16) }
