using System;
using System.Runtime.CompilerServices;

namespace TerraPrety.Preview;

public static class Program
{
    public static int Main(string[] args)
    {
        var gameDir = Options.PeekGameDir(args) ?? GameAssemblies.DefaultGameDir();
        if (string.IsNullOrEmpty(gameDir) || !System.IO.File.Exists(System.IO.Path.Combine(gameDir, "VintagestoryAPI.dll")))
        {
            Console.Error.WriteLine("Vintage Story install not found. Pass --game <dir> or set VINTAGE_STORY.");
            return 2;
        }
        GameAssemblies.Register(gameDir);
        return Run(args);
    }

    // Kept out of Main so no game type is loaded before the assembly resolver is registered.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine("Run with --help for usage.");
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        return PreviewCommand.Execute(options);
    }
}
