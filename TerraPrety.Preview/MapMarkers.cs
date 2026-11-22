using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace TerraPrety.Preview;

// A waypoint a player put on their in-game map. X and Z are absolute block coordinates.
public record MapMarker(int X, int Z, string Icon, int Color, string Title, string Owner);

public static class MapMarkers
{
    // Read like WaypointMapLayer.OnSaveGameLoaded: protobuf under playerMapMarkers_v2, JSON under the old key.
    public static List<MapMarker> FromSave(SaveFile save)
    {
        List<Waypoint> waypoints = null;
        if (save.SaveGame.GetData("playerMapMarkers_v2") is { } data)
        {
            waypoints = SerializerUtil.Deserialize<List<Waypoint>>(data);
        }
        else if (save.SaveGame.GetData("playerMapMarkers") is { } legacy)
        {
            waypoints = JsonUtil.FromBytes<List<Waypoint>>(legacy);
        }

        return (waypoints ?? [])
            .Where(w => w?.Position != null && !w.Temporary)
            .Select(w => new MapMarker((int)Math.Floor(w.Position.X), (int)Math.Floor(w.Position.Z), w.Icon ?? "circle", w.Color, w.Title, w.OwningPlayerUid))
            .ToList();
    }
}
