using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TerraPrety.Preview;

public class Options
{
    public static readonly string[] AllLayers = ["composite", "height", "landforms", "ocean", "mountains", "climate", "actual"];

    public bool ShowHelp;
    public string GameDir;
    public string SavePath;
    public string ConfigPath;
    public string ModAssets;
    public string ModsDir;

    public int? Seed;
    public int? WorldSize;
    public int? WorldHeight;
    public Dictionary<string, string> WorldConfig = [];

    public int CenterX;
    public int CenterZ;
    public int SizeBlocks = 65536;
    public int BlocksPerPixel = 32;
    public List<string> Layers = ["composite"];
    public string Output = "preview.png";
    public bool Verify;
    public bool AllLayersRequested;
    public bool MapMarkers;
    public static readonly string[] TranslocatorFilters = ["all", "repaired", "broken"];

    public string MapTranslocators; // null when off, otherwise one of TranslocatorFilters
    public int? MarkerSize;
    public int? Threads;

    public const string Usage = """
        Usage: terraprety-preview [options]

        Renders a PNG preview of TerraPrety's large-scale world generation (continents, landform heights,
        mountain ranges, climate) for a seed, without starting the game.

        World settings (defaults are the game's defaults; --save reads them from a savegame):
          --save <file.vcdbs>            Read seed and world settings from a savegame (opened read-only)
          --seed <n>                     World seed
          --world-height <n>             World height in blocks (default 256)
          --world-size <n>               World width and length in blocks (default 1024000)
          --landform-scale <f|n%>        Landform scale, e.g. 3 or 300% (default 1)
          --polar-equator-distance <n>   Polar-equator distance in blocks (default 50000)
          --climate <realistic|patchy>   World climate (default realistic)
          --starting-climate <name>      temperate, hot, warm, cool or icy (default temperate)
          --temperature <f|n%>           Global temperature (default 1)
          --rainfall <f|n%>              Global precipitation (default 1)
          --geologic-activity <f|n%>     Geologic activity (default 0.05)
          --upheaval <f|n%>              Upheaval rate (TerraPrety replaces upheaval; accepted for completeness)
          --landcover <f|n%>             Land cover (TerraPrety ignores it; accepted for completeness)
          --set <key=value>              Any other world configuration value

        Mod:
          --config <TerraPrety.json>     Mod config (default: VintagestoryData/ModConfig/TerraPrety.json if it exists,
                                         otherwise the defaults compiled into the mod)
          --mod-assets <dir|zip>         Mod assets folder or released mod zip (default: this repo's TerraPrety/assets)
          --mods <dir|none>              Other installed mods, whose assets and patches can change world generation, e.g.
                                         story structures that force land (default: VintagestoryData/Mods if it exists;
                                         mods disabled in the game's mod manager are skipped; any TerraPrety there is
                                         replaced by --mod-assets)
          --game <dir>                   Vintage Story install folder (default: $VINTAGE_STORY or the build-time path)

        Area and output:
          --center <x,z>                 Center in in-game coordinates, i.e. relative to the world spawn (default 0,0)
          --size <blocks>                Width and height of the rendered square (default 65536)
          --scale <blocks>               Blocks per pixel, at least 1 (default 32)
          --layers <list|all>            Comma separated: composite, height, landforms, ocean, mountains, climate, and with
                                         --save also actual (the terrain the game generated, for comparison with composite)
          -o, --output <file.png>        Output file (default preview.png). With several layers,
                                         each one is written as <name>-<layer>.png
          --threads <n>                  Worker threads for sampling (default: all CPU cores)
          --map-markers                  Draw the players' map markers from --save, with the in-game icons and colors
          --marker-size <px>             Map marker icon size in pixels (default: 8 to 16, growing with the image size)
          --map-translocators [which]    Draw the static translocators the game generated in --save: cyan when repaired,
                                         orange when broken, with a line between linked ones. which is all (default),
                                         repaired or broken
          --verify                       Compare the generated ocean and landform maps against the map regions
                                         the game stored in --save, and report how well they match
          -h, --help                     Show this help
        """;

