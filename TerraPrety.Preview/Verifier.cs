using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Datastructures;
using Vintagestory.Server;

namespace TerraPrety.Preview;

// Regenerates map regions the game already stored in a savegame and reports how closely they match.
public static class Verifier
{
    public static bool Run(MapGenerator gen, SaveFile save, int maxRegions = 64)
    {
        var positions = save.RegionPositions().ToList();
        // Spread the sample over the whole explored area rather than taking a contiguous block.
        var sample = positions.Count <= maxRegions ? positions : positions.Where((_, i) => i % (positions.Count / maxRegions + 1) == 0).Take(maxRegions).ToList();

        var ocean = new Stat("Ocean map");
        var landform = new Stat("Landform map");
        var height = new Stat("Landform height map");
        var climate = new Stat("Climate map");
        var worstRegions = new List<(double Score, int X, int Z)>();

        foreach (var (rx, rz) in sample)
        {
            var region = save.GetRegion(rx, rz);
            if (region == null) continue;

            ocean.Add(gen.RegionOceanMap(rx, rz), region.OceanMap, tolerance: 0);
            var lf = gen.RegionLandformMap(rx, rz, out var heights);
            double lfScore = landform.Add(lf, region.LandformMap, tolerance: 0);
            if (region.ModMaps.TryGetValue("LandformHeightMap", out var storedHeights)) height.Add(heights, storedHeights, tolerance: 0);
            climate.Add(gen.RegionClimateMap(rx, rz), region.ClimateMap, tolerance: 0);
            worstRegions.Add((lfScore, rx, rz));
        }

        Console.WriteLine($"Compared {sample.Count} of {positions.Count} stored map regions:");
        foreach (var s in new[] { ocean, landform, height, climate }) Console.WriteLine("  " + s);

        var worst = worstRegions.OrderBy(w => w.Score).Take(3).Where(w => w.Score < 1).ToList();
        if (worst.Count > 0)
        {
            Console.WriteLine("  Least matching landform regions: " + string.Join(", ", worst.Select(w => $"({w.X},{w.Z}) {w.Score:P1}")));
        }
        VerifySurfaceHeights(gen, save);
        return ocean.ExactFraction > 0.99 && landform.ExactFraction > 0.95;
    }

    // The surface height is only an estimate (the game computes it from 3D noise), so report how far off it is.
    private static void VerifySurfaceHeights(MapGenerator gen, SaveFile save, int maxChunks = 1500)
    {
        var chunks = save.MapChunkHeights().ToList();
        if (chunks.Count == 0) return;

        int step = chunks.Count / maxChunks + 1, sea = gen.World.SeaLevel;
        long n = 0, waterAgree = 0, actualWater = 0, estWater = 0;
        double sumErr = 0, sumAbs = 0;
        var errors = new List<double>();
        for (int c = 0; c < chunks.Count; c += step)
        {
            var (cx, cz, heights) = chunks[c];
            for (int lx = 4; lx < 32; lx += 8)
            {
                for (int lz = 4; lz < 32; lz += 8)
                {
                    int actual = heights[lz * 32 + lx];
                    float est = gen.Sample(cx * 32 + lx, cz * 32 + lz).SurfaceY;
                    double err = est - actual;
                    sumErr += err; sumAbs += Math.Abs(err); errors.Add(Math.Abs(err)); n++;
                    bool aw = actual < sea - 1, ew = est < sea;
                    if (aw) actualWater++;
                    if (ew) estWater++;
                    if (aw == ew) waterAgree++;
                }
            }
        }
        errors.Sort();
        Console.WriteLine($"  Surface height estimate vs {n} terrain columns from {chunks.Count / step} stored map chunks:");
        Console.WriteLine($"    mean error {sumErr / n:+0.0;-0.0} blocks, mean abs error {sumAbs / n:0.0}, median abs {errors[errors.Count / 2]:0.0}, 90th percentile {errors[(int)(errors.Count * 0.9)]:0.0}");
        Console.WriteLine($"    land/water agreement {(double)waterAgree / n:P1} (water: {(double)actualWater / n:P1} actual, {(double)estWater / n:P1} estimated)");
    }

    private class Stat(string name)
    {
        private long total, exact, missing;
        private double absDiff;

        public double ExactFraction => total == 0 ? 0 : (double)exact / total;

        public double Add(int[] generated, IntDataMap2D stored, int tolerance)
        {
            if (stored?.Data == null || stored.Data.Length == 0)
            {
                missing++;
                return 1;
            }
            if (stored.Data.Length != generated.Length)
            {
                throw new InvalidOperationException($"{name}: stored size {stored.Data.Length} vs generated {generated.Length}; region layout differs from what the preview expects.");
            }

            long localExact = 0;
            for (int i = 0; i < generated.Length; i++)
            {
                int d = Math.Abs(generated[i] - stored.Data[i]);
                if (d <= tolerance) localExact++;
                absDiff += d;
            }
            total += generated.Length;
            exact += localExact;
            return (double)localExact / generated.Length;
        }

        public override string ToString() => total == 0
            ? $"{name,-20} not stored in this save"
            : $"{name,-20} {ExactFraction,8:P2} of tiles identical, mean abs difference {absDiff / total:0.###}" + (missing > 0 ? $" ({missing} regions without data)" : "");
    }
}
