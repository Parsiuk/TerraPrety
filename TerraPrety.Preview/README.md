# TerraPrety map preview

A command line tool that renders a PNG preview of the world TerraPrety would generate for a given seed and world settings, without starting the game.

It compiles the mod's own map layer code from this repo (`MapLayerOceansSmooth`, `LandformHeightNoise`, ...) and runs it against the game's assemblies, set up the same way `GenMaps.initWorldGen` is once TerraPrety's Harmony patches are applied. That includes spawn land, story structure locations and their forced landforms.

## Build

Requires the .NET 10 SDK and a Vintage Story install (the same one the mod builds against).

```sh
VINTAGE_STORY=~/Apps/VintageStory dotnet build -c Release TerraPrety.Preview
```

The game folder is remembered at build time; it can be overridden with `--game <dir>` or `VINTAGE_STORY` when running.

## Usage

```sh
# New world, from the settings on the world creation screen
dotnet TerraPrety.Preview/bin/Release/net10.0/terraprety-preview.dll \
    --seed 1934320731 --world-height 448 --landform-scale 300% --polar-equator-distance 100000 \
    -o preview.png

# Existing world: read seed and settings from the savegame (opened read-only), render every layer
dotnet TerraPrety.Preview/bin/Release/net10.0/terraprety-preview.dll \
    --save ~/.config/VintagestoryData/Saves/myworld.vcdbs --layers all -o out/myworld.png

# Zoom in on an in-game coordinate
... --center 1200,-3400 --size 8192 --scale 4
```

Run with `--help` for all options. Coordinates are in-game coordinates, i.e. relative to the world spawn. The mod config defaults to `VintagestoryData/ModConfig/TerraPrety.json` when it exists; `--mod-assets` can point at a released mod zip instead of this repo's assets.

### Layers

| Layer       | Shows                                                                                         |
|-------------|-----------------------------------------------------------------------------------------------|
| `composite` | Estimated terrain: water depth, elevation colors, snow and desert tint from climate, hillshade |
| `height`    | TerraPrety's landform height map (0..1), oceans darkened by oceanicity                        |
| `landforms` | Landform per tile (one color per landform code)                                               |
| `ocean`     | Ocean map (oceanicity)                                                                        |
| `mountains` | Mountain range opacity (red) and uplift (yellow) over the height map                          |
| `climate`   | Temperature (blue to red) and rainfall (saturation)                                           |

The white dot is the world spawn, magenta dots are story structures.

## Accuracy

Run with `--save <world> --verify` to compare against what the game actually generated in that world. It regenerates the stored map regions and compares them tile by tile, then compares the estimated surface height against the stored terrain height maps.

- The **ocean, landform, landform height and climate maps** are the mod's own code and match the game exactly. On a 1.22.7 world with TerraPrety 7.11.1: 100% of tiles identical.
- The **surface height** in `composite` is an estimate: each landform's 50% terrain threshold, lowered by oceanicity. The game computes the real height from 3D noise. On the same world: median error 3 blocks, 90th percentile 16 blocks, 87% land/water agreement.
- Story structures from other mods: with `--save`, their positions are read from the savegame and marked, but their configs aren't loaded, so any landform they force isn't applied. With only `--seed` they aren't known at all. Vanilla story structures are handled in both modes.
- Rivers aren't drawn (they are disabled in the mod as well).

## How it works

- `FakeServerApi` answers the few `ICoreServerAPI` members the map layers use (world size, assets) and throws on anything else, so a new dependency in the mod fails loudly.
- `AssetSource` reads game assets from the install and mod assets from the repo or a zip, applying the mod's JSON patches.
- The mod's noise classes keep RNG state in statics and fields, so they can't run on several threads at once. `ParallelSampler` gives each worker thread its own copy of the tool's assembly in a separate `AssemblyLoadContext`. The output is byte-identical to a single-threaded run (`--threads 1`).
