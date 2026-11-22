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

// Minimal stand-in for the game's asset manager. Game assets come from the install folder; TerraPrety's from the repo
// (or a released zip); other mods' from their zips or folders. Like the game it remaps mods' compatibility/<modid>/
// folders and applies every mod's JSON patches (honoring dependsOn, condition and side), so other mods' changes to
// worldgen files, e.g. BetterRuins giving its story structures a required landform under TerraPrety, are seen too.
public sealed class AssetSource : IDisposable
{
    private static readonly string[] GameDomainFolders = ["survival", "game", "creative"];

    private readonly string gameAssetsDir;
    private readonly IReadOnlyDictionary<string, string> worldConfig;
    private readonly List<ModSource> mods = [];
    // "domain:path" -> reader, in load order. Later mods replace files of earlier ones, as in the game.
    private readonly Dictionary<string, Func<string>> modFiles = [];
    private readonly HashSet<string> loadedModIds = ["game", "survival", "creative"];
    private readonly List<(JObject Patch, string Source)> patches = [];

    public List<string> Log { get; } = [];
    public IReadOnlyList<ModSource> Mods => mods;

    public AssetSource(string gameDir, string terraPretyPath, IEnumerable<string> otherModPaths, IReadOnlyDictionary<string, string> worldConfig)
    {
        gameAssetsDir = Path.Combine(gameDir, "assets");
        this.worldConfig = worldConfig ?? new Dictionary<string, string>();

        var terraPrety = ModSource.Open(terraPretyPath) ?? throw new ArgumentException("Not a mod folder or zip: " + terraPretyPath);
        terraPrety.ModId ??= "terraprety";
        mods.Add(terraPrety);
        foreach (var path in otherModPaths ?? [])
        {
            var mod = ModSource.Open(path);
            if (mod == null || mod.ModId == terraPrety.ModId) continue; // The TerraPrety under test replaces any installed copy.
            mods.Add(mod);
        }
        foreach (var mod in mods.Where(m => m.ModId != null)) loadedModIds.Add(mod.ModId);

        // Mods load in file name order (good enough: only the order of files replacing each other depends on it).
        mods.Sort((a, b) => string.Compare(a.SortKey, b.SortKey, StringComparison.OrdinalIgnoreCase));

        foreach (var mod in mods)
        {
            foreach (var (domain, path, read) in mod.Files())
            {
                if (!path.StartsWith("compatibility/")) modFiles[domain + ":" + path] = read;
            }
        }

        // ModCompatiblityUtil: <anydomain>:compatibility/<modid>/<path> becomes <modid>:<path> when that mod is loaded.
        foreach (var mod in mods)
        {
            foreach (var (_, path, read) in mod.Files())
            {
                var parts = path.Split('/', 3);
                if (parts.Length == 3 && parts[0] == "compatibility" && loadedModIds.Contains(parts[1])) modFiles[parts[1] + ":" + parts[2]] = read;
            }
        }

        LoadPatches();
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

    // Like IAssetManager.GetMany for one exact path: that file from every domain that has it, game first.
    public List<IAsset> GetMany(string path)
    {
        var domains = new List<string> { "game" };
        foreach (var key in modFiles.Keys)
        {
            var domain = key[..key.IndexOf(':')];
            if (!domains.Contains(domain)) domains.Add(domain);
        }
        return domains.Select(d => TryGet(new AssetLocation(d, path))).Where(a => a != null).ToList();
    }

    private string ReadRaw(AssetLocation location)
    {
        if (modFiles.TryGetValue(location.Domain + ":" + location.Path, out var read)) return read();
        if (location.Domain != "game") return null;

        foreach (var folder in GameDomainFolders)
        {
            var path = Path.Combine(gameAssetsDir, folder, location.Path);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }

    private void LoadPatches()
    {
        var sources = new List<(string Name, Func<string> Read)>();
        foreach (var folder in GameDomainFolders)
        {
            var dir = Path.Combine(gameAssetsDir, folder, "patches");
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).Order())
            {
                sources.Add(("game:" + Path.GetRelativePath(Path.Combine(gameAssetsDir, folder), file).Replace('\\', '/'), () => File.ReadAllText(file)));
            }
        }
        foreach (var (key, read) in modFiles)
        {
            var path = key[(key.IndexOf(':') + 1)..];
            if (path.StartsWith("patches/") && path.EndsWith(".json")) sources.Add((key, read));
        }

        foreach (var (name, read) in sources)
        {
            JToken token;
            try
            {
                token = ParseLenient(read());
            }
            catch (JsonException e)
            {
                Log.Add($"Could not parse patch file {name}: {e.Message}");
                continue;
            }
            foreach (var patch in token is JArray arr ? arr.OfType<JObject>() : token is JObject obj ? [obj] : [])
            {
                if (IsActive(patch)) patches.Add((patch, name));
            }
        }
    }

