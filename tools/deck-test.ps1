<#
.SYNOPSIS
  Tests HotReload on a Steam Deck (Linux + Proton) from Windows over SSH: deploys HotReload and the IL2CPP test fixtures
  into the Deck's copy of an IL2CPP game, triggers reloads, reads the log. The fixtures are built against the Windows copy
  of the same game (GameDir in Local.props).

.DESCRIPTION
  One-time preparation on the Deck (Desktop Mode, Konsole):
      passwd                                   # give the deck user a password, if it has none
      sudo systemctl enable --now sshd         # start the SSH server, also after reboots
      ip -4 addr show wlan0                    # the Deck's address, e.g. 192.168.1.50
  One-time on Windows (asks for the Deck password once, then works without it):
      pwsh tools/deck-test.ps1 Setup -Deck deck@192.168.1.50 -GameFolder MyGame

  Then, with the game installed on the Deck with MelonLoader (launch option WINEDLLOVERRIDES="version=n,b" %command%):
      pwsh tools/deck-test.ps1 Check  -Deck deck@192.168.1.50 -GameFolder MyGame      # finds the game, shows MelonLoader / HotReload state
      pwsh tools/deck-test.ps1 Deploy -Deck deck@192.168.1.50 -GameFolder MyGame      # HotReload + fixtures + test bundles (game closed)
      (start the game on the Deck)
      pwsh tools/deck-test.ps1 Log    -Deck deck@192.168.1.50 -GameFolder MyGame      # HotReload / fixture lines of the current log
      pwsh tools/deck-test.ps1 Reload -Deck deck@192.168.1.50 -GameFolder MyGame -Version 1.0.2          # new HRTestBase build
      pwsh tools/deck-test.ps1 Reload -Deck deck@192.168.1.50 -GameFolder MyGame -Version 1.0.3 -WithLibrary
      pwsh tools/deck-test.ps1 Cleanup -Deck deck@192.168.1.50 -GameFolder MyGame     # removes the fixtures (game closed)

  Builds run on Windows against the Windows copy of the game (Local.props GameDir): the Deck runs the same Windows
  build through Proton, so the binaries are identical.
