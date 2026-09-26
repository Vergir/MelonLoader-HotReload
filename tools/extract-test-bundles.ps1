<#
.SYNOPSIS
  Writes the two AssetBundles the HRTestBase fixture loads (HRTestBundleKept.bundle, HRTestBundleDropped.bundle) into a
  folder, usually a game's UserData. They are UniverseLib's embedded UI bundles (UniverseLib is MIT-licensed; nothing is
  committed here): any bundle works, as long as the game itself has not loaded it.
.EXAMPLE
  pwsh tools/extract-test-bundles.ps1 -UserData "C:\Games\PEAK-MelonTest\UserData"
#>
param(
    [Parameter(Mandatory)] [string] $UserData,
    [string] $UniverseLib   # a UniverseLib DLL; default: the one in the UserLibs of GameDir / MonoGameDir from Local.props
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
if (-not $UniverseLib) {
    $props = Join-Path $repo "Local.props"
    $dirs = @()
    if (Test-Path $props) { $pg = ([xml](Get-Content $props)).Project.PropertyGroup; $dirs = @($pg.GameDir, $pg.MonoGameDir) | Where-Object { $_ } }
    $UniverseLib = $dirs | ForEach-Object { Get-ChildItem (Join-Path $_ "UserLibs") -Filter "UniverseLib*.dll" -ErrorAction SilentlyContinue } |
                   Select-Object -First 1 -ExpandProperty FullName
    if (-not $UniverseLib) { throw "No UniverseLib*.dll in the UserLibs of Local.props games; pass -UniverseLib" }
}
$cecil = Get-ChildItem "$env:USERPROFILE\.nuget\packages\mono.cecil" -Recurse -Filter Mono.Cecil.dll | Where-Object FullName -match "netstandard2.0" | Select-Object -First 1
if (-not $cecil) { throw "Mono.Cecil (netstandard2.0) not in the NuGet cache; run 'dotnet test tests/HotReload.Tests' once" }
Add-Type -Path $cecil.FullName -ErrorAction SilentlyContinue
$map = @{ "UniverseLib.Resources.modern.bundle" = "HRTestBundleKept.bundle"; "UniverseLib.Resources.legacy.bundle" = "HRTestBundleDropped.bundle" }
New-Item -ItemType Directory -Force $UserData | Out-Null
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($UniverseLib)
try {
    foreach ($r in $asm.MainModule.Resources) {
        if (-not $map.ContainsKey($r.Name)) { continue }
        $out = Join-Path $UserData $map[$r.Name]
        [IO.File]::WriteAllBytes($out, $r.GetResourceData())
        "{0} -> {1}" -f $r.Name, $out
    }
} finally { $asm.Dispose() }