    public static string PeekGameDir(string[] args)
    {
        var i = Array.IndexOf(args, "--game");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {arg}.");

            switch (arg)
            {
                case "-h": case "--help": o.ShowHelp = true; break;
                case "--game": o.GameDir = Next(); break;
                case "--save": o.SavePath = Next(); break;
                case "--config": o.ConfigPath = Next(); break;
                case "--mod-assets": o.ModAssets = Next(); break;
                case "--mods": o.ModsDir = Next(); break;
                case "--seed": o.Seed = ParseInt(arg, Next()); break;
                case "--world-size": o.WorldSize = ParseInt(arg, Next()); break;
                case "--world-height": o.WorldHeight = ParseInt(arg, Next()); break;
                case "--landform-scale": o.WorldConfig["landformScale"] = ParseFraction(arg, Next()); break;
                case "--polar-equator-distance": o.WorldConfig["polarEquatorDistance"] = ParseInt(arg, Next()).ToString(CultureInfo.InvariantCulture); break;
                case "--climate": o.WorldConfig["worldClimate"] = Next(); break;
                case "--starting-climate": o.WorldConfig["startingClimate"] = Next(); break;
                case "--temperature": o.WorldConfig["globalTemperature"] = ParseFraction(arg, Next()); break;
                case "--rainfall": o.WorldConfig["globalPrecipitation"] = ParseFraction(arg, Next()); break;
                case "--geologic-activity": o.WorldConfig["geologicActivity"] = ParseFraction(arg, Next()); break;
                case "--upheaval": o.WorldConfig["upheavelCommonness"] = ParseFraction(arg, Next()); break;
                case "--landcover": o.WorldConfig["landcover"] = ParseFraction(arg, Next()); break;
                case "--set":
                    var kv = Next().Split('=', 2);
                    if (kv.Length != 2) throw new ArgumentException("--set expects key=value.");
                    o.WorldConfig[kv[0]] = kv[1];
                    break;
                case "--center":
                    var parts = Next().Split(',');
                    if (parts.Length != 2) throw new ArgumentException("--center expects x,z.");
                    o.CenterX = ParseInt(arg, parts[0]);
                    o.CenterZ = ParseInt(arg, parts[1]);
                    break;
                case "--size": o.SizeBlocks = ParseInt(arg, Next()); break;
                case "--scale": o.BlocksPerPixel = ParseInt(arg, Next()); break;
                case "--layers":
                    var value = Next();
                    o.Layers = value == "all" ? [.. AllLayers] : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    o.AllLayersRequested = value == "all";
                    var unknown = o.Layers.Except(AllLayers).ToList();
                    if (unknown.Count > 0) throw new ArgumentException("Unknown layer(s): " + string.Join(", ", unknown));
                    break;
                case "-o": case "--output": o.Output = Next(); break;
                case "--verify": o.Verify = true; break;
                case "--map-markers": o.MapMarkers = true; break;
                case "--map-translocators":
                    // The filter is optional: take the next argument only if it is one.
                    o.MapTranslocators = i + 1 < args.Length && TranslocatorFilters.Contains(args[i + 1]) ? args[++i] : "all";
                    break;
                case "--marker-size": o.MarkerSize = ParseInt(arg, Next()); break;
                case "--threads": o.Threads = ParseInt(arg, Next()); break;
                default: throw new ArgumentException("Unknown option: " + arg);
            }
        }

        if (o.BlocksPerPixel < 1) throw new ArgumentException("--scale must be at least 1.");
        if (o.SizeBlocks < o.BlocksPerPixel) throw new ArgumentException("--size must be at least --scale.");
        if (o.Verify && o.SavePath == null) throw new ArgumentException("--verify needs --save.");
        if (o.MapMarkers && o.SavePath == null) throw new ArgumentException("--map-markers needs --save.");
        if (o.MapTranslocators != null && o.SavePath == null) throw new ArgumentException("--map-translocators needs --save.");
        if (o.MarkerSize is int ms && (ms < 2 || ms > 256)) throw new ArgumentException("--marker-size must be between 2 and 256.");
        if (o.MarkerSize != null && !o.MapMarkers) throw new ArgumentException("--marker-size needs --map-markers.");
        if (o.SavePath == null && o.AllLayersRequested) o.Layers.Remove("actual");
        if (o.SavePath == null && o.Layers.Contains("actual")) throw new ArgumentException("The actual layer needs --save.");
        if (o.SavePath == null && o.Seed == null && !o.ShowHelp) throw new ArgumentException("Pass --seed or --save.");
        return o;
    }

    public static string DefaultModConfigPath()
    {
        var path = Path.Combine(DataDir, "ModConfig", "TerraPrety.json");
        return File.Exists(path) ? path : null;
    }

    public static string DefaultModsDir()
    {
        var path = Path.Combine(DataDir, "Mods");
        return Directory.Exists(path) ? path : null;
    }

    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VintagestoryData");

    private static int ParseInt(string name, string value) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException($"{name}: '{value}' is not a whole number.");

    // Accepts 0.8 as well as 80% (the world creation screen shows percentages, the savegame stores fractions).
    private static string ParseFraction(string name, string value)
    {
        value = value.Trim();
        bool percent = value.EndsWith('%');
        if (!double.TryParse(percent ? value[..^1] : value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
        {
            throw new ArgumentException($"{name}: '{value}' is not a number.");
        }
        return (percent ? f / 100 : f).ToString(CultureInfo.InvariantCulture);
    }
}
