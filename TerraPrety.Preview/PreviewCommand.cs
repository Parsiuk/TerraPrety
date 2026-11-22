using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TerraPrety.Preview;

public static class PreviewCommand
{
    public static int Execute(Options options)
    {
        var modAssets = options.ModAssets ?? GameAssemblies.BuildTimeModAssetsDir();
        if (modAssets == null || !(Directory.Exists(modAssets) || File.Exists(modAssets)))
        {
            Console.Error.WriteLine("Mod assets not found. Pass --mod-assets <dir|zip>.");
            return 2;
        }

        using var save = options.SavePath != null ? new SaveFile(options.SavePath) : null;
        var world = save?.ToWorldSettings() ?? new WorldSettings();
        if (options.Seed is int seed) world.Seed = seed;
        if (options.WorldSize is int size) world.MapSizeX = world.MapSizeZ = size;
        if (options.WorldHeight is int height) world.MapSizeY = height;
        foreach (var (key, value) in options.WorldConfig) world.WorldConfig[key] = value;

        var modsDir = options.ModsDir == "none" ? null : options.ModsDir ?? Options.DefaultModsDir();
        if (modsDir != null && !Directory.Exists(modsDir))
        {
            Console.Error.WriteLine("Mods folder not found: " + modsDir);
            return 2;
        }
        var mods = AssetSource.ModsInFolder(modsDir, out var disabledMods);
        using var assets = new AssetSource(GameAssemblies.GameDir, modAssets, mods, world.WorldConfig);

        var configPath = options.ConfigPath ?? Options.DefaultModConfigPath();
        var config = MapGenerator.LoadConfig(configPath);

        var story = (save != null ? StoryStructures.FromSave(save, assets) : null) ?? StoryStructures.Compute(world, assets);

        Console.WriteLine($"Seed {world.Seed}, world {world.MapSizeX}x{world.MapSizeY}x{world.MapSizeZ}, sea level {world.SeaLevel}, " +
                          $"landform scale {world.Get("landformScale", "1")}, climate {world.Get("worldClimate", "realistic")}, " +
                          $"polar-equator distance {world.Get("polarEquatorDistance", "50000")}");
        Console.WriteLine($"Mod config: {configPath ?? "mod defaults"}; mod assets: {modAssets}");
        Console.WriteLine($"Other mods ({modsDir ?? "none"}): " + (assets.Mods.Count > 1 ? string.Join(", ", assets.Mods.Where(m => m.ModId != "terraprety")) : "none") +
                          (disabledMods.Count > 0 ? "; disabled, skipped: " + string.Join(", ", disabledMods) : ""));
        Console.WriteLine($"Story structures ({(save != null ? "from save" : "computed from seed")}, * forces land): " +
                          string.Join(", ", story.Select(s => $"{s.Code}{(s.RequireLandform != null ? "*" : "")} {s.X - world.MapSizeX / 2},{s.Z - world.MapSizeZ / 2}")));
        if (StoryStructures.MissingConfigs.Count > 0)
        {
            Console.WriteLine("Note: no config found for story structure(s) " + string.Join(", ", StoryStructures.MissingConfigs) +
                              "; the game doesn't force land for them either, but the mod that added them may be missing from --mods.");
        }

        var spec = new GeneratorSpec { GameDir = GameAssemblies.GameDir, ModAssets = modAssets, Mods = mods, World = world, ConfigJson = JsonConvert.SerializeObject(config), Story = story };
        var sw = Stopwatch.StartNew();
        var gen = new MapGenerator(world, config, assets, story);
        foreach (var line in assets.Log) Console.WriteLine("Note: " + line);
        Console.WriteLine($"Initialized in {sw.Elapsed.TotalSeconds:0.0}s ({gen.Landforms.LandFormsByIndex.Length} landforms)");

        if (options.Verify)
        {
            sw.Restart();
            Verifier.Run(gen, save);
            Console.WriteLine($"Verified in {sw.Elapsed.TotalSeconds:0.0}s");
        }

        sw.Restart();
        int threads = Math.Max(1, options.Threads ?? Environment.ProcessorCount);
        var grid = SampleGrid.Generate(gen, spec, options, threads);
        Console.WriteLine($"Sampled {grid.Width}x{grid.Height} px at {options.BlocksPerPixel} blocks/px on {threads} thread(s) in {sw.Elapsed.TotalSeconds:0.0}s");

        List<MapMarker> markers = null;
        SvgIcons icons = null;
        if (options.MapMarkers)
        {
            markers = MapMarkers.FromSave(save);
            icons = new SvgIcons(assets);
            var unknown = markers.Select(m => m.Icon).Distinct().Where(i => !icons.Has(i)).ToList();
            Console.WriteLine($"{markers.Count} map markers (icons: {string.Join(", ", markers.GroupBy(m => m.Icon).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))})" +
                              (unknown.Count > 0 ? $"; unknown icon(s) {string.Join(", ", unknown)} drawn as circle, as in the game" : ""));
        }

        float[] actual = null;
        if (options.Layers.Contains("actual"))
        {
            sw.Restart();
            actual = grid.ActualSurface(save);
            Console.WriteLine($"Read generated terrain heights from the savegame in {sw.Elapsed.TotalSeconds:0.0}s");
        }

        foreach (var layer in options.Layers)
        {
            var path = options.Layers.Count == 1 ? options.Output
                : Path.Combine(Path.GetDirectoryName(options.Output) ?? "", Path.GetFileNameWithoutExtension(options.Output) + "-" + layer + ".png");
            Png.Write(path, grid.Width, grid.Height, Renderer.Render(layer, grid, gen, world, actual, markers, icons, options.MarkerSize));
            Console.WriteLine($"Wrote {path}");
        }
        return 0;
    }
}
