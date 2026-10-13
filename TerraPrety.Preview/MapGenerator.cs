using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MapLayer;
using Newtonsoft.Json;
using TerraPrety.LandformHeights;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace TerraPrety.Preview;

// One sampled map position.
public struct MapSample
{
    public int Oceanicity;          // 0..255, ocean map value (TerraPrety's continent/ocean noise)
    public int LandformIndex;       // Index into Landforms.LandFormsByIndex
    public double LandformHeight;   // 0..1, TerraPrety's landform height map
    public int MountainRange;       // 0..255, mountain range opacity
    public int Uplift;              // Uplift map value (TerraPrety stores it in the upheaval map)
    public int Climate;             // Packed climate: temperature << 16 | rain << 8 | geologic activity
    public float SurfaceY;          // Estimated terrain height in blocks (see EstimateSurfaceY)
}

// Builds the map layers the way GenMaps.initWorldGen does once TerraPrety's Harmony patches are applied,
// using the mod's own map layer classes compiled from the repo.
public class MapGenerator
{
    private readonly WorldSettings world;
    private readonly ICoreServerAPI api;
    private readonly MapLayerOceansSmooth oceanGen;
    private readonly MapLayerLandformsSmooth landformsGen;
    private readonly MapLayerBase climateGen;
    private readonly List<ForceLandform> forcedLandforms = [];
    private readonly NormalizedSimplexNoise forceNoiseX;
    private readonly NormalizedSimplexNoise forceNoiseZ;
    private readonly float[] landformBaseY;
    private readonly float oceanicityFac;

    public LandformsWorldProperty Landforms => LandformHeightNoise.landforms;
    public IReadOnlyList<StoryLocation> StoryLocations { get; }
    public WorldGenConfig Config { get; }
    public WorldSettings World => world;

    public MapGenerator(WorldSettings world, WorldGenConfig config, AssetSource assets, IReadOnlyList<StoryLocation> storyLocations)
    {
        this.world = world;
        Config = config;
        StoryLocations = storyLocations;
        api = FakeServerApi.Create(world, assets);

        // GenMaps.initWorldGen with TerraPrety's patches applied
        long seed = world.Seed;
        int noiseSizeOcean = world.RegionSize / TerraGenConfig.oceanMapScale;
        var noiseClimate = CreateClimateNoise(seed);

        // Spawn: GenMaps.ForceRandomLandArea, replaced by TerraPrety with a single forced voronoi point
        var requireLandAt = new List<XZ>();
        void ForceLandAt(int posX, int posZ)
        {
            double factor = noiseSizeOcean / (double)world.RegionSize;
            requireLandAt.Add(new XZ((int)(posX * factor), (int)(posZ * factor)));
        }
        ForceLandAt((world.MapSizeX + world.ChunkSize) / 2, (world.MapSizeZ + world.ChunkSize) / 2);

        climateGen = GenMaps.GetClimateMapGen(seed + 1, noiseClimate);
        oceanGen = new MapLayerOceansSmooth(seed + 1873, config, requireLandAt);
        landformsGen = new MapLayerLandformsSmooth(seed + 4 + 12, noiseClimate, api, world.GetFloat("landformScale", 1f), config);

        forceNoiseX = NormalizedSimplexNoise.FromDefaultOctaves(2, 1f / (2f * TerraGenConfig.landformMapScale), 0.9f, seed + 2);
        forceNoiseZ = NormalizedSimplexNoise.FromDefaultOctaves(2, 1f / (2f * TerraGenConfig.landformMapScale), 0.9f, seed + 1231296);

        // GenStoryStructures.SetupForceLandform -> GenMaps.ForceLandformAt (patched by TerraPrety to also force the height)
        foreach (var loc in storyLocations.Where(l => l.RequireLandform != null))
        {
            FindLandformIndex(loc.RequireLandform); // The game throws on unknown codes too.
            var forced = StoryStructures.ToForceLandform(loc);
            forcedLandforms.Add(forced);
            landformsGen.AddForcedLandform(forced);
            ForceLandAt(loc.X, loc.Z);
        }

        // The forced height points are read lazily on the first GenLayer call, which isn't thread safe; do it now.
        landformsGen.GenLayer(0, 0, 1, 1);

        // TerraPrety patches GenTerra's oceanicity factor to use (MapSizeY - 64); note the integer division, as in the game.
        oceanicityFac = ((world.MapSizeY - WorldSettings.NumBlocksLowerWorldBy) / 256) * 0.33333f;
        landformBaseY = Landforms.LandFormsByIndex.Select(lf => BaseY(lf, world.MapSizeY - WorldSettings.NumBlocksLowerWorldBy)).ToArray();
    }

