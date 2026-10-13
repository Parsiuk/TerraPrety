using System;
using System.Collections.Generic;
using System.Reflection;
using Func = System.Func<object[], object>;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace TerraPrety.Preview;

// The mod's map layers only touch a handful of ICoreServerAPI members (world sizes and two asset lookups).
// Rather than implementing those huge interfaces, answer the used members from a lookup and fail loudly on anything else,
// so a new dependency in the mod shows up as a clear error instead of silently wrong output.
public class FakeApiProxy : DispatchProxy
{
    private string interfaceName;
    private Dictionary<string, Func> members;

    public static T Create<T>(Dictionary<string, Func> members) where T : class
    {
        var proxy = DispatchProxy.Create<T, FakeApiProxy>();
        var self = (FakeApiProxy)(object)proxy;
        self.interfaceName = typeof(T).Name;
        self.members = members;
        return proxy;
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (members.TryGetValue(targetMethod.Name, out var handler)) return handler(args);
        throw new NotSupportedException($"The preview tool does not emulate {interfaceName}.{targetMethod.Name}. The mod's map gen now needs it; add it to FakeServerApi.");
    }
}

public static class FakeServerApi
{
    public static ICoreServerAPI Create(WorldSettings world, AssetSource assets)
    {
        var worldManager = FakeApiProxy.Create<IWorldManagerAPI>(new()
        {
            ["get_MapSizeX"] = _ => world.MapSizeX,
            ["get_MapSizeY"] = _ => world.MapSizeY,
            ["get_MapSizeZ"] = _ => world.MapSizeZ,
            ["get_RegionSize"] = _ => world.RegionSize,
            ["get_ChunkSize"] = _ => world.ChunkSize,
            ["get_Seed"] = _ => (long)world.Seed,
        });

        var assetManager = FakeApiProxy.Create<IAssetManager>(new()
        {
            ["Get"] = args => assets.Get(args[0] as AssetLocation ?? new AssetLocation((string)args[0])),
            ["TryGet"] = args => assets.TryGet(args[0] as AssetLocation ?? new AssetLocation((string)args[0])),
        });

        return FakeApiProxy.Create<ICoreServerAPI>(new()
        {
            ["get_WorldManager"] = _ => worldManager,
            ["get_Assets"] = _ => assetManager,
            ["get_Side"] = _ => EnumAppSide.Server,
        });
    }
}
