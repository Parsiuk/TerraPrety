using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NanoSvg;
using Vintagestory.API.Common;

namespace TerraPrety.Preview;

// The in-game map's waypoint icons, rasterized with the nanosvg library the game ships (same calls as the game's SvgLoader).
public sealed class SvgIcons
{
    private readonly Dictionary<string, AssetLocation> icons = [];
    private readonly AssetSource assets;
    private readonly Dictionary<(string, int), float[]> cache = [];

    public IEnumerable<string> Codes => icons.Keys;

    public SvgIcons(AssetSource assets)
    {
        this.assets = assets;
        // WaypointMapLayer: every textures/icons/worldmap/*.svg, code = file name without extension and "<digits>-" prefix.
        foreach (var location in assets.List("textures/icons/worldmap/").Where(l => l.Path.EndsWith(".svg")))
        {
            var name = location.Path[(location.Path.LastIndexOf('/') + 1)..];
            icons[Regex.Replace(name[..name.IndexOf('.')], "\\d+\\-", "")] = location;
        }
    }

    public bool Has(string code) => icons.ContainsKey(code);

    // Coverage (0..1) of a size x size square with the icon fitted and centered; the game falls back to "circle".
    public float[] Mask(string code, int size)
    {
        if (!icons.ContainsKey(code)) code = "circle";
        if (!icons.TryGetValue(code, out var location)) return null;
        if (cache.TryGetValue((code, size), out var mask)) return mask;

        var rgba = Rasterize(assets.Get(location).ToText(), size);
        mask = new float[size * size];
        for (int i = 0; i < mask.Length; i++) mask[i] = rgba[i * 4 + 3] / 255f;
        return cache[(code, size)] = mask;
    }

    private static byte[] Rasterize(string svg, int size)
    {
        var image = SvgNativeMethods.nsvgParse(svg, "px", 96f);
        if (image == IntPtr.Zero) throw new InvalidOperationException("Could not parse SVG icon.");
        var rasterizer = SvgNativeMethods.nsvgCreateRasterizer();
        var pixels = new byte[size * size * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var dims = Marshal.PtrToStructure<NsvgSize>(SvgNativeMethods.nsvgImageGetSize(image));
            float scale = Math.Min(size / dims.width, size / dims.height);
            float tx = (size - dims.width * scale) / 2, ty = (size - dims.height * scale) / 2;
            SvgNativeMethods.nsvgRasterize(rasterizer, image, (int)tx, (int)ty, scale, handle.AddrOfPinnedObject(), size, size, size * 4);
        }
        finally
        {
            handle.Free();
            SvgNativeMethods.nsvgDeleteRasterizer(rasterizer);
            SvgNativeMethods.nsvgDelete(image);
        }
        return pixels;
    }
}
