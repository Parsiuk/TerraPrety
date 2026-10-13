using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TerraPrety;

namespace TerraPrety.Preview;

// Everything a worker needs to build an identical MapGenerator, in a form that crosses load contexts as plain JSON.
public class GeneratorSpec
{
    public string GameDir;
    public string ModAssets;
    public WorldSettings World;
    public string ConfigJson;
    public List<StoryLocation> Story;

    public MapGenerator Build() => new(World, JsonConvert.DeserializeObject<WorldGenConfig>(ConfigJson), new AssetSource(GameDir, ModAssets), Story);
}

// The mod's map layers keep static and per-instance RNG state, so they can't be shared between threads. Each worker
// therefore loads its own copy of this assembly (and with it the mod's code and statics) into a separate
// AssemblyLoadContext; the game assemblies are shared, as their noise classes used here are thread safe.
public static class ParallelSampler
{
    private sealed class WorkerContext(int index) : AssemblyLoadContext("terraprety-preview-worker-" + index)
    {
        private static readonly Assembly Self = typeof(WorkerContext).Assembly;

        protected override Assembly Load(AssemblyName name) =>
            name.Name == Self.GetName().Name ? LoadFromAssemblyPath(Self.Location) : null;
    }

    public static void Sample(SampleGrid grid, GeneratorSpec spec, int threads)
    {
        int width = grid.Width, n = grid.Width * grid.Height;
        var ocean = new int[n]; var landform = new int[n]; var mountain = new int[n]; var uplift = new int[n]; var climate = new int[n];
        var height = new double[n]; var surface = new float[n];
        object[] buffers = [ocean, landform, height, mountain, uplift, climate, surface];

        var specJson = JsonConvert.SerializeObject(spec);
        var workers = new ConcurrentBag<(object Generator, MethodInfo SampleRow)>();
        Parallel.For(0, threads, i =>
        {
            var asm = new WorkerContext(i).LoadFromAssemblyPath(typeof(WorkerContext).Assembly.Location);
            var entry = asm.GetType(typeof(WorkerEntry).FullName);
            var generator = entry.GetMethod(nameof(WorkerEntry.Create)).Invoke(null, [specJson]);
            workers.Add((generator, entry.GetMethod(nameof(WorkerEntry.SampleRow))));
        });

        int done = 0;
        var progress = Stopwatch.StartNew();
        Parallel.For(0, grid.Height, new ParallelOptions { MaxDegreeOfParallelism = threads }, row =>
        {
            if (!workers.TryTake(out var worker)) throw new InvalidOperationException("No free sampler worker.");
            try
            {
                worker.SampleRow.Invoke(null, [worker.Generator, grid.OriginX, grid.OriginZ, grid.BlocksPerPixel, width, row, spec.World.MapSizeX, spec.World.MapSizeZ, buffers]);
            }
            finally
            {
                workers.Add(worker);
            }

            int count = Interlocked.Increment(ref done);
            if (progress.ElapsedMilliseconds >= 2000)
            {
                lock (progress)
                {
                    if (progress.ElapsedMilliseconds >= 2000)
                    {
                        Console.Write($"\r  {100.0 * count / grid.Height:0}%  ");
                        progress.Restart();
                    }
                }
            }
        });
        Console.Write("\r        \r");

        for (int i = 0; i < n; i++)
        {
            grid.Samples[i] = new MapSample
            {
                Oceanicity = ocean[i], LandformIndex = landform[i], LandformHeight = height[i], MountainRange = mountain[i],
                Uplift = uplift[i], Climate = climate[i], SurfaceY = surface[i],
            };
        }
    }
}

// Runs inside a worker load context; only primitive types cross the boundary.
public static class WorkerEntry
{
    public static object Create(string specJson) => JsonConvert.DeserializeObject<GeneratorSpec>(specJson).Build();

    public static void SampleRow(object generator, int originX, int originZ, int scale, int width, int row, int mapSizeX, int mapSizeZ, object[] buffers)
    {
        var gen = (MapGenerator)generator;
        var ocean = (int[])buffers[0]; var landform = (int[])buffers[1]; var height = (double[])buffers[2]; var mountain = (int[])buffers[3];
        var uplift = (int[])buffers[4]; var climate = (int[])buffers[5]; var surface = (float[])buffers[6];

        int bz = Math.Clamp(originZ + row * scale + scale / 2, 0, mapSizeZ - 1);
        for (int px = 0; px < width; px++)
        {
            int bx = Math.Clamp(originX + px * scale + scale / 2, 0, mapSizeX - 1);
            var s = gen.Sample(bx, bz);
            int i = row * width + px;
            ocean[i] = s.Oceanicity; landform[i] = s.LandformIndex; height[i] = s.LandformHeight; mountain[i] = s.MountainRange;
            uplift[i] = s.Uplift; climate[i] = s.Climate; surface[i] = s.SurfaceY;
        }
    }
}