    // ModJsonPatchLoader.ApplyPatches: enabled, side, condition and dependsOn.
    private bool IsActive(JObject patch)
    {
        if (Prop(patch, "file") == null) return false;
        if (Prop(patch, "enabled") is { Type: JTokenType.Boolean } enabled && !(bool)enabled) return false;
        if ((string)(Prop(patch, "side") ?? Prop(patch, "sideType")) is string side
            && !side.Equals("server", StringComparison.OrdinalIgnoreCase) && !side.Equals("universal", StringComparison.OrdinalIgnoreCase)) return false;

        if (Prop(patch, "condition") is JObject condition)
        {
            var when = (string)Prop(condition, "when");
            if (when == null || !worldConfig.TryGetValue(when, out var value)) return false;
            if (Prop(condition, "useValue") is { Type: JTokenType.Boolean } useValue && (bool)useValue)
            {
                patch["value"] = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? new JValue(d)
                    : bool.TryParse(value, out var b) ? new JValue(b) : new JValue(value);
            }
            else if (!string.Equals((string)Prop(condition, "isValue") ?? "", value ?? "", StringComparison.InvariantCultureIgnoreCase))
            {
                return false;
            }
        }

        if (Prop(patch, "dependsOn") is JArray dependsOn)
        {
            foreach (var dep in dependsOn.OfType<JObject>())
            {
                bool present = loadedModIds.Contains((string)Prop(dep, "modid") ?? "");
                bool invert = Prop(dep, "invert") is { Type: JTokenType.Boolean } inv && (bool)inv;
                if (present == invert) return false;
            }
        }
        return true;
    }

    private string ApplyPatches(AssetLocation location, string text)
    {
        var matching = patches.Where(p => PatchTargets(p.Patch, location)).ToList();
        if (matching.Count == 0) return text;

        var root = ParseLenient(text);
        foreach (var (patch, source) in matching)
        {
            root = ApplyPatch(root, patch, location, source);
        }
        return root.ToString(Formatting.None);
    }

    private static bool PatchTargets(JObject patch, AssetLocation location)
    {
        var file = (string)Prop(patch, "file");
        if (file.EndsWith('*'))
        {
            var prefix = new AssetLocation(file.TrimEnd('*'));
            return prefix.Domain == location.Domain && location.Path.StartsWith(prefix.Path);
        }
        var target = new AssetLocation(file.EndsWith(".json") ? file : file + ".json");
        return target.Domain == location.Domain && target.Path == location.Path;
    }

    private JToken ApplyPatch(JToken root, JObject patch, AssetLocation location, string source)
    {
        var op = ((string)Prop(patch, "op"))?.ToLowerInvariant();
        var path = (string)Prop(patch, "path") ?? "";
        var value = Prop(patch, "value");
        void Skip(string why) => Log.Add($"Skipped patch {op} {path} from {source} on {location}: {why}.");

        if (op is "copy" or "move")
        {
            var from = Resolve(root, (string)Prop(patch, "fromPath") ?? "");
            if (from == null) { Skip("fromPath does not exist"); return root; }
            value = from.DeepClone();
            if (op == "move")
            {
                if (from.Parent is JProperty prop) prop.Remove();
                else from.Remove();
            }
            op = "add";
        }

        var segments = Segments(path);
        if (segments.Length == 0)
        {
            if (op is "replace" or "add") return value.DeepClone();
            Skip("op on the document root is not supported");
            return root;
        }

        var parent = Resolve(root, segments[..^1]);
        if (parent == null) { Skip("path does not exist"); return root; }

        var last = segments[^1];
        switch (op)
        {
            case "add":
            case "addeach":
            case "addmerge":
                var values = op == "addeach" && value is JArray each ? each.Select(v => v.DeepClone()).ToList() : [value.DeepClone()];
                if (parent is JArray array)
                {
                    int at = last == "-" ? array.Count : int.Parse(last);
                    if (op == "addmerge" && last != "-" && at < array.Count && Merge(array[at], value)) break;
                    foreach (var v in values) array.Insert(at++, v);
                }
                else if (parent is JObject obj)
                {
                    if (op == "addmerge" && obj[last] is JToken existing && Merge(existing, value)) break;
                    obj[last] = values[0];
                }
                break;
            case "replace":
                if (parent is JArray replaceArray && int.TryParse(last, out var ri) && ri < replaceArray.Count) replaceArray[ri] = value.DeepClone();
                else if (parent is JObject replaceObj && replaceObj.ContainsKey(last)) replaceObj[last] = value.DeepClone();
                else Skip("path does not exist");
                break;
            case "remove":
                if (parent is JArray removeArray && int.TryParse(last, out var di) && di < removeArray.Count) removeArray.RemoveAt(di);
                else if (parent is JObject removeObj && removeObj.ContainsKey(last)) removeObj.Remove(last);
                else Skip("path does not exist");
                break;
            default:
                Skip("op not supported by the preview tool");
                break;
        }
        return root;
    }

    private static bool Merge(JToken target, JToken value)
    {
        if (target is JObject to && value is JObject vo) { to.Merge(vo); return true; }
        if (target is JArray ta && value is JArray va) { foreach (var v in va) ta.Add(v.DeepClone()); return true; }
        return false;
    }