#>
param(
    [Parameter(Mandatory, Position = 0)] [ValidateSet("Setup", "Check", "Deploy", "Log", "Reload", "Cleanup")] [string] $Action,
    [Parameter(Mandatory)] [string] $Deck,                      # ssh target, e.g. deck@192.168.1.50
    [Parameter(Mandatory)] [string] $GameFolder,                # steamapps/common/<GameFolder> on the Deck
    [string] $GameDir,                                          # full path on the Deck; found automatically when empty
    [string] $Version = "1.0.1",
    [string] $Bundles,                                          # Deploy: a folder with HRTestBundleKept.bundle / HRTestBundleDropped.bundle (optional)
    [switch] $WithLibrary
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$sshOpts = @("-o", "BatchMode=yes", "-o", "ConnectTimeout=8")

function Remote([string] $cmd) {
    $out = & ssh @sshOpts $Deck $cmd 2>&1
    if ($LASTEXITCODE -ne 0) { throw "ssh failed ($LASTEXITCODE): $out" }
    return $out
}
function Copy-To([string[]] $files, [string] $remoteDir) {
    Remote "mkdir -p '$remoteDir'" | Out-Null
    & scp @sshOpts -q @files "${Deck}:$remoteDir/"
    if ($LASTEXITCODE -ne 0) { throw "scp to $remoteDir failed" }
}
function Find-Game {
    if ($GameDir) { return $GameDir }
    # Internal library first, then SD cards / extra libraries.
    $found = Remote "for d in ~/.local/share/Steam/steamapps/common /run/media/*/steamapps/common /run/media/*/*/steamapps/common; do [ -d `"`$d/$GameFolder`" ] && echo `"`$d/$GameFolder`" && break; done; true"
    if (-not $found) { throw "$GameFolder not found on the Deck; pass -GameDir" }
    return ($found | Select-Object -First 1).Trim()
}
function Build([string] $project, [string[]] $props) {
    dotnet build (Join-Path $repo $project) -c Release -p:DeployToGame=false --nologo -v quiet @props | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed: $project" }
}

switch ($Action) {
    "Setup" {
        $key = Join-Path $env:USERPROFILE ".ssh\id_ed25519"
        # -N with an empty argument: in pwsh 7.3+ '""' would become a two-character passphrase.
        if (-not (Test-Path $key)) { ssh-keygen -q -t ed25519 -N ([string]::Empty) -f $key; "created $key" }
        "Installing the public key on $Deck (enter the Deck password when asked)..."
        Get-Content "$key.pub" | ssh $Deck "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"
        Remote "echo key login works on `$(uname -n)"
    }
    "Check" {
        $g = Find-Game
        "game: $g"
        Remote "cd '$g' && if [ -d MelonLoader ]; then echo 'MelonLoader: installed'; grep -m3 -E 'MelonLoader v|Game Name|HotReload\] MelonLoader' MelonLoader/Latest.log 2>/dev/null; ls Plugins Mods UserLibs 2>/dev/null; else echo 'MelonLoader: NOT installed in this copy of the game'; fi; true"
    }
    "Deploy" {
        $g = Find-Game
        Build "HotReload.csproj" @()
        Build "tests/HRTestPlugin/HRTestPlugin.csproj" @("-p:Version=1.0.0")
        Build "tests/HRTestDependent/HRTestDependent.csproj" @("-p:Version=1.0.0")   # builds HRTestLib and HRTestBase too
        Copy-To @("$repo/bin/Release/HotReload.dll", "$repo/bin/Release/HotReload.pdb", "$repo/tests/HRTestPlugin/bin/Release/HRTestPlugin.dll") "$g/Plugins"
        Copy-To @("$repo/tests/HRTestLib/bin/Release/HRTestLib.dll") "$g/UserLibs"
        Copy-To @("$repo/tests/HRTestBase/bin/Release/HRTestBase.dll", "$repo/tests/HRTestBase/bin/Release/HRTestBase.pdb") "$g/Mods"
        Copy-To @("$repo/tests/HRTestDependent/bin/Release/HRTestDependent.dll") "$g/Mods/HRTestSub"
        Remote "echo '{ `"note`": `"HotReload test fixture`" }' > '$g/Mods/HRTestSub/manifest.json'" | Out-Null
        if ($Bundles) { Copy-To (Get-ChildItem $Bundles -Filter "HRTestBundle*.bundle").FullName "$g/UserData" }
        "deployed to $g; start the game on the Deck, then: deck-test.ps1 Log / Reload"
    }
    "Log" {
        $g = Find-Game
        Remote "grep -E '\[HotReload\]|\[HRTest|bundle loaded|Probe\(\)|ERROR' '$g/MelonLoader/Latest.log' | grep -v -E 'coroutine tick|timer from|behaviour Update' | tail -n 60"
    }
    "Reload" {
        $g = Find-Game
        $props = @("-p:Version=$Version"); if (-not $WithLibrary) { $props += "-p:BuildProjectReferences=false" }
        Build "tests/HRTestBase/HRTestBase.csproj" $props
        # Write to a temporary name, then rename: the watcher sees one complete file.
        Remote "mkdir -p /tmp/hrtest" | Out-Null
        $files = @("$repo/tests/HRTestBase/bin/Release/HRTestBase.dll", "$repo/tests/HRTestBase/bin/Release/HRTestBase.pdb")
        if ($WithLibrary) { $files += "$repo/tests/HRTestLib/bin/Release/HRTestLib.dll" }
        & scp @sshOpts -q @files "${Deck}:/tmp/hrtest/"
        $mv = "mv -f /tmp/hrtest/HRTestBase.pdb '$g/Mods/' && mv -f /tmp/hrtest/HRTestBase.dll '$g/Mods/'"
        if ($WithLibrary) { $mv += " && mv -f /tmp/hrtest/HRTestLib.dll '$g/UserLibs/'" }
        Remote $mv | Out-Null
        "HRTestBase $Version copied; waiting for the reload..."
        Start-Sleep -Seconds 8
        Remote "grep -E '\[HotReload\]|bundle loaded|Probe\(\)|restored|alive|ERROR' '$g/MelonLoader/Latest.log' | tail -n 25"
    }
    "Cleanup" {
        $g = Find-Game
        Remote "cd '$g' && rm -rf Mods/HRTestBase.* Mods/HRTestSub Plugins/HRTestPlugin.* UserLibs/HRTestLib.* UserData/HRTestBundle*.bundle; ls Plugins Mods UserLibs"
        "Note: MelonPreferences.cfg keeps the fixtures' HRTestBase / [HRTest Unrelated Name] entries; remove them by hand if wanted."
    }
}
