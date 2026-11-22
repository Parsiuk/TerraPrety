using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace TerraPrety.Preview;

// The game assemblies are not copied next to the tool, so resolve them (and their native libraries) from the game install.
// Must be registered before any game type is touched, which is why Program.Main does nothing else before calling it.
public static class GameAssemblies
{
    public static string GameDir { get; private set; }

    public static void Register(string gameDir)
    {
        GameDir = gameDir;
        string[] searchDirs = [gameDir, Path.Combine(gameDir, "Lib"), Path.Combine(gameDir, "Mods")];

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var dir in searchDirs)
            {
                var path = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, name) =>
        {
            foreach (var candidate in new[] { name, "lib" + name + ".so", "lib" + name + ".dylib", name + ".dll" })
            {
                var path = Path.Combine(gameDir, "Lib", candidate);
                if (File.Exists(path)) return NativeLibrary.Load(path);
            }
            return IntPtr.Zero;
        };
    }

    // Default game folder: VINTAGE_STORY env var, else the folder the tool was built against.
    public static string DefaultGameDir()
    {
        var env = Environment.GetEnvironmentVariable("VINTAGE_STORY");
        if (!string.IsNullOrEmpty(env)) return env;

        foreach (var attr in typeof(GameAssemblies).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attr.Key == "GameDir") return attr.Value;
        }
        return null;
    }

    public static string BuildTimeModAssetsDir()
    {
        foreach (var attr in typeof(GameAssemblies).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attr.Key == "ModAssetsDir") return attr.Value;
        }
        return null;
    }
}
