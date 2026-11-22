using System;
using System.Diagnostics;

namespace TerraPrety.Preview;

public class SampleGrid
{
    public int Width;
    public int Height;
    public int BlocksPerPixel;
    public int OriginX;   // Absolute block coordinate of the top left pixel's corner
    public int OriginZ;
    public MapSample[] Samples;

    public ref MapSample At(int px, int pz) => ref Samples[pz * Width + px];

    // Terrain surface the game generated at each pixel's center (one above the top terrain block, so below sea level
    // means water, as in MapSample.SurfaceY), NaN where the savegame has no map chunk.
    public float[] ActualSurface(SaveFile save)
    {
        var result = new float[Width * Height];
        Array.Fill(result, float.NaN);
        int half = BlocksPerPixel / 2;

        // Pixel index whose center lies on block coordinate b, or -1.
        static int PixelAt(int b, int origin, int scale, int half, int count)
        {
            int offset = b - origin - half;
            if (offset < 0 || offset % scale != 0) return -1;
            int p = offset / scale;
            return p < count ? p : -1;
        }

        foreach (var (cx, cz, heights) in save.MapChunkHeights())
        {
            for (int lz = 0; lz < 32; lz++)
            {
                int pz = PixelAt(cz * 32 + lz, OriginZ, BlocksPerPixel, half, Height);
                if (pz < 0) continue;
                for (int lx = 0; lx < 32; lx++)
                {
                    int px = PixelAt(cx * 32 + lx, OriginX, BlocksPerPixel, half, Width);
                    if (px >= 0) result[pz * Width + px] = heights[lz * 32 + lx] + 1;
                }
            }
        }
        return result;
    }

    public static SampleGrid Generate(MapGenerator gen, GeneratorSpec spec, Options options, int threads)
    {
        var world = spec.World;
        int scale = options.BlocksPerPixel;
        var grid = new SampleGrid
        {
            Width = options.SizeBlocks / scale,
            Height = options.SizeBlocks / scale,
            BlocksPerPixel = scale,
            OriginX = world.MapSizeX / 2 + options.CenterX - options.SizeBlocks / 2,
            OriginZ = world.MapSizeZ / 2 + options.CenterZ - options.SizeBlocks / 2,
        };
        grid.Samples = new MapSample[grid.Width * grid.Height];

        if (threads > 1)
        {
            ParallelSampler.Sample(grid, spec, threads);
            return grid;
        }

        var progress = Stopwatch.StartNew();
        for (int pz = 0; pz < grid.Height; pz++)
        {
            for (int px = 0; px < grid.Width; px++)
            {
                int bx = Math.Clamp(grid.OriginX + px * scale + scale / 2, 0, world.MapSizeX - 1);
                int bz = Math.Clamp(grid.OriginZ + pz * scale + scale / 2, 0, world.MapSizeZ - 1);
                grid.Samples[pz * grid.Width + px] = gen.Sample(bx, bz);
            }

            if (progress.Elapsed.TotalSeconds >= 2)
            {
                Console.Write($"\r  {100.0 * (pz + 1) / grid.Height:0}%  ");
                progress.Restart();
            }
        }
        Console.Write("\r        \r");
        return grid;
    }
}