    private static string[] Segments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Replace("~1", "/").Replace("~0", "~")).ToArray();

    private static JToken Resolve(JToken root, string path) => Resolve(root, Segments(path));

    private static JToken Resolve(JToken token, IEnumerable<string> segments)
    {
        foreach (var segment in segments)
        {
            token = token switch
            {
                JArray a => int.TryParse(segment, out var i) && i >= 0 && i < a.Count ? a[i] : null,
                JObject o => o[segment],
                _ => null,
            };
            if (token == null) return null;
        }
        return token;
    }

    // The game deserializes patches with case-insensitive property names ("dependsOn", "DependsOn", "fromPath"...).
    private static JToken Prop(JObject obj, string name) => obj.GetValue(name, StringComparison.OrdinalIgnoreCase);

    // Game JSON allows comments, trailing commas and unquoted keys; Newtonsoft's JToken.Parse copes with all of those.
    public static JToken ParseLenient(string text) => JToken.Parse(text, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });

    // Enabled mods in a mods folder, skipping those the game's mod manager has disabled (clientsettings.json).
    public static List<string> ModsInFolder(string modsDir, out List<string> disabled)
    {
        disabled = [];
        if (modsDir == null || !Directory.Exists(modsDir)) return [];

        var disabledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var settings = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(modsDir).TrimEnd('/')) ?? "", "clientsettings.json");
        if (File.Exists(settings))
        {
            try
            {
                var list = ParseLenient(File.ReadAllText(settings))["stringListSettings"]?["disabledMods"] as JArray;
                foreach (var entry in list ?? []) disabledIds.Add((string)entry);
            }
            catch (JsonException) { }
        }

        var result = new List<string>();
        foreach (var path in Directory.GetFileSystemEntries(modsDir).Order())
        {
            using var mod = ModSource.Open(path);
            if (mod == null) continue;
            if (mod.ModId != null && disabledIds.Contains(mod.ModId + "@" + mod.Version)) disabled.Add(mod.ModId);
            else result.Add(path);
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var mod in mods) mod.Dispose();
    }
}

// One mod's assets: a zip, a mod folder (with modinfo.json), or a bare assets folder (this repo's TerraPrety/assets).
public sealed class ModSource : IDisposable
{
    private readonly ZipArchive zip;
    private readonly string assetsDir;

    public string ModId;
    public string Version;
    public string Location { get; }
    public string SortKey => Path.GetFileName(Location.TrimEnd('/'));

    private ModSource(string location, ZipArchive zip, string assetsDir)
    {
        Location = location;
        this.zip = zip;
        this.assetsDir = assetsDir;
    }

    public static ModSource Open(string path)
    {
        ModSource mod;
        string modInfo = null;
        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var zip = ZipFile.OpenRead(path);
            mod = new ModSource(path, zip, null);
            if (zip.GetEntry("modinfo.json") is { } entry)
            {
                using var reader = new StreamReader(entry.Open());
                modInfo = reader.ReadToEnd();
            }
        }
        else if (Directory.Exists(Path.Combine(path, "assets")))
        {
            mod = new ModSource(path, null, Path.Combine(path, "assets"));
            var info = Path.Combine(path, "modinfo.json");
            if (File.Exists(info)) modInfo = File.ReadAllText(info);
        }
        else if (Directory.Exists(path) && Path.GetFileName(path.TrimEnd('/')) == "assets")
        {
            mod = new ModSource(path, null, path);
            var info = Path.Combine(path, "..", "modinfo.json");
            if (File.Exists(info)) modInfo = File.ReadAllText(info);
        }
        else
        {
            return null;
        }

        if (modInfo != null)
        {
            try
            {
                var json = (JObject)AssetSource.ParseLenient(modInfo);
                mod.ModId = ((string)json.GetValue("modid", StringComparison.OrdinalIgnoreCase))?.ToLowerInvariant();
                mod.Version = (string)json.GetValue("version", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException) { }
        }
        return mod;
    }

    // (domain, path within the domain, reader) for every asset file, with lowercase paths like AssetLocation uses.
    public IEnumerable<(string Domain, string Path, Func<string> Read)> Files()
    {
        if (zip != null)
        {
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) || name.EndsWith('/')) continue;
                var parts = name["assets/".Length..].Split('/', 2);
                if (parts.Length < 2) continue;
                var e = entry;
                yield return (parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant(), () =>
                {
                    lock (zip)
                    {
                        using var reader = new StreamReader(e.Open());
                        return reader.ReadToEnd();
                    }
                });
            }
            yield break;
        }

        foreach (var file in Directory.GetFiles(assetsDir, "*", SearchOption.AllDirectories).Order())
        {
            var parts = System.IO.Path.GetRelativePath(assetsDir, file).Replace('\\', '/').Split('/', 2);
            if (parts.Length < 2) continue;
            yield return (parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant(), () => File.ReadAllText(file));
        }
    }

    public override string ToString() => (ModId ?? SortKey) + (Version != null ? " " + Version : "");

    public void Dispose() => zip?.Dispose();
}
