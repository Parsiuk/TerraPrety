using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ProtoBuf;
using Vintagestory.API.Datastructures;

namespace TerraPrety.Preview;

// A static translocator the game placed in the world. X, Y, Z are absolute block coordinates; Link is where a repaired
// one sends players, if it has been connected.
public record Translocator(int X, int Y, int Z, bool Repaired, (int X, int Y, int Z)? Link);

public static class Translocators
{
    private const int ChunkSize = 32;

    // The fields of ServerChunk this needs; protobuf-net skips the others. ServerChunk.FromBytes would want a chunk data
    // pool and a world to resolve block entities against.
    [ProtoContract]
    private class ChunkContents
    {
        [ProtoMember(1)] public byte[] BlocksCompressed { get; set; }
        [ProtoMember(3)] public byte[] LightSatCompressed { get; set; }
        [ProtoMember(8)] public List<byte[]> BlockEntitiesSerialized { get; set; }
        [ProtoMember(15)] public int SavedCompressionVersion { get; set; }
    }

    private sealed class Chunk
    {
        public int[] Blocks;
        public List<byte[]> BlockEntities;
    }

    private static readonly MethodInfo UnpackBlocksTo =
        typeof(Vintagestory.Common.ChunkData).GetMethod("UnpackBlocksTo", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("Vintagestory.Common.ChunkData.UnpackBlocksTo not found; the game's chunk format changed.");

    // Finds translocators the way BlockEntityStaticTranslocator.HasExitPoint does: every structure whose code contains
    // "gates" (the translocator rooms in worldgen/structures.json) is searched for a statictranslocator block. Broken
    // translocators have no block entity, so the chunk's block data is decoded; repaired ones also have a block entity
    // that holds their exit.
    public static List<Translocator> FromSave(SaveFile save, out int structuresWithout)
    {
        structuresWithout = 0;
        var result = new List<Translocator>();
        var stateById = TranslocatorBlockIds(save);
        if (stateById.Count == 0) return result;

        var chunks = new Dictionary<long, Chunk>();
        var found = new HashSet<(int, int, int)>();

        foreach (var (rx, rz) in save.RegionPositions())
        {
            var region = save.GetRegion(rx, rz);
            if (region?.GeneratedStructures == null) continue;

            foreach (var structure in region.GeneratedStructures)
            {
                if (structure?.Code == null || !structure.Code.Contains("gates") || structure.Location == null) continue;
                var loc = structure.Location;
                bool any = false;

                for (int y = Math.Max(0, loc.Y1); y <= loc.Y2; y++)
                {
                    for (int z = loc.Z1; z <= loc.Z2; z++)
                    {
                        for (int x = loc.X1; x <= loc.X2; x++)
                        {
                            var chunk = GetChunk(save, chunks, x / ChunkSize, y / ChunkSize, z / ChunkSize);
                            if (chunk == null) continue;
                            int id = chunk.Blocks[((y % ChunkSize) * ChunkSize + z % ChunkSize) * ChunkSize + x % ChunkSize];
                            if (!stateById.TryGetValue(id, out var repaired)) continue;
                            any = true;
                            if (!found.Add((x, y, z))) continue;
                            result.Add(new Translocator(x, y, z, repaired, repaired ? ReadLink(chunk, x, y, z) : null));
                        }
                    }
                }
                if (!any) structuresWithout++;
            }
        }
        return result;
    }

    // Block ids of statictranslocator-{normal|broken|unrepairable}-{side} in this save, and whether the block is a
    // repaired one (only *-normal-* has a block entity and can teleport).
    private static Dictionary<int, bool> TranslocatorBlockIds(SaveFile save)
    {
        var data = save.SaveGame.GetData("BlockIDs");
        if (data == null) return [];
        var codes = Serializer.Deserialize<Dictionary<int, string>>(new MemoryStream(data));
        var ids = new Dictionary<int, bool>();
        foreach (var (id, code) in codes)
        {
            if (code == null) continue;
            int colon = code.IndexOf(':');
            if (colon >= 0 && code[..colon] != "game") continue;
            var path = code[(colon + 1)..];
            if (!path.StartsWith("statictranslocator-")) continue;
            ids[id] = path.Split('-') is { Length: >= 2 } parts && parts[1] == "normal";
        }
        return ids;
    }

    private static Chunk GetChunk(SaveFile save, Dictionary<long, Chunk> cache, int cx, int cy, int cz)
    {
        long key = SaveFile.ChunkKey(cx, cy, cz);
        if (cache.TryGetValue(key, out var chunk)) return chunk;

        var data = save.GetChunk(cx, cy, cz);
        if (data != null)
        {
            var contents = Serializer.Deserialize<ChunkContents>(new MemoryStream(data));
            var blocks = new int[ChunkSize * ChunkSize * ChunkSize];
            if (contents.BlocksCompressed != null)
            {
                UnpackBlocksTo.Invoke(null, [blocks, contents.BlocksCompressed, contents.LightSatCompressed, contents.SavedCompressionVersion]);
            }
            chunk = new Chunk { Blocks = blocks, BlockEntities = contents.BlockEntitiesSerialized ?? [] };
        }
        return cache[key] = chunk;
    }

    // ServerChunk stores each block entity as its class name followed by its tree attributes.
    // BlockEntityStaticTranslocator keeps its exit in teleX/Y/Z when canTele is set; 0,0 means none.
    private static (int, int, int)? ReadLink(Chunk chunk, int x, int y, int z)
    {
        foreach (var bytes in chunk.BlockEntities)
        {
            try
            {
                using var reader = new BinaryReader(new MemoryStream(bytes));
                if (reader.ReadString() != "StaticTranslocator") continue;
                var tree = new TreeAttribute();
                tree.FromBytes(reader);
                if (tree.GetInt("posx") != x || tree.GetInt("posy") != y || tree.GetInt("posz") != z) continue;
                if (!tree.GetBool("canTele")) return null;

                int tx = tree.GetInt("teleX"), ty = tree.GetInt("teleY"), tz = tree.GetInt("teleZ");
                if (tx == 0 && tz == 0) return null;
                return tree.GetBool("tpLocationIsOffset") ? (x + tx, y + ty, z + tz) : (tx, ty, tz);
            }
            catch (Exception e) when (e is EndOfStreamException or IOException or InvalidDataException)
            {
                // Corrupted block entity data; the game discards it as well.
            }
        }
        return null;
    }
}
