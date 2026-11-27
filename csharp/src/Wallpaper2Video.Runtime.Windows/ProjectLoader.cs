using System.Text.Json;
using Wallpaper2Video.Core;

namespace Wallpaper2Video.Runtime.Windows;

public sealed record EffectInfo(string Key, string Name, string Layer, string File, string Kind = "effect");
public sealed record ProjectSnapshot(string Root, string Entry, string Title, string Kind,
    JsonElement Metadata, JsonElement? Scene, string? PackagePath, string? PackageVersion, int PackageFiles,
    int? Width, int? Height, int Objects, int Images, int Particles, IReadOnlyList<EffectInfo> Effects, string? PreviewPath);

public static class ProjectLoader
{
    private static readonly HashSet<string> VideoExtensions = new(
        [".mp4", ".webm", ".mkv", ".mov", ".avi", ".gif", ".m4v"], StringComparer.OrdinalIgnoreCase);

    public static JsonElement ParseObject(byte[] data)
    {
        if (data.Length > 16 * 1024 * 1024) throw new InvalidDataException("JSON超过16MB");
        ReadOnlyMemory<byte> bytes = data;
        if (data.AsSpan().StartsWith(new byte[] { 239, 187, 191 })) bytes = bytes[3..];
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("JSON顶层必须是对象");
        return document.RootElement.Clone();
    }

    private static JsonElement ReadObject(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("JSON超过16MB");
        return ParseObject(File.ReadAllBytes(path));
    }
    private static string Text(JsonElement element, string key, string fallback = "") =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
    private static int? Number(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
    private static JsonElement RequireKind(JsonElement value, JsonValueKind kind, string path) =>
        value.ValueKind == kind ? value : throw new InvalidDataException($"场景字段{path}必须为{(kind == JsonValueKind.Object ? "对象" : "数组")}");
    private static bool HasContent(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
        JsonValueKind.String => value.GetString()!.Length > 0,
        JsonValueKind.Number => value.GetDouble() != 0,
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        _ => true
    };
    // Match Python's _value(visible) is False, including property-bound values.
    private static bool IsHidden(JsonElement element)
    {
        if (!element.TryGetProperty("visible", out var visible)) return false;
        if (visible.ValueKind == JsonValueKind.Object)
            return visible.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.False;
        return visible.ValueKind == JsonValueKind.False;
    }

    public static ProjectSnapshot Load(string source)
    {
        string path = SafeFiles.Resolve(source);
        if (!Path.Exists(path)) throw new FileNotFoundException("素材不存在", path);
        string root = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        string metadataPath = Path.Combine(root, "project.json");
        if (File.Exists(metadataPath)) metadataPath = SafeFiles.Member(root, "project.json");
        JsonElement metadata = File.Exists(metadataPath) ? ReadObject(metadataPath) : ParseObject("{}"u8.ToArray());
        string asset = Directory.Exists(path) || Path.GetFileName(path).Equals("project.json", StringComparison.OrdinalIgnoreCase)
            ? SafeFiles.Member(root, Text(metadata, "file", "scene.json")) : path;
        string kind = VideoExtensions.Contains(Path.GetExtension(asset)) ? "video" : Text(metadata, "type").ToLowerInvariant();
        string packagePath = Path.GetExtension(asset).Equals(".pkg", StringComparison.OrdinalIgnoreCase) ? asset : Path.Combine(root, "scene.pkg");
        JsonElement? scene = null;
        string? version = null;
        int packageFiles = 0;
        string entry = asset;
        if (kind != "video" && File.Exists(packagePath))
        {
            packagePath = SafeFiles.Member(root, Path.GetRelativePath(root, packagePath));
            using var package = PackageReader.Open(packagePath);
            scene = ParseObject(package.Read("scene.json"));
            version = package.Version;
            packageFiles = package.Entries.Count;
            kind = "scene";
            entry = File.Exists(metadataPath) ? metadataPath : packagePath;
        }
        else if (kind == "scene" || Path.GetFileName(asset).Equals("scene.json", StringComparison.OrdinalIgnoreCase))
        {
            kind = "scene";
            scene = ReadObject(asset);
            entry = File.Exists(metadataPath) ? metadataPath : asset;
        }
        else if (!File.Exists(asset)) throw new FileNotFoundException("找不到项目入口", asset);
        if (kind is not ("video" or "scene" or "web")) throw new InvalidDataException("不支持的壁纸类型");
        var effects = new List<EffectInfo>();
        int objects = 0, images = 0, particles = 0;
        int? width = null, height = null;
        if (scene is JsonElement document)
        {
            if (document.TryGetProperty("general", out var general))
            {
                RequireKind(general, JsonValueKind.Object, "general");
                if (general.TryGetProperty("orthogonalprojection", out var projection))
                {
                    RequireKind(projection, JsonValueKind.Object, "general.orthogonalprojection");
                    width = Number(projection, "width"); height = Number(projection, "height");
                }
            }
            if (document.TryGetProperty("objects", out var array))
            {
                RequireKind(array, JsonValueKind.Array, "objects");
                foreach (var obj in array.EnumerateArray())
                {
                    int index = objects++;
                    RequireKind(obj, JsonValueKind.Object, $"objects[{index}]");
                    if (obj.TryGetProperty("image", out var image) && HasContent(image)) images++;
                    if (obj.TryGetProperty("particle", out var particle) && HasContent(particle)) particles++;
                    string layer = Text(obj, "name", $"图层{index + 1}");
                    bool hidden = IsHidden(obj);
                    if (!hidden && obj.TryGetProperty("particle", out particle) && HasContent(particle))
                        effects.Add(new($"particle:{index}", "粒子 · " + layer, $"图层 {index + 1}",
                            particle.ValueKind == JsonValueKind.String ? particle.GetString()! : particle.GetRawText(), "particle"));
                    if (!obj.TryGetProperty("effects", out var list)) continue;
                    RequireKind(list, JsonValueKind.Array, $"objects[{index}].effects");
                    int ei = 0;
                    foreach (var effect in list.EnumerateArray())
                    {
                        RequireKind(effect, JsonValueKind.Object, $"objects[{index}].effects[{ei}]");
                        string file = Text(effect, "file");
                        string name = Path.GetFileName(Path.GetDirectoryName(file.Replace('/', Path.DirectorySeparatorChar))) ?? "效果";
                        if (!hidden && !IsHidden(effect))
                            effects.Add(new($"effect:{index}:{ei}", name, $"{layer} · {index + 1}.{ei + 1}", file));
                        ei++;
                    }
                }
            }
        }
        string? preview = null;
        string previewName = Text(metadata, "preview");
        if (previewName.Length > 0)
        {
            try { string candidate = SafeFiles.Member(root, previewName); if (File.Exists(candidate)) preview = candidate; }
            catch (InvalidDataException) { }
        }
        return new(root, entry, Text(metadata, "title", kind == "video" ? Path.GetFileNameWithoutExtension(asset) : Path.GetFileName(root)), kind, metadata, scene,
            version == null ? null : packagePath, version, packageFiles, width, height, objects, images, particles, effects.AsReadOnly(), preview);
    }
}
