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

# Existing world with the players' map markers, over the terrain the game generated
dotnet TerraPrety.Preview/bin/Release/net10.0/terraprety-preview.dll \
    --save ~/.config/VintagestoryData/Saves/myworld.vcdbs --map-markers --layers composite,actual -o out/myworld.png

# Zoom in on an in-game coordinate
... --center 1200,-3400 --size 8192 --scale 4
```

Run with `--help` for all options. Coordinates are in-game coordinates, i.e. relative to the world spawn. The mod config defaults to `VintagestoryData/ModConfig/TerraPrety.json` when it exists; `--mod-assets` can point at a released mod zip instead of this repo's assets.

### Other mods

Other mods can change TerraPrety's map. The main way is through story structures that force land: every story structure with a `requireLandform` adds a forced continent point to TerraPrety's ocean map, which reshapes the coastline for thousands of blocks around it. BetterRuins, for example, adds 15 story structures and, when TerraPrety is installed, gives all of them `requireLandform: ultraflats`.

So the tool loads the installed mods the way the game does: `VintagestoryData/Mods` by default (`--mods <dir>` for another folder, `--mods none` to leave them out), skipping mods disabled in the game's mod manager. Their `worldgen/storystructures.json` files are merged with the game's, `compatibility/<modid>/` folders are remapped, and all JSON patches are applied, honoring `dependsOn`, `condition` and `side`. Any TerraPrety in the mods folder is replaced by the one from `--mod-assets`.

### Layers

| Layer       | Shows                                                                                         |
|-------------|-----------------------------------------------------------------------------------------------|
| `composite` | Estimated terrain: water depth, elevation colors, snow and desert tint from climate, hillshade |
| `height`    | TerraPrety's landform height map (0..1), oceans darkened by oceanicity                        |
| `landforms` | Landform per tile (one color per landform code)                                               |
| `ocean`     | Ocean map (oceanicity)                                                                        |
| `mountains` | Mountain range opacity (red) and uplift (yellow) over the height map                          |
| `climate`   | Temperature (blue to red) and rainfall (saturation)                                           |
| `actual`    | With `--save`: the terrain the game generated, in the `composite` palette, with the estimate dimmed where nothing was generated yet. Compare with `composite`. |

The white dot is the world spawn, magenta dots are story structures. With `--map-markers` (needs `--save`), the waypoints players put on their in-game map are drawn on every layer, using the game's map icons (including icons added by mods) and the marker colors, with the same drop shadow as in the game. Icons are 8 to 16 px by default, growing with the image size; `--marker-size <px>` sets the size explicitly. Temporary markers are left out. An unknown icon is drawn as a circle, as in the game.

## Accuracy

Run with `--save <world> --verify` to compare against what the game actually generated in that world. It regenerates every stored map region and compares it tile by tile, lists the regions that differ, and then compares the estimated surface height against the stored terrain height maps.

- The **ocean, landform, landform height and climate maps** are the mod's own code and match the game exactly, as long as the mods, the mod config and the story structure locations are the same as when the region was generated. The game never regenerates a region, so a world played under several setups is a patchwork. Example: on a 1.22 world with TerraPrety 7.11.1 and BetterRuins 0.6.4, 939 of 986 regions are identical. The other 47 were generated before BetterRuins' story structures forced land, and they match exactly with `--mods none`.
- The **surface height** in `composite` is an estimate: each landform's 50% terrain threshold, lowered by oceanicity. The game computes the real height from 3D noise, blending neighbouring landforms. On the same world: median error 4 blocks, 90th percentile 23 blocks, 88% land/water agreement. Coastlines, seas and large lakes are where the game puts them. Small lakes and ponds are not. Use the `actual` layer to see the difference.
- Story structures without a config (their mod was removed) force nothing, in the game and in the preview; the tool lists them. With only `--seed`, story structure locations are computed the way the game does for a new world. With `--save`, they are read from the savegame, which also picks up structures the game moved, e.g. with `/wgen story setpos`.
- Rivers aren't drawn (they are disabled in the mod as well).

## How it works

- `FakeServerApi` answers the few `ICoreServerAPI` members the map layers use (world size, assets) and throws on anything else, so a new dependency in the mod fails loudly.
- `AssetSource` reads game assets from the install, TerraPrety's from the repo or a zip, and the other mods' from their zips or folders. It applies every mod's JSON patches as the game's `ModJsonPatchLoader` does.
- The mod's noise classes keep RNG state in statics and fields, so they can't run on several threads at once. `ParallelSampler` gives each worker thread its own copy of the tool's assembly in a separate `AssemblyLoadContext`. The output is byte-identical to a single-threaded run (`--threads 1`).
