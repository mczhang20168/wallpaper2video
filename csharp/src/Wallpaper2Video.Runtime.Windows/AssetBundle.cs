using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wallpaper2Video.Core;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

public sealed record EffectGraph(Dictionary<string, string> Files, Dictionary<string, JsonNode?> Defaults,
    Dictionary<string, JsonNode?> Combos, string Signature, string? Category);

public sealed class AssetBundle(ProjectSnapshot project, string? engine = null)
{
    private static readonly ConcurrentDictionary<string, Dictionary<string, List<string>>> Indexes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<JsonObject> Reviews = new(() =>
    {
        using var stream = typeof(AssetBundle).Assembly.GetManifestResourceStream("Wallpaper2Video.reviewed_effects.json")
            ?? throw new InvalidDataException("缺少效果签名规则资源");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });
    private readonly JsonObject? reviewedEffects;

    internal AssetBundle(ProjectSnapshot project, string? engine, JsonObject reviewedEffects) : this(project, engine)
    {
        this.reviewedEffects = reviewedEffects;
    }

    public string? Assets { get; } = FindAssets(project, engine);
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> BinaryFiles { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Stamps { get; } = new(StringComparer.Ordinal);

    internal bool TryGetSavedSliderValue(string name, out double value)
    {
        value = double.NaN;
        return project.Metadata.ValueKind == JsonValueKind.Object &&
            project.Metadata.TryGetProperty("general", out var general) && general.ValueKind == JsonValueKind.Object &&
            general.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object &&
            properties.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object &&
            property.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "slider" &&
            property.TryGetProperty("value", out var saved) && saved.ValueKind == JsonValueKind.Number &&
            saved.TryGetDouble(out value) && double.IsFinite(value);
    }

    public static string? FindAssets(ProjectSnapshot project, string? engine)
    {
        engine = string.IsNullOrWhiteSpace(engine) ? Environment.GetEnvironmentVariable("WPE_EXE") : engine;
        if (!string.IsNullOrWhiteSpace(engine)) return Path.Combine(Directory.Exists(engine) ? engine : Path.GetDirectoryName(Path.GetFullPath(engine))!, "assets");
        for (var path = new DirectoryInfo(project.Root); path != null; path = path.Parent)
            if (path.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return Path.Combine(path.FullName, "common", "wallpaper_engine", "assets");
        return null;
    }
    public string Read(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new InvalidDataException("素材依赖名称必须为非空路径字符串");
        name = ArchivePaths.Normalize(name);
        if (Files.TryGetValue(name, out string? cached)) return cached;
        byte[] data = ReadBinary(name, 16 * 1024 * 1024);
        string source = new UTF8Encoding(false, true).GetString(data);
        if (source.StartsWith('\uFEFF')) source = source[1..];
        source = source.Replace("\r", "");
        Files[name] = source;
        BinaryFiles.Remove(name);
        return source;
    }
    public byte[] ReadBinary(string name, int limit = 256 * 1024 * 1024)
    {
        name = ArchivePaths.Normalize(name);
        if (BinaryFiles.TryGetValue(name, out var cached)) { if (cached.Length > limit) throw new InvalidDataException("素材过大：" + name); return cached; }
        byte[] data = LoadBytes(name, limit, false);
        BinaryFiles[name] = data;
        Stamps[name] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
        return data;
    }
    public byte[] ReadTail(string name, int limit = 2 * 1024 * 1024)
    {
        name = ArchivePaths.Normalize(name);
        byte[] data = LoadBytes(name, limit, true);
        Stamps[name + "#timing-tail"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
        return data;
    }
    public (byte[] Data, long Length) ReadRange(string name, long offset, int maximum)
    {
        if (offset < 0 || maximum is < 1 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(offset));
        name = ArchivePaths.Normalize(name);
        (byte[] Data, long Length) result;
        if (BinaryFiles.TryGetValue(name, out var cached))
        {
            if (offset > cached.Length) throw new InvalidDataException("素材分段读取越界");
            result = (cached.AsSpan((int)offset, (int)Math.Min(maximum, cached.Length - offset)).ToArray(), cached.Length);
        }
        else
        {
            string path = SafeFiles.Member(project.Root, name);
            if (File.Exists(path)) result = FileRange(path);
            else if (project.PackagePath != null)
            {
                using var package = PackageReader.Open(project.PackagePath);
                if (package.Entries.Any(e => ArchivePaths.CaseFold(e.Name) == ArchivePaths.CaseFold(name))) result = package.ReadRange(name, offset, maximum);
                else result = Builtin();
            }
            else result = Builtin();
        }
        Stamps[$"{name}#range:{offset}:{maximum}"] = Hash(result.Length + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(result.Data)));
        return result;
        (byte[], long) FileRange(string path)
        {
            using var file = File.OpenRead(path); if (offset > file.Length) throw new InvalidDataException("素材分段读取越界");
            file.Position = offset; var data = new byte[(int)Math.Min(maximum, file.Length - offset)]; file.ReadExactly(data); return (data, file.Length);
        }
        (byte[], long) Builtin()
        {
            if (Assets == null) throw new InvalidDataException("无法定位效果依赖：" + name);
            string path = SafeFiles.Member(Assets, name); if (!File.Exists(path)) throw new InvalidDataException("无法定位效果依赖：" + name);
            return FileRange(path);
        }
    }
    private byte[] LoadBytes(string name, int limit, bool tail)
    {
        byte[]? data = null;
        string loose = SafeFiles.Member(project.Root, name);
        if (File.Exists(loose)) data = ReadFile(loose, limit, tail);
        else if (project.PackagePath != null)
        {
            using var package = PackageReader.Open(project.PackagePath);
            string folded = ArchivePaths.CaseFold(name);
            if (package.Entries.Any(e => ArchivePaths.CaseFold(e.Name) == folded)) data = tail ? package.ReadTail(name, limit) : package.Read(name, limit);
        }
        if (data == null && Assets != null)
        {
            string builtin = SafeFiles.Member(Assets, name);
            if (File.Exists(builtin)) data = ReadFile(builtin, limit, tail);
            else
            {
                var index = Indexes.GetOrAdd(Assets, BuildIndex);
                if (index.TryGetValue(ArchivePaths.CaseFold(name), out var candidates) && candidates.Count == 1) data = ReadFile(candidates[0], limit, tail);
            }
        }
        if (data == null) throw new InvalidDataException("无法定位效果依赖：" + name);
        if (data.Length > limit) throw new InvalidDataException("素材过大：" + name);
        return data;
    }
    private static byte[] ReadFile(string path, int limit, bool tail)
    {
        if (tail)
        {
            using var stream = File.OpenRead(path); int length = (int)Math.Min(stream.Length, limit);
            stream.Position = stream.Length - length; byte[] bytes = new byte[length]; stream.ReadExactly(bytes); return bytes;
        }
        if (new FileInfo(path).Length > limit) throw new InvalidDataException("素材过大：" + path);
        return File.ReadAllBytes(path);
    }
    private static Dictionary<string, List<string>> BuildIndex(string assets)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string root = Path.Combine(assets, "effects");
        if (!Directory.Exists(root)) return result;
        foreach (string effect in Directory.EnumerateDirectories(root))
        {
            var pending = new Stack<string>(); pending.Push(effect);
            while (pending.TryPop(out string? directory))
            {
                foreach (string child in Directory.EnumerateDirectories(directory))
                    if (Path.GetFileName(child) != "preview" && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    if (Path.GetExtension(file).ToLowerInvariant() is not (".json" or ".frag" or ".vert" or ".h" or ".js")) continue;
                    string key = ArchivePaths.CaseFold(Path.GetRelativePath(effect, file).Replace('\\', '/'));
                    if (!result.TryGetValue(key, out var list)) result[key] = list = [];
                    list.Add(file);
                }
            }
        }
        return result;
    }
    public EffectGraph Effect(string name)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var defaults = new Dictionary<string, JsonNode?>();
        var combos = new Dictionary<string, JsonNode?>();
        void Add(string member, int depth = 0)
        {
            if (files.ContainsKey(member)) return;
            if (depth > 128 || files.Count > 2048) throw new InvalidDataException("效果依赖过深或过多");
            string source = Read(member); files[member] = source;
            foreach (Match include in Regex.Matches(source, "^\\s*#include\\s+[\"<]([^\">]+)", RegexOptions.Multiline)) Add("shaders/" + include.Groups[1].Value, depth + 1);
        }
        Add(name);
        JsonObject definition = Parse(files[name]);
        foreach (var entry in Array(definition["passes"]))
        {
            string material = Text(entry?["material"]);
            if (material.Length == 0) continue;
            Add(material);
            foreach (var pass in Array(Parse(files[material])["passes"]))
            {
                string shader = Text(pass?["shader"]);
                if (shader.Length > 0) { Add("shaders/" + shader + ".vert"); Add("shaders/" + shader + ".frag"); }
            }
        }
        foreach (var dependency in Array(definition["dependencies"]))
        {
            string member = Text(dependency);
            if (Path.GetExtension(member) is ".vert" or ".frag" or ".h") Add(member);
        }
        foreach (var (member, source) in files)
        {
            if (Path.GetExtension(member) is not (".vert" or ".frag")) continue;
            foreach (Match match in Regex.Matches(source, @"\[COMBO\]\s*(\{[^\n]+\})"))
            {
                try { var meta = Parse(match.Groups[1].Value); if (meta["combo"] != null) combos[Text(meta["combo"])] = meta["default"]?.DeepClone() ?? JsonValue.Create(0); }
                catch (System.Text.Json.JsonException) { }
            }
            foreach (string line in source.Split('\n'))
            {
                if (!line.Contains("uniform ") || !line.Contains("//")) continue;
                try
                {
                    var meta = Parse(line[(line.IndexOf("//", StringComparison.Ordinal) + 2)..].Trim());
                    if (meta["material"] != null && meta.ContainsKey("default")) defaults[Text(meta["material"])] = meta["default"]?.DeepClone();
                }
                catch (System.Text.Json.JsonException) { }
            }
        }
        string signature = Hash(PythonStringMap(files));
        var reviews = reviewedEffects ?? Reviews.Value;
        return new(files, defaults, combos, signature, reviews[name] is JsonObject versions ? Text(versions[signature], null!) : null);
    }
    public bool ParticleMouse(string name, HashSet<string>? seen = null)
    {
        seen ??= [];
        if (seen.Count > 256) throw new InvalidDataException("粒子控制点依赖过深或过多");
        if (!seen.Add(name)) return false;
        var data = Parse(Read(name));
        if (Array(data["controlpoint"]).Any(cp => ((int)Number(cp?["flags"], 0) & 1) != 0)) return true;
        return Array(data["children"]).Any(child => Truth(child?["name"]) && ParticleMouse(Text(child!["name"]), seen));
    }
}
