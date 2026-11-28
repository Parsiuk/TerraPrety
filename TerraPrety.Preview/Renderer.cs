using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;

namespace TerraPrety.Preview;

public static class Renderer
{
    public static uint[] Render(string layer, SampleGrid grid, MapGenerator gen, WorldSettings world, float[] actualSurface = null,
                                IReadOnlyList<MapMarker> markers = null, SvgIcons icons = null, int? markerSize = null,
                                IReadOnlyList<Translocator> translocators = null)
    {
        var pixels = RenderLayer(layer, grid, gen, world, actualSurface);
        if (translocators != null) DrawTranslocators(pixels, grid, translocators);
        if (markers != null && icons != null) DrawMapMarkers(pixels, grid, markers, icons, markerSize);
        return pixels;
    }

    private static uint[] RenderLayer(string layer, SampleGrid grid, MapGenerator gen, WorldSettings world, float[] actualSurface)
    {
        if (layer == "actual") return RenderActual(grid, gen, world, actualSurface ?? throw new ArgumentException("The actual layer needs --save."));

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

    // What the game generated: the composite palette over the terrain heights stored in the savegame, with the estimate
    // dimmed where nothing has been generated yet. Compare with the composite layer to see how the estimate holds up.
    private static uint[] RenderActual(SampleGrid grid, MapGenerator gen, WorldSettings world, float[] actualSurface)
    {
        var estimate = RenderLayer("composite", grid, gen, world, null);
        var actual = new SampleGrid { Width = grid.Width, Height = grid.Height, BlocksPerPixel = grid.BlocksPerPixel, OriginX = grid.OriginX, OriginZ = grid.OriginZ };
        actual.Samples = (MapSample[])grid.Samples.Clone();
        for (int i = 0; i < actual.Samples.Length; i++)
        {
            if (!float.IsNaN(actualSurface[i])) actual.Samples[i].SurfaceY = actualSurface[i];
        }

        var pixels = new uint[grid.Width * grid.Height];
        for (int z = 0; z < grid.Height; z++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                int i = z * grid.Width + x;
                pixels[i] = float.IsNaN(actualSurface[i]) ? Lerp(Scale(estimate[i], 0.45), 0x303030, 0.5) : Composite(actual, x, z, world);
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

    private const uint RepairedTranslocator = 0x3ee0f0, BrokenTranslocator = 0xd08a3a, LinkLine = 0x9ff6ff, Outline = 0x101010;

    // Static translocators as inverted triangles, the bottom vertex on the translocator's block, and a line between each
    // linked pair. Broken ones are drawn first so repaired ones end up on top where they overlap.
    private static void DrawTranslocators(uint[] pixels, SampleGrid grid, IReadOnlyList<Translocator> translocators)
    {
        // Center of a block in pixel space, where pixel i covers [i, i + 1).
        (double X, double Z) ToPixel(int bx, int bz) =>
            ((bx + 0.5 - grid.OriginX) / grid.BlocksPerPixel, (bz + 0.5 - grid.OriginZ) / grid.BlocksPerPixel);

        var drawn = new HashSet<((int, int, int), (int, int, int))>();
        foreach (var t in translocators)
        {
            if (t.Link is not { } link) continue;
            var a = (t.X, t.Y, t.Z);
            if (!drawn.Add(a.CompareTo(link) < 0 ? (a, link) : (link, a))) continue;
            var (x0, z0) = ToPixel(t.X, t.Z);
            var (x1, z1) = ToPixel(link.X, link.Z);
            DrawLine(pixels, grid, x0, z0, x1, z1, LinkLine, 0.9);
        }

        double width = Math.Clamp(grid.Width / 200, 7, 14), height = width * 0.9;
        foreach (var t in translocators.OrderBy(t => t.Repaired).ThenBy(t => t.Z))
        {
            var (ax, az) = ToPixel(t.X, t.Z);
            if (ax < -width || az < -1 || ax >= grid.Width + width || az >= grid.Height + height + 2) continue;
            DrawTriangle(pixels, grid, ax, az, width, height, 1.0, Outline);
            DrawTriangle(pixels, grid, ax, az, width, height, 0, t.Repaired ? RepairedTranslocator : BrokenTranslocator);
        }
    }

    // A triangle pointing down with its bottom vertex at (ax, az), grown by `grow` pixels on every side, blended in with
    // 4x4 supersampled coverage.
    private static void DrawTriangle(uint[] pixels, SampleGrid grid, double ax, double az, double width, double height, double grow, uint color)
    {
        // Edges as inward unit normals n and a point p on the edge: inside where n . (s - p) >= -grow.
        (double X, double Z) left = (ax - width / 2, az - height), right = (ax + width / 2, az - height);
        (double Nx, double Nz, double Px, double Pz)[] edges =
        [
            Edge(left.X, left.Z, right.X, right.Z),
            Edge(right.X, right.Z, ax, az),
            Edge(ax, az, left.X, left.Z),
        ];

        // Inward normal of the edge a -> b for a triangle wound left, right, apex (clockwise on screen, z down).
        static (double, double, double, double) Edge(double x0, double z0, double x1, double z1)
        {
            double dx = x1 - x0, dz = z1 - z0, len = Math.Sqrt(dx * dx + dz * dz);
            return (-dz / len, dx / len, x0, z0);
        }

        const int Sub = 4;
        int px0 = (int)Math.Floor(left.X - grow - 1), px1 = (int)Math.Ceiling(right.X + grow + 1);
        int pz0 = (int)Math.Floor(left.Z - grow - 1), pz1 = (int)Math.Ceiling(az + grow * 2 + 1);
        for (int pz = Math.Max(0, pz0); pz <= Math.Min(grid.Height - 1, pz1); pz++)
        {
            for (int px = Math.Max(0, px0); px <= Math.Min(grid.Width - 1, px1); px++)
            {
                int inside = 0;
                for (int sz = 0; sz < Sub; sz++)
                {
                    for (int sx = 0; sx < Sub; sx++)
                    {
                        double x = px + (sx + 0.5) / Sub, z = pz + (sz + 0.5) / Sub;
                        bool ok = true;
                        foreach (var e in edges)
                        {
                            if (e.Nx * (x - e.Px) + e.Nz * (z - e.Pz) < -grow) { ok = false; break; }
                        }
                        if (ok) inside++;
                    }
                }
                if (inside == 0) continue;
                ref uint p = ref pixels[pz * grid.Width + px];
                p = Lerp(p, color, inside / (double)(Sub * Sub));
            }
        }
    }

    // An anti-aliased 1 px line (Xiaolin Wu), clipped to the image; endpoints may lie far outside it.
    private static void DrawLine(uint[] pixels, SampleGrid grid, double x0, double z0, double x1, double z1, uint color, double opacity)
    {
        void Plot(int x, int z, double coverage)
        {
            if (x < 0 || z < 0 || x >= grid.Width || z >= grid.Height || coverage <= 0) return;
            ref uint p = ref pixels[z * grid.Width + x];
            p = Lerp(p, color, coverage * opacity);
        }

        // Work in pixel-center coordinates so integer positions are pixel centers.
        x0 -= 0.5; z0 -= 0.5; x1 -= 0.5; z1 -= 0.5;
        bool steep = Math.Abs(z1 - z0) > Math.Abs(x1 - x0);
        if (steep) { (x0, z0) = (z0, x0); (x1, z1) = (z1, x1); }
        if (x0 > x1) { (x0, x1) = (x1, x0); (z0, z1) = (z1, z0); }
        double dx = x1 - x0, gradient = dx == 0 ? 0 : (z1 - z0) / dx;
        int major = steep ? grid.Height : grid.Width;

        int start = (int)Math.Max(Math.Round(x0), 0), end = (int)Math.Min(Math.Round(x1), major - 1);
        for (int i = start; i <= end; i++)
        {
            double z = z0 + gradient * (i - x0);
            int zi = (int)Math.Floor(z);
            double f = z - zi;
            if (steep) { Plot(zi, i, 1 - f); Plot(zi + 1, i, f); }
            else { Plot(i, zi, 1 - f); Plot(i, zi + 1, f); }
        }
    }

    // WaypointMapComponent.Render: the icon in black at 60% opacity, a little larger and offset down right, then the
    // icon in the marker's color on top.
    private static void DrawMapMarkers(uint[] pixels, SampleGrid grid, IReadOnlyList<MapMarker> markers, SvgIcons icons, int? markerSize)
    {
        // Default kept small so markers don't hide much terrain, but large enough to tell the icons apart.
        int size = markerSize ?? Math.Clamp(grid.Width / 160, 8, 16);
        int shadowSize = (int)Math.Round(size * 1.1);
        int offset = Math.Max(1, size / 16);

        void Stamp(float[] mask, int maskSize, int cx, int cz, uint color, double opacity)
        {
            int x0 = cx - maskSize / 2, z0 = cz - maskSize / 2;
            for (int dz = 0; dz < maskSize; dz++)
            {
                int z = z0 + dz;
                if (z < 0 || z >= grid.Height) continue;
                for (int dx = 0; dx < maskSize; dx++)
                {
                    int x = x0 + dx;
                    float a = mask[dz * maskSize + dx];
                    if (x < 0 || x >= grid.Width || a <= 0) continue;
                    ref uint p = ref pixels[z * grid.Width + x];
                    p = Lerp(p, color, a * opacity);
                }
            }
        }

        // Draw markers further south last, so overlapping icons stack like a map read top to bottom.
        foreach (var marker in markers.OrderBy(m => m.Z))
        {
            int cx = (marker.X - grid.OriginX) / grid.BlocksPerPixel, cz = (marker.Z - grid.OriginZ) / grid.BlocksPerPixel;
            if (cx < -size || cz < -size || cx >= grid.Width + size || cz >= grid.Height + size) continue;
            var mask = icons.Mask(marker.Icon, size);
            var shadow = icons.Mask(marker.Icon, shadowSize);
            if (mask == null) continue;

            double alpha = ((marker.Color >> 24) & 0xff) / 255.0;
            if (alpha == 0) alpha = 1; // Colors picked in the game are opaque; treat a missing alpha byte as opaque too.
            Stamp(shadow, shadowSize, cx + offset, cz + offset, 0x000000, 0.6 * alpha);
            Stamp(mask, size, cx, cz, (uint)marker.Color & 0xffffff, alpha);
        }
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
