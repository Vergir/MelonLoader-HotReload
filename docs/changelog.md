# Changelog

## 1.1.0

* Unity errors and exceptions that appear after a reload are copied into the MelonLoader log, once per reload with a
  repeat count and the reload they followed. Before, exceptions thrown inside game code reached only `Player.log`.
  Setting `EchoUnityErrors` (`AfterReload`, `Always`, `Off`); left to MelonLoader when its `capture_player_logs` is on.
* `HotReload.LoadingLate` AppDomain flag: `"reload"` or `"new"` while HotReload loads a melon into a running game, so a
  mod can skip once-per-launch work without referencing HotReload.
* Guide: a plain copy into `Mods/` is enough; moving the old DLL aside is only needed for plugins and libraries.

## 1.0.0

Initial release.
