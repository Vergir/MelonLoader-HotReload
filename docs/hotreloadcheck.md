# HotReloadCheck

`HotReloadCheck` reads compiled mod DLLs, with no source and no game, and reports whether HotReload can reload them and
what the author would have to add. It only reads metadata: which APIs a mod calls, which types and fields it defines,
which callbacks it overrides. It never loads or runs a mod.

Download `HotReloadCheck.zip` from the [releases](https://github.com/vergir/MelonLoader-HotReload/releases), extract it,
and run it with the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer):

```bash
dotnet HotReloadCheck.dll <dll-or-folder>... --md report.md --csv report.csv
```

The zip holds only managed code (no `.exe` launcher). To build it from source instead:

```bash
dotnet publish checker -c Release -o out
dotnet out/HotReloadCheck.dll <dll-or-folder>...
```

Folders are searched recursively. `--libs` also lists DLLs without `[MelonInfo]`.

| Verdict | Meaning |
|---|---|
| READY | Nothing found that HotReload cannot clean up. |
| REVIEW | Uses something HotReload cannot clean up, but has `OnDeinitializeMelon`: check that it undoes it. |
| NEEDS CLEANUP | Uses something HotReload cannot clean up and has no `OnDeinitializeMelon`. |
| UNSUPPORTED | Built for MelonLoader 0.5 (Unhollower); it does not load on 0.6 or newer anyway. |
| LIBRARY | No `[MelonInfo]`: not a mod. |

Each finding says whether it is **handled** (HotReload cleans it up; listed for information) or needs **cleanup** by the
mod, with the evidence (the API calls it found) and what to do. Cleanup findings today: threads the mod starts, hooks
outside Harmony that are not kept in a field, and (in IL2CPP games) AssetBundles that are not kept in a field.

A static scan cannot tell whether `OnDeinitializeMelon` undoes everything, and misses behaviour hidden behind reflection
or obfuscation. REVIEW means "read the cleanup code or try it". The problems in
[the guide for mod authors](writing-reloadable-mods.md) that come from game logic (UI attached from events, objects left in
game lists) cannot be seen in metadata at all.
