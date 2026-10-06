# Building

HotReload compiles against the `LavaGang.MelonLoader` NuGet package and reaches Unity through reflection, so it builds
without a game, on Windows or Linux, with the .NET SDK 10 (the tests and the checker run on .NET 8):

```bash
dotnet build -c Release          # bin/Release/HotReload.dll, for Mono and IL2CPP games
dotnet test tests/HotReload.Tests
pwsh tools/apicompat.ps1         # the built DLL against MelonLoader 0.6.0-0.7.3 (downloads them once)
pwsh ./package.ps1               # release zips into dist/
```

`MelonLoader-HotReload.sln` opens HotReload, the checker, the tests and ApiCompat in Visual Studio or Rider.

## One DLL for both runtimes

HotReload targets net472 and compiles against the **net35** assemblies of the MelonLoader, HarmonyX and Mono.Cecil
packages: MelonLoader runs its net35 build in Mono games, which ships Mono.Cecil 0.10.4, and .NET 6 in IL2CPP games
accepts the newer copies for those references. The packages are fetched with `PackageDownload` and referenced by path,
because a normal package reference would pick their net472 group. Everything that differs between the runtimes is decided
at runtime.

## Deploying to a game

To copy each build into games' `Plugins/` folders, copy `Local.props.example` to `Local.props` (git-ignored) and set
`GameDir` (an IL2CPP game) and/or `MonoGameDir` (a Mono game), or use the `MELONLOADER_GAME_DIR` /
`MELONLOADER_MONO_GAME_DIR` environment variables or `-p:GameDir=...`. `-p:DeployToGame=false` skips the copy.

A running game keeps its HotReload DLL open. The build moves it aside to `*.hotreload-old` (Windows allows renaming a
loaded DLL) and copies the new one in; HotReload deletes the old copies at the next start. The running game keeps the old
build until it restarts: HotReload cannot reload itself.

## Version and releases

The version and the repository URL live in `Directory.Build.props` (`HotReloadVersion`, `HotReloadRepository`).
HotReload's `[MelonInfo]`, the checker's reports and `package.ps1` all read them.

GitHub Actions (`.github/workflows/ci.yml`) build, test and run the API check on every push, on Windows and Linux.
Pushing a tag `vX.Y.Z` that matches `HotReloadVersion` runs `.github/workflows/release.yml`: the same checks, then the
release with `MelonLoader-HotReload.zip` and `HotReloadCheck.zip`, and the notes from that version's section of
[changelog.md](changelog.md). The asset names carry no version, so
`https://github.com/vergir/MelonLoader-HotReload/releases/latest/download/MelonLoader-HotReload.zip` always points at the
newest release.

## Reproducing a release

Release builds are reproducible: the same commit built with the same .NET SDK gives byte-identical DLLs on any machine
and in any folder. Every release's notes list the SDK, the commit and the SHA256 of each file inside the zips, so a
release can be checked against the source:

1. Install the .NET SDK version named in the release notes (any .NET 10 SDK builds it, but a different compiler
   version can produce slightly different bytes). Check with `dotnet --version`.
2. Clone the release's tag and build the zips:

   ```bash
   git clone --branch v1.2.0 https://github.com/vergir/MelonLoader-HotReload
   cd MelonLoader-HotReload
   pwsh ./package.ps1
   ```

   Or without PowerShell: `dotnet build HotReload.csproj -c Release -p:DeployToGame=false` (output in `bin/Release/`)
   and `dotnet publish checker -c Release -o out` (output in `out/`).
3. Compare `dist/SHA256SUMS.txt` with the list in the release notes, or hash the files yourself
   (`sha256sum`, or `Get-FileHash` in PowerShell).

What makes it reproducible: deterministic compilation, `ContinuousIntegrationBuild` recording the checkout path as `/_/`
(`Directory.Build.props`), and `.gitattributes` giving every checkout LF line endings, which the compiler records
through the PDB checksums. The zips themselves differ between builds (they carry file times); compare the files inside.

What is in the release zips:

| File | What it is |
|---|---|
| `Plugins/HotReload.dll`, `HotReload.pdb` | The plugin (managed code) and its debug symbols. |
| `HotReloadCheck.dll`, `HotReloadCheck.pdb` | The checker (managed .NET 8 code) and its debug symbols. |
| `HotReloadCheck.deps.json`, `HotReloadCheck.runtimeconfig.json` | .NET's dependency and runtime settings for the checker. |
| `README.md`, `LICENSE` | Text. |

Neither zip contains native code. Both DLLs decompile with any .NET decompiler (ILSpy, dnSpy).

Testing in games: [testing.md](testing.md).
