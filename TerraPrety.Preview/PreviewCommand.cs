using System;
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
        var assets = new AssetSource(GameAssemblies.GameDir, modAssets);

        using var save = options.SavePath != null ? new SaveFile(options.SavePath) : null;
        var world = save?.ToWorldSettings() ?? new WorldSettings();
        if (options.Seed is int seed) world.Seed = seed;
        if (options.WorldSize is int size) world.MapSizeX = world.MapSizeZ = size;
        if (options.WorldHeight is int height) world.MapSizeY = height;
        foreach (var (key, value) in options.WorldConfig) world.WorldConfig[key] = value;

        var configPath = options.ConfigPath ?? Options.DefaultModConfigPath();
        var config = MapGenerator.LoadConfig(configPath);

        var story = (save != null ? StoryStructures.FromSave(save, assets) : null) ?? StoryStructures.Compute(world, assets);

        Console.WriteLine($"Seed {world.Seed}, world {world.MapSizeX}x{world.MapSizeY}x{world.MapSizeZ}, sea level {world.SeaLevel}, " +
                          $"landform scale {world.Get("landformScale", "1")}, climate {world.Get("worldClimate", "realistic")}, " +
                          $"polar-equator distance {world.Get("polarEquatorDistance", "50000")}");
        Console.WriteLine($"Mod config: {configPath ?? "mod defaults"}; mod assets: {modAssets}");
        Console.WriteLine($"Story structures ({(save != null ? "from save" : "computed from seed")}): " +
                          string.Join(", ", story.Select(s => $"{s.Code} {s.X - world.MapSizeX / 2},{s.Z - world.MapSizeZ / 2}")));

        var spec = new GeneratorSpec { GameDir = GameAssemblies.GameDir, ModAssets = modAssets, World = world, ConfigJson = JsonConvert.SerializeObject(config), Story = story };
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

        foreach (var layer in options.Layers)
        {
            var path = options.Layers.Count == 1 ? options.Output
                : Path.Combine(Path.GetDirectoryName(options.Output) ?? "", Path.GetFileNameWithoutExtension(options.Output) + "-" + layer + ".png");
            Png.Write(path, grid.Width, grid.Height, Renderer.Render(layer, grid, gen, world));
            Console.WriteLine($"Wrote {path}");
        }
        return 0;
    }
}