    public static WorldGenConfig LoadConfig(string path)
    {
        if (path == null) return new WorldGenConfig();
        return JsonConvert.DeserializeObject<WorldGenConfig>(File.ReadAllText(path)) ?? new WorldGenConfig();
    }

    private NoiseClimate CreateClimateNoise(long seed)
    {
        NoiseClimate noise;
        if (world.Get("worldClimate", "realistic") == "realistic")
        {
            var (minTemp, maxTemp) = world.Get("startingClimate", "temperate") switch
            {
                "hot" => (28, 32),
                "warm" => (19, 23),
                "cool" => (-5, 1),
                "icy" => (-15, -10),
                _ => (6, 14),
            };
            var realistic = new NoiseClimateRealistic(seed, (double)world.MapSizeZ / TerraGenConfig.climateMapScale / TerraGenConfig.climateMapSubScale,
                world.GetInt("polarEquatorDistance", 50000), minTemp, maxTemp);
            realistic.GeologicActivityStrength = world.GetFloat("geologicActivity", 0.05f);
            noise = realistic;
        }
        else
        {
            noise = new NoiseClimatePatchy(seed);
        }
        noise.rainMul = world.GetFloat("globalPrecipitation", 1f);
        noise.tempMul = world.GetFloat("globalTemperature", 1f);
        return noise;
    }

