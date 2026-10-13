using System;
using Vintagestory.API.Common;

namespace TerraPrety.Preview;

public static class Renderer
{
    public static uint[] Render(string layer, SampleGrid grid, MapGenerator gen, WorldSettings world)
    {
        var pixels = new uint[grid.Width * grid.Height];
        System.Func<SampleGrid, int, int, uint> shader = layer switch
        {
            "composite" => (g, x, z) => Composite(g, x, z, world),
            "height" => (g, x, z) => HeightMap(g.At(x, z)),
            "landforms" => (g, x, z) => LandformColor(g.At(x, z), gen),
            "ocean" => (g, x, z) => Gradient(g.At(x, z).Oceanicity / 255.0, 0x1c2a1e, 0x0a1e5c, 0x5aa0e6),
            "mountains" => (g, x, z) => Mountains(g.At(x, z)),
            "climate" => (g, x, z) => ClimateColor(g.At(x, z), world),
            _ => throw new ArgumentException("Unknown layer " + layer),
        };

        for (int z = 0; z < grid.Height; z++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                pixels[z * grid.Width + x] = shader(grid, x, z);
            }
        }

        DrawMarkers(pixels, grid, gen, world);
        return pixels;
    }

    // Estimated terrain: water depth / elevation coloring, a climate tint (snow, deserts) and hillshading.
    private static uint Composite(SampleGrid g, int x, int z, WorldSettings world)
    {
        ref var s = ref g.At(x, z);
        float sea = world.SeaLevel;
        float elevation = s.SurfaceY - sea;

        if (elevation < 0)
        {
            double depth = Math.Clamp(-elevation / 80.0, 0, 1);
            return Gradient(depth, 0x7fc4e8, 0x2f6fb5, 0x0d2350);
        }

        uint color = Hypsometric(elevation);

        int rain = (s.Climate >> 8) & 0xff;
        float temp = Climate.GetScaledAdjustedTemperatureFloat((s.Climate >> 16) & 0xff, (int)elevation);
        if (temp < -6) color = Lerp(color, 0xf2f5f8, Math.Clamp((-6 - temp) / 6.0, 0, 1) * 0.85);
        else if (rain < 90 && temp > 12) color = Lerp(color, 0xd8c48a, Math.Clamp((90 - rain) / 60.0, 0, 1) * 0.75);

        // Hillshade from the north west, scaled so a 1 block per pixel slope at 16 blocks/px is subtle.
        float left = g.At(Math.Max(0, x - 1), z).SurfaceY, up = g.At(x, Math.Max(0, z - 1)).SurfaceY;
        float slope = ((s.SurfaceY - left) + (s.SurfaceY - up)) * 16f / g.BlocksPerPixel;
        double shade = Math.Clamp(1.0 + slope * 0.04, 0.6, 1.3);
        return Scale(color, shade);
    }

    private static uint Hypsometric(float elevation)
    {
        // elevation in blocks above sea level
        (float At, uint Color)[] stops =
        [
            (0, 0xc9c08a), (3, 0x7fa65a), (20, 0x5f8f45), (45, 0x8a9a52), (80, 0x9c845a), (120, 0x8b7865), (170, 0x9a9a9a), (230, 0xf4f4f4),
        ];
        for (int i = 1; i < stops.Length; i++)
        {
            if (elevation <= stops[i].At)
            {
                return Lerp(stops[i - 1].Color, stops[i].Color, (elevation - stops[i - 1].At) / (stops[i].At - stops[i - 1].At));
            }
        }
        return stops[^1].Color;
    }

    private static uint HeightMap(in MapSample s)
    {
        uint land = Gradient(Math.Clamp(s.LandformHeight, 0, 1), 0x1d3b1a, 0x8c8a3c, 0xf0ece0);
        return Lerp(land, 0x0c1f55, s.Oceanicity / 255.0 * 0.9);
    }

    private static uint LandformColor(in MapSample s, MapGenerator gen)
    {
        // Landform hex colors in the TerraPrety patch are all placeholders, so derive a stable color from the code.
        var code = gen.Landforms.LandFormsByIndex[s.LandformIndex].Code.Path;
        uint hash = 2166136261;
        foreach (char c in code) hash = (hash ^ c) * 16777619;
        uint color = HsvToRgb((hash % 360) / 360.0, 0.55 + (hash >> 9) % 30 / 100.0, 0.65 + (hash >> 17) % 30 / 100.0);
        return color;
    }

    private static uint Mountains(in MapSample s)
    {
        uint baseColor = Gradient(Math.Clamp(s.LandformHeight, 0, 1), 0x202020, 0x707070, 0xd0d0d0);
        baseColor = Lerp(baseColor, 0x0c1f55, s.Oceanicity / 255.0 * 0.8);
        uint withRange = Lerp(baseColor, 0xe0402a, s.MountainRange / 255.0 * 0.85);
        return s.Uplift < 0 ? Lerp(withRange, 0xffd23c, Math.Clamp(-s.Uplift / 64.0, 0, 1)) : withRange;
    }

    private static uint ClimateColor(in MapSample s, WorldSettings world)
    {
        float temp = Climate.GetScaledAdjustedTemperatureFloat((s.Climate >> 16) & 0xff, 0);
        double rain = ((s.Climate >> 8) & 0xff) / 255.0;
        uint t = Gradient(Math.Clamp((temp + 20) / 55.0, 0, 1), 0x3b4cc0, 0xdddddd, 0xb40426);
        uint c = Lerp(0xe8dcb5, t, 0.35 + rain * 0.65);
        return s.SurfaceY < world.SeaLevel ? Scale(c, 0.55) : c;
    }

    private static void DrawMarkers(uint[] pixels, SampleGrid grid, MapGenerator gen, WorldSettings world)
    {
        void Mark(int bx, int bz, uint color, int radius)
        {
            int cx = (bx - grid.OriginX) / grid.BlocksPerPixel, cz = (bz - grid.OriginZ) / grid.BlocksPerPixel;
            for (int dx = -radius - 1; dx <= radius + 1; dx++)
            {
                for (int dz = -radius - 1; dz <= radius + 1; dz++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (x < 0 || z < 0 || x >= grid.Width || z >= grid.Height) continue;
                    int d2 = dx * dx + dz * dz;
                    if (d2 <= radius * radius) pixels[z * grid.Width + x] = color;
                    else if (d2 <= (radius + 1) * (radius + 1)) pixels[z * grid.Width + x] = 0x101010;
                }
            }
        }

        int markerRadius = Math.Clamp(grid.Width / 300, 2, 6);
        Mark(world.MapSizeX / 2, world.MapSizeZ / 2, 0xffffff, markerRadius + 1);
        foreach (var loc in gen.StoryLocations) Mark(loc.X, loc.Z, 0xff3df5, markerRadius);
    }

    private static uint Gradient(double t, uint a, uint b, uint c) => t < 0.5 ? Lerp(a, b, t * 2) : Lerp(b, c, (t - 0.5) * 2);

    private static uint Lerp(uint a, uint b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        uint Ch(int shift) => (uint)Math.Round(((a >> shift) & 0xff) * (1 - t) + ((b >> shift) & 0xff) * t);
        return (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    private static uint Scale(uint c, double f)
    {
        uint Ch(int shift) => (uint)Math.Clamp(Math.Round(((c >> shift) & 0xff) * f), 0, 255);
        return (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    private static uint HsvToRgb(double h, double s, double v)
    {
        double r = 0, g = 0, b = 0;
        int i = (int)(h * 6);
        double f = h * 6 - i, p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            case 5: r = v; g = p; b = q; break;
        }
        return ((uint)(r * 255) << 16) | ((uint)(g * 255) << 8) | (uint)(b * 255);
    }
}
