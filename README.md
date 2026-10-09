# Nivalis Mods

Mods for **Nivalis Nights**, maintained by [foobarto](https://github.com/foobarto).

| Mod | Purpose | Version |
| --- | --- | --- |
| [Boat Decor](mods/NivalisBoatDecor) | Decorate the docked player boat with owned furniture and native placement, pickup and storage controls. | 0.1.3 |
| [Farm First Manager Supply](mods/NivalisFarmSupply) | Managers harvest ripe crops from player-owned farms before buying ingredients, then replant. | 0.4.0 |

Farm First Manager Supply is also published on
[Nexus Mods](https://www.nexusmods.com/nivalisnights/mods/34).

## Install a mod

Download the install ZIP from [Releases](https://github.com/foobarto/nivalis-mods/releases).
Each mod has its own requirements and configuration instructions. Game and loader
assemblies are required locally and are not included in this repository or its releases.

Farm First Manager Supply requires **BepInEx 6 Unity IL2CPP for Windows x64**
(tested with be.788). Follow the
[official IL2CPP installation guide](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html)
and use the [BepInEx bleeding-edge downloads](https://builds.bepinex.dev/projects/bepinex_be).
Launch the game once to generate bindings, close it, and extract the mod ZIP into
the game folder. Under Proton, the Windows loader also needs the native `winhttp`
override; see the mod's instructions.

## Build from source

Install the **.NET 10 SDK**. The plugin targets the loader's .NET 6 runtime and
references the assemblies in your own game installation after BepInEx has generated
its bindings. From the repository root:

```sh
dotnet build mods/NivalisFarmSupply/NivalisFarmSupply.csproj -c Release -p:GamePath="/path/to/Nivalis Nights"
```

The output is `mods/NivalisFarmSupply/bin/Release/net6.0/NivalisFarmSupply.dll`.
Copy that DLL into `BepInEx/plugins/NivalisFarmSupply/` while the game is closed.
No game binaries are replaced.

## Checks

These checks run without the game or BepInEx:

```sh
dotnet build mods/NivalisFarmSupply/tests/Tests.csproj -c Release -m:1 --disable-build-servers
dotnet run --project mods/NivalisFarmSupply/tests/Tests.csproj -c Release --no-build --no-restore
```

The checks cover manager demand, verified item transfers, receipt snapshots,
missing API members, replant recovery, and shared produce/planting identities.
Plugin compilation and passing checks are separate from live gameplay and
save/reload verification.

## Releases

Mods are versioned independently. Tags use `<mod-name>-v<version>`, for example
`farm-first-manager-supply-v0.2.2`. Release titles include the mod name and version,
and each release contains only that mod's install package. There is no collection-wide
version. Shared-helper changes are tested and released with each affected mod.

## Layout

- `mods/`: each mod's source, documentation, and checks.
- `shared/managed/`: reusable C# helpers linked into individual mods.

For behavior, configuration, compatibility, and validation limits, read the
[Farm First Manager Supply documentation](mods/NivalisFarmSupply/README.md) and
[Boat Decor documentation](mods/NivalisBoatDecor/README.md).

## License

Original mod source and shared helpers are available under the [MIT License](LICENSE).
Game and third-party loader components retain their own licenses.
