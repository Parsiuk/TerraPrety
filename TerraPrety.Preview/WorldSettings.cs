using System.Collections.Generic;
using System.Globalization;

namespace TerraPrety.Preview;

// Everything from the world creation screen (and savegame) that affects the large scale map generation.
// Values are kept as the same strings the game stores in SaveGame.WorldConfiguration.
public class WorldSettings
{
    public int Seed;
    public int MapSizeX = 1024000;
    public int MapSizeY = 256;
    public int MapSizeZ = 1024000;
    public int RegionSize = 512;
    public int ChunkSize = 32;

    public Dictionary<string, string> WorldConfig = new()
    {
        ["worldClimate"] = "realistic",
        ["startingClimate"] = "temperate",
        ["globalTemperature"] = "1",
        ["globalPrecipitation"] = "1",
        ["polarEquatorDistance"] = "50000",
        ["upheavelCommonness"] = "0.3",
        ["landcover"] = "1",
        ["oceanscale"] = "1",
        ["landformScale"] = "1.0",
        ["geologicActivity"] = "0.05",
    };

    public string Get(string key, string fallback) => WorldConfig.TryGetValue(key, out var v) && v != null ? v : fallback;

    public float GetFloat(string key, float fallback) =>
        float.TryParse(Get(key, null), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    // Sea level as patched by TerraPrety (vanilla formula, with the world lowered by NumBlocksLowerWorldBy).
    public int SeaLevel => (int)(0.4313725490196078 * (MapSizeY - NumBlocksLowerWorldBy));

    // Mirrors TerraPretyModSystem.NumBlocksLowerWorldBy, which can't be linked in without pulling in the whole mod.
    public const int NumBlocksLowerWorldBy = 64;
}
