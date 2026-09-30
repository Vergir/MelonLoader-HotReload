# Changelog

## 1.1.1

* HotReloadCheck ships without the `HotReloadCheck.exe` launcher; run it with `dotnet HotReloadCheck.dll`. The
  unsigned launcher stub from the .NET SDK was flagged by antivirus scanners.
* Reproducible builds: a Release build of a tag with the same .NET SDK gives byte-identical DLLs. The release notes list
  the SDK, the commit and the SHA256 of every file in the zips ([how to check](building.md#reproducing-a-release)).
* HotReload itself is unchanged apart from the build settings.

## 1.1.0

* Unity errors and exceptions that appear after a reload are copied into the MelonLoader log, once per reload with a
  repeat count and the reload they followed. Before, exceptions thrown inside game code reached only `Player.log`.
  Setting `EchoUnityErrors` (`AfterReload`, `Always`, `Off`); left to MelonLoader when its `capture_player_logs` is on.
* `HotReload.LoadingLate` AppDomain flag: `"reload"` or `"new"` while HotReload loads a melon into a running game, so a
  mod can skip once-per-launch work without referencing HotReload.
* Guide: a plain copy into `Mods/` is enough; moving the old DLL aside is only needed for plugins and libraries.

## 1.0.0

Initial release.
