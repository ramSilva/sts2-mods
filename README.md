# sts2-mods

Slay the Spire 2 mods by **killermelga**.

## Mods

| Mod | Summary | Docs | Nexus |
| --- | --- | --- | --- |
| [EnemyIntentTracker](EnemyIntentTracker/) | Hover any enemy to see a prediction of what they will do on their next turn. | [README](EnemyIntentTracker/README.md) | _(coming soon)_ |
| [DamageTracker](DamageTracker/) | Overlay that tracks damage and block per card / relic / effect across combat, act, and run scopes. | _(not yet documented)_ | _(not published)_ |

## Shared library

`common/` is the `Sts2Mods.Common` helper library (logger, Harmony bootstrap, reflection helpers). It is **source-included** into each mod's `.csproj` via `<Compile Include="../common/*.cs">` rather than referenced as a separate assembly — Slay the Spire 2 loads each mod in its own `AssemblyLoadContext` which does not resolve sibling DLLs, so per-mod source inlining is the only way that works. Each mod ships its own copy of the resulting types.

## Building from source

Requirements:
- .NET 9 SDK
- Slay the Spire 2 installed via Steam at the standard install path. The build auto-detects it on macOS (arm64 / x86_64) and Windows (`C:\Program Files (x86)\Steam\...`). If your install is elsewhere, pass `-p:Sts2DataDir=/path/to/data_dir` to `dotnet build`.

Build and install both mods to your local game:

    ./build.sh

Or per-mod:

    ./EnemyIntentTracker/build.sh
    ./DamageTracker/build.sh

Each mod also has a `package.sh` that writes a release zip into `<mod>/build/dist/`.

## License

MIT — see [LICENSE](LICENSE).
