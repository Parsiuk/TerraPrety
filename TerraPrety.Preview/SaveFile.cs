using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using Vintagestory.Server;

namespace TerraPrety.Preview;

// Read-only access to a .vcdbs savegame: world settings and the map regions the game already generated.
public sealed class SaveFile : IDisposable
{
    private readonly SqliteConnection connection;

    public SaveGame SaveGame { get; }

    public SaveFile(string path)
    {
        // immutable=1 so a running game holding the file is never disturbed and no -wal/-shm files get created.
        var uri = "file:" + Uri.EscapeDataString(System.IO.Path.GetFullPath(path)).Replace("%2F", "/") + "?mode=ro&immutable=1";
        connection = new SqliteConnection("Data Source=" + uri);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT data FROM gamedata LIMIT 1";
        var data = (byte[])cmd.ExecuteScalar() ?? throw new InvalidOperationException("Savegame has no gamedata row: " + path);

        SaveGame = SerializerUtil.Deserialize<SaveGame>(data);
        SaveGame.LoadWorldConfig();
    }

    public WorldSettings ToWorldSettings()
    {
        var settings = new WorldSettings
        {
            Seed = SaveGame.Seed,
            MapSizeX = SaveGame.MapSizeX,
            MapSizeY = SaveGame.MapSizeY,
            MapSizeZ = SaveGame.MapSizeZ,
        };

        foreach (var (key, value) in SaveGame.WorldConfiguration)
        {
            settings.WorldConfig[key] = value switch
            {
                StringAttribute s => s.value,
                _ => value?.ToString(),
            };
        }
        return settings;
    }

    // Position key used by the game for map regions: x | z << 27 (y and dimension are always 0).
    public static long RegionKey(int regionX, int regionZ) => (long)((uint)regionX | ((ulong)regionZ << 27));

    public static (int X, int Z) FromRegionKey(long key) => ((int)(key & ((1 << 27) - 1)), (int)((key >> 27) & ((1 << 27) - 1)));

    public IEnumerable<(int X, int Z)> RegionPositions()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT position FROM mapregion";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return FromRegionKey(reader.GetInt64(0));
    }

    public ServerMapRegion GetRegion(int regionX, int regionZ)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT data FROM mapregion WHERE position = $pos";
        cmd.Parameters.AddWithValue("$pos", RegionKey(regionX, regionZ));
        return cmd.ExecuteScalar() is byte[] data ? ServerMapRegion.FromBytes(data) : null;
    }

    // (chunkX, chunkZ, terrain height per column) for every map chunk the game generated.
    public IEnumerable<(int X, int Z, ushort[] Heights)> MapChunkHeights()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT position, data FROM mapchunk";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var (x, z) = FromRegionKey(reader.GetInt64(0));
            var chunk = ServerMapChunk.FromBytes((byte[])reader.GetValue(1));
            if (chunk.WorldGenTerrainHeightMap is { Length: > 0 } heights) yield return (x, z, heights);
        }
    }

    public void Dispose() => connection.Dispose();
}
