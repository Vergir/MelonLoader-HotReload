<#
.SYNOPSIS
  Checks that every MelonLoader / HarmonyX / Mono.Cecil / Il2CppInterop member a built HotReload.dll uses exists in each
  supported MelonLoader release (net6 folder for IL2CPP games, net35 / net472 for Mono games). Downloads the releases
  from GitHub into a cache folder on first use. Exit code 1 when anything is missing.
.EXAMPLE
  pwsh tools/apicompat.ps1                          # builds nothing; checks bin/Release/HotReload.dll
  pwsh tools/apicompat.ps1 -Cache D:\ml-releases
#>
param(
    [string] $Dll,
    [string] $Cache,
    # First and last 0.6 release and every 0.7 release. Shadow copy needs 0.7.1+ (MelonFolderHandler._modDirs).
    [string[]] $Versions = @("v0.6.0", "v0.6.6", "v0.7.0", "v0.7.1", "v0.7.2", "v0.7.3")
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Dll) { $Dll = Join-Path $repo "bin/Release/HotReload.dll" }
if (-not (Test-Path $Dll)) { throw "$Dll not found; build first (dotnet build -c Release)" }
if (-not $Cache) {
    $base = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } elseif ($IsWindows -or $env:OS -eq "Windows_NT") { $env:LOCALAPPDATA } else { Join-Path $HOME ".cache" }
    $Cache = Join-Path $base "hotreload-apicompat"
}
New-Item -ItemType Directory -Force $Cache | Out-Null

$dirs = @()
foreach ($v in $Versions) {
    $target = Join-Path $Cache $v
    if (-not (Test-Path (Join-Path $target "net6"))) {
        $zip = Join-Path $Cache "$v.zip"
        if (-not (Test-Path $zip)) {
            Write-Host "downloading MelonLoader $v"
            Invoke-WebRequest "https://github.com/LavaGang/MelonLoader/releases/download/$v/MelonLoader.x64.zip" -OutFile $zip
        }
        $tmp = Join-Path $Cache "tmp-$v"
        if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
        Expand-Archive $zip -DestinationPath $tmp
        New-Item -ItemType Directory -Force $target | Out-Null
        foreach ($f in "net6", "net35", "net472") {
            $src = Join-Path $tmp "MelonLoader/$f"
            if (Test-Path $src) { Move-Item $src (Join-Path $target $f) }
        }
        Remove-Item $tmp -Recurse -Force
    }
    foreach ($f in "net6", "net35", "net472") {
        $d = Join-Path $target $f
        if (Test-Path $d) { $dirs += $d }
    }
}

dotnet run --project (Join-Path $repo "tools/ApiCompat") -c Release -- $Dll @dirs --optional _modDirs
exit $LASTEXITCODE