    private int FindLandformIndex(string code)
    {
        var list = Landforms.LandFormsByIndex;
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i].Code.Path == code) return i;
        }
        throw new ArgumentException("No landform with code " + code + " found.");
    }

    // Approximate surface height of a landform: the Y where its terrain threshold curve crosses 50%,
    // i.e. where the 3D terrain noise is solid about half the time.
    private static float BaseY(LandformVariant lf, int effectiveHeight)
    {
        var pos = lf.TerrainYKeyPositions;
        var th = lf.TerrainYKeyThresholds;
        for (int i = 1; i < pos.Length; i++)
        {
            if (th[i - 1] >= 0.5f && th[i] < 0.5f)
            {
                float t = (th[i - 1] - 0.5f) / (th[i - 1] - th[i]);
                return (pos[i - 1] + t * (pos[i] - pos[i - 1])) * effectiveHeight;
            }
        }
        return (th.Length > 0 && th[^1] >= 0.5f ? pos[^1] : pos[0]) * effectiveHeight;
    }

    public float EstimateSurfaceY(int landformIndex, int oceanicity) => landformBaseY[landformIndex] - oceanicity * oceanicityFac;

    // Absolute block coordinates. Must not be called concurrently: several of the noise classes keep per-instance state.
    public MapSample Sample(int blockX, int blockZ)
    {
        var s = new MapSample();
        s.Oceanicity = oceanGen.GetOceanicityAt(blockX / TerraGenConfig.oceanMapScale, blockZ / TerraGenConfig.oceanMapScale);

        int lx = blockX / TerraGenConfig.landformMapScale;
        int lz = blockZ / TerraGenConfig.landformMapScale;
        s.LandformIndex = landformsGen.GenLayer(lx, lz, 1, 1)[0];
        s.LandformHeight = LandformHeightNoise.heightMapValues[0] / (double)LandformHeightNoise.significantDigitMult;
        s.MountainRange = LandformHeightNoise.mountainRangeMapValues[0];
        s.Uplift = LandformHeightNoise.upliftMapValues[0];
        s.LandformIndex = ApplyForcedLandforms(lx, lz, s.LandformIndex);

        s.Climate = ClimateAt(blockX, blockZ);
        s.SurfaceY = EstimateSurfaceY(s.LandformIndex, s.Oceanicity);
        return s;
    }

    // The vanilla climate layer interpolates relative to the requested window and the game always requests whole
    // (padded) regions, so values only match the game when taken from a region sized map.
    private readonly Dictionary<long, int[]> climateRegions = [];

    private int ClimateAt(int blockX, int blockZ)
    {
        int rx = blockX / world.RegionSize, rz = blockZ / world.RegionSize;
        long key = ((long)rx << 32) | (uint)rz;
        if (!climateRegions.TryGetValue(key, out var map))
        {
            if (climateRegions.Count > 4096) climateRegions.Clear();
            climateRegions[key] = map = RegionClimateMap(rx, rz);
        }

        int size = world.RegionSize / TerraGenConfig.climateMapScale, pad = 2;
        int tx = blockX % world.RegionSize / TerraGenConfig.climateMapScale, tz = blockZ % world.RegionSize / TerraGenConfig.climateMapScale;
        return map[(tz + pad) * (size + 2 * pad) + tx + pad];
    }

    // Full region maps exactly as GenMaps.OnMapRegionGen lays them out (padding included), for --verify.
    public int[] RegionOceanMap(int regionX, int regionZ)
    {
        int size = world.RegionSize / TerraGenConfig.oceanMapScale, pad = 5;
        return oceanGen.GenLayer(regionX * size - pad, regionZ * size - pad, size + 2 * pad, size + 2 * pad);
    }

    public int[] RegionLandformMap(int regionX, int regionZ, out int[] heightMap)
    {
        int size = world.RegionSize / TerraGenConfig.landformMapScale, pad = TerraGenConfig.landformMapPadding;
        int full = size + 2 * pad;
        var data = landformsGen.GenLayer(regionX * size - pad, regionZ * size - pad, full, full);
        heightMap = (int[])LandformHeightNoise.heightMapValues.Clone();

        for (int i = 0; i < full; i++)
        {
            for (int j = 0; j < full; j++)
            {
                data[j * full + i] = ApplyForcedLandforms(regionX * size - pad + i, regionZ * size - pad + j, data[j * full + i]);
            }
        }
        return data;
    }

    public int[] RegionClimateMap(int regionX, int regionZ)
    {
        int size = world.RegionSize / TerraGenConfig.climateMapScale, pad = 2;
        return climateGen.GenLayer(regionX * size - pad, regionZ * size - pad, size + 2 * pad, size + 2 * pad);
    }

    // GenMaps.forceLandform stamps the required landform over story structure areas (with a little noise wobble on the edge).
    // Re-expressed per landform map tile so it can be applied to single samples, not just whole regions.
    private int ApplyForcedLandforms(int lx, int lz, int current)
    {
        if (forcedLandforms.Count == 0) return current;

        int regionTiles = world.RegionSize / TerraGenConfig.landformMapScale;
        float wobble = 80f / world.RegionSize * regionTiles;
        foreach (var fl in forcedLandforms)
        {
            double cx = fl.CenterPos.X / (double)TerraGenConfig.landformMapScale;
            double cz = fl.CenterPos.Z / (double)TerraGenConfig.landformMapScale;
            double r = fl.Radius / (double)TerraGenConfig.landformMapScale;
            if (Math.Abs(lx - cx) > r + wobble + 2 || Math.Abs(lz - cz) > r + wobble + 2) continue;

            // The game iterates source tiles and writes them shifted by the wobble noise; search the few source tiles that can land here.
            int reach = (int)Math.Ceiling(wobble) + 1;
            for (int sx = lx - reach; sx <= lx + reach; sx++)
            {
                for (int sz = lz - reach; sz <= lz + reach; sz++)
                {
                    if ((sx - cx) * (sx - cx) + (sz - cz) * (sz - cz) >= r * r) continue;
                    int tx = sx + (int)(wobble * forceNoiseX.Noise(sx, sz));
                    int tz = sz + (int)(wobble * forceNoiseZ.Noise(sx, sz));
                    if (tx == lx && tz == lz) return FindLandformIndex(fl.LandformCode);
                }
            }
        }
        return current;
    }
}
