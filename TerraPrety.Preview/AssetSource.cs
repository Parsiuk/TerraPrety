using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.Common;

namespace TerraPrety.Preview;

// Minimal stand-in for the game's asset manager: game assets from the install folder, mod assets from the repo
// (or a released mod zip), with the mod's JSON patches applied the same way the game does on load.
public class AssetSource
{
    private static readonly string[] GameDomainFolders = ["survival", "game", "creative"];

    private readonly string gameAssetsDir;
    private readonly string modAssetsPath;
    private readonly ZipArchive modZip;
    private readonly List<JObject> modPatches = [];

    public List<string> Log { get; } = [];

    public AssetSource(string gameDir, string modAssetsPath)
    {
        gameAssetsDir = Path.Combine(gameDir, "assets");
        this.modAssetsPath = modAssetsPath;

        if (modAssetsPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            modZip = ZipFile.OpenRead(modAssetsPath);
        }

        foreach (var (name, text) in ModFiles("terraprety/patches/"))
        {
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            var token = ParseLenient(text);
            var patches = token is JArray arr ? arr.OfType<JObject>() : [(JObject)token];
            modPatches.AddRange(patches);
        }
    }

    public IAsset Get(AssetLocation location) =>
        TryGet(location) ?? throw new FileNotFoundException("Asset not found: " + location);

    public IAsset TryGet(AssetLocation location)
    {
        var text = ReadRaw(location);
        if (text == null) return null;

        var patched = ApplyPatches(location, text);
        return new Asset(Encoding.UTF8.GetBytes(patched), location, null);
    }

    private string ReadRaw(AssetLocation location)
    {
        if (location.Domain == "game")
        {
            foreach (var folder in GameDomainFolders)
            {
                var path = Path.Combine(gameAssetsDir, folder, location.Path);
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            return null;
        }

        var relative = location.Domain + "/" + location.Path;
        return ModFiles(relative).Select(f => f.Text).FirstOrDefault();
    }

    // Yields (path relative to the assets root, content) for mod asset files under the given prefix.
    private IEnumerable<(string Name, string Text)> ModFiles(string prefix)
    {
        if (modZip != null)
        {
            foreach (var entry in modZip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith("assets/" + prefix, StringComparison.OrdinalIgnoreCase) || name.EndsWith('/')) continue;
                using var reader = new StreamReader(entry.Open());
                yield return (name["assets/".Length..], reader.ReadToEnd());
            }
            yield break;
        }

        var target = Path.Combine(modAssetsPath, prefix);
        if (File.Exists(target))
        {
            yield return (prefix, File.ReadAllText(target));
        }
        else if (Directory.Exists(target))
        {
            foreach (var file in Directory.GetFiles(target).Order())
            {
                yield return (prefix + Path.GetFileName(file), File.ReadAllText(file));
            }
        }
    }

    private string ApplyPatches(AssetLocation location, string text)
    {
        var matching = modPatches.Where(p => PatchTargets(p, location)).ToList();
        if (matching.Count == 0) return text;

        var root = ParseLenient(text);
        foreach (var patch in matching)
        {
            root = ApplyPatch(root, patch, location);
        }
        return root.ToString(Formatting.None);
    }

    private static bool PatchTargets(JObject patch, AssetLocation location)
    {
        var file = (string)patch["file"];
        if (file == null) return false;
        if (patch["enabled"]?.Type == JTokenType.Boolean && !(bool)patch["enabled"]) return false;
        if ((string)patch["side"] is string side && !side.Equals("server", StringComparison.OrdinalIgnoreCase) && !side.Equals("universal", StringComparison.OrdinalIgnoreCase)) return false;

        var target = new AssetLocation(file.EndsWith(".json") ? file : file + ".json");
        return target.Domain == location.Domain && target.Path == location.Path;
    }

    private JToken ApplyPatch(JToken root, JObject patch, AssetLocation location)
    {
        var op = ((string)patch["op"])?.ToLowerInvariant();
        var path = (string)patch["path"] ?? "";
        var value = patch["value"];

        if (patch["dependsOn"] is JArray { Count: > 0 })
        {
            Log.Add($"Skipped patch {op} {path} on {location}: it depends on other mods.");
            return root;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Replace("~1", "/").Replace("~0", "~")).ToArray();
        if (segments.Length == 0)
        {
            if (op is "replace" or "add") return value.DeepClone();
            throw new NotSupportedException($"Patch op '{op}' on the document root is not supported.");
        }

        var parent = root;
        foreach (var segment in segments[..^1])
        {
            parent = parent is JArray a ? a[int.Parse(segment)] : parent[segment];
            if (parent == null)
            {
                Log.Add($"Skipped patch {op} {path} on {location}: path does not exist.");
                return root;
            }
        }

        var last = segments[^1];
        switch (op)
        {
            case "replace":
            case "add":
                if (parent is JArray array)
                {
                    if (last == "-") array.Add(value.DeepClone());
                    else if (op == "add") array.Insert(int.Parse(last), value.DeepClone());
                    else array[int.Parse(last)] = value.DeepClone();
                }
                else
                {
                    parent[last] = value.DeepClone();
                }
                break;
            case "remove":
                if (parent is JArray removeArray) removeArray.RemoveAt(int.Parse(last));
                else ((JObject)parent).Remove(last);
                break;
            default:
                Log.Add($"Skipped patch {op} {path} on {location}: op not supported by the preview tool.");
                break;
        }
        return root;
    }

    // Game JSON allows comments, trailing commas and unquoted keys; Newtonsoft's JToken.Parse copes with all of those.
    public static JToken ParseLenient(string text) => JToken.Parse(text, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
}
