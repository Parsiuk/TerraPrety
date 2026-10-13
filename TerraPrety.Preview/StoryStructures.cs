using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;

namespace TerraPrety.Preview;

// Story structures force land (and a landform height) around themselves, which noticeably shapes TerraPrety's continents.
public record StoryLocation(string Code, int X, int Z, int Radius, string RequireLandform);

public static class StoryStructures
{
    private record Config(string Code, string DependsOn, int MinX, int MaxX, int MinZ, int MaxZ, int Radius, string RequireLandform);

    // Locations recorded in the savegame are authoritative (they also reflect /wgen story setpos edits).
    public static List<StoryLocation> FromSave(SaveFile save, AssetSource assets)
    {
        var stored = save.SaveGame.GetData<Vintagestory.API.Datastructures.OrderedDictionary<string, StoryStructureLocation>>("storystructurelocations");
        if (stored == null) return null;

        var configs = LoadConfigs(assets, 1).ToDictionary(c => c.Code);
        var result = new List<StoryLocation>();
        foreach (var (code, loc) in stored)
        {
            configs.TryGetValue(code, out var cfg);
            result.Add(new StoryLocation(code, loc.CenterPos.X, loc.CenterPos.Z, cfg?.Radius ?? loc.LandformRadius, cfg?.RequireLandform));
        }
        return result;
    }

    // Same placement as GenStoryStructures.TryAddStoryLocation for a fresh world.
    public static List<StoryLocation> Compute(WorldSettings world, AssetSource assets)
    {
        if (!world.Get("loreContent", "true").Equals("true", StringComparison.OrdinalIgnoreCase)) return [];

        double distScaling = world.GetFloat("storyStructuresDistScaling", 1f);
        var rand = new LCGRandom(world.Seed + 1095);
        var placed = new List<(StoryLocation Loc, int DirX)>();

        int index = 0;
        foreach (var cfg in LoadConfigs(assets, distScaling))
        {
            rand.SetWorldSeed(world.Seed + 1095 + index);
            index++;

            int baseX, baseZ;
            int? dirX = null;
            if (cfg.DependsOn == "spawn")
            {
                baseX = world.MapSizeX / 2;
                baseZ = world.MapSizeZ / 2;
            }
            else
            {
                var parent = placed.FirstOrDefault(p => p.Loc.Code == cfg.DependsOn);
                if (parent.Loc == null) continue; // The game logs an error and skips it as well.
                baseX = parent.Loc.X;
                baseZ = parent.Loc.Z;
                dirX = parent.DirX;
            }

            int dx = dirX ?? (rand.NextFloat() > 0.5 ? -1 : 1);
            int dz = rand.NextFloat() > 0.5 ? -1 : 1;
            int distX = cfg.MinX + rand.NextInt(cfg.MaxX + 1 - cfg.MinX);
            int distZ = cfg.MinZ + rand.NextInt(cfg.MaxZ + 1 - cfg.MinZ);

            var loc = new StoryLocation(cfg.Code, baseX + distX * dx, baseZ + distZ * dz, cfg.Radius, cfg.RequireLandform);
            placed.Add((loc, dx));
        }
        return placed.Select(p => p.Loc).ToList();
    }

    private static IEnumerable<Config> LoadConfigs(AssetSource assets, double distScaling)
    {
        var json = AssetSource.ParseLenient(assets.Get(new AssetLocation("game:worldgen/storystructures.json")).ToText());
        foreach (var s in json["structures"].OfType<JObject>())
        {
            int Get(string key) => (int)((int?)s.GetValue(key, StringComparison.OrdinalIgnoreCase) ?? 0);
            yield return new Config(
                (string)s.GetValue("code", StringComparison.OrdinalIgnoreCase),
                (string)s.GetValue("dependsOnStructure", StringComparison.OrdinalIgnoreCase),
                (int)(Get("minSpawnDistX") * distScaling), (int)(Get("maxSpawnDistX") * distScaling),
                (int)(Get("minSpawnDistZ") * distScaling), (int)(Get("maxSpawnDistZ") * distScaling),
                Get("landformRadius"),
                (string)s.GetValue("requireLandform", StringComparison.OrdinalIgnoreCase));
        }
    }

    public static ForceLandform ToForceLandform(StoryLocation loc) => new()
    {
        CenterPos = new BlockPos(loc.X, 0, loc.Z, 0),
        Radius = loc.Radius,
        LandformCode = loc.RequireLandform,
    };
}
