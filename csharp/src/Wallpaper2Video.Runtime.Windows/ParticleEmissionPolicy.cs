using System.Text.Json.Nodes;
using Wallpaper2Video.Core;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

/// <summary>Audio-independent emission, shared by analysis and engine-copy preparation.</summary>
public static class ParticleEmissionPolicy
{
    public const double FallbackRate = 1;
    public static bool HasAudioResponse(JsonNode? node)
        => node is JsonObject obj ? obj.Any(p => p.Key.Equals("audioprocessingmode", StringComparison.OrdinalIgnoreCase) && Number(Value(p.Value), 0) != 0 || HasAudioResponse(p.Value))
            : node is JsonArray array && array.Any(HasAudioResponse);

    public static void Normalize(JsonObject definition, bool root)
    {
        // Event children must retain zero continuous rate and their original instantaneous burst.
        if (root) foreach (var emitter in Array(definition["emitter"]).OfType<JsonObject>())
            if (HasAudioResponse(emitter) && Number(Value(emitter["rate"]), 0) <= 0) emitter["rate"] = FallbackRate;
        void Disable(JsonNode? node)
        {
            if (node is JsonObject obj) foreach (string key in obj.Select(p => p.Key).ToArray())
            { if (key.Equals("audioprocessingmode", StringComparison.OrdinalIgnoreCase)) obj[key] = 0; else Disable(obj[key]); }
            else if (node is JsonArray array) foreach (var child in array) Disable(child);
        }
        Disable(definition);
    }

    public static JsonObject Apply(JsonObject scene, string root, AssetBundle bundle)
    {
        var changed = new JsonArray(); int ordinal = 0;
        string job = Hash(root)[..12];
        foreach (var obj in Array(scene["objects"]).OfType<JsonObject>().Where(o => Truth(o["particle"])))
        {
            string source = ArchivePaths.Normalize(Text(obj["particle"]));
            var files = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            void Read(string path)
            {
                path = ArchivePaths.Normalize(path); if (files.ContainsKey(path)) return;
                if (files.Count >= 256) throw new InvalidDataException("粒子依赖过多，无法关闭音频响应");
                string local = SafeFiles.Member(root, path);
                var data = Parse(File.Exists(local) ? File.ReadAllText(local) : bundle.Read(path)); files[path] = data;
                foreach (var link in Array(data["children"])) Read(Text(link?["name"]));
            }
            Read(source);
            if (!files.Values.Any(HasAudioResponse)) continue;
            // Each root owns its graph: a definition used as both a root and an event child
            // must not acquire the root's fallback rate in the child role.
            string prefix = $"particles/w2v_fixed_{job}_{ordinal++}/";
            var mapping = files.Keys.Select((name, index) => (name, target: prefix + index + ".json")).ToDictionary(p => p.name, p => p.target, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, data) in files)
            {
                Normalize(data, name.Equals(source, StringComparison.OrdinalIgnoreCase));
                foreach (var link in Array(data["children"])) link!["name"] = mapping[ArchivePaths.Normalize(Text(link?["name"]))];
                string target = SafeFiles.Member(root, mapping[name]); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, data.ToJsonString(Output));
            }
            obj["particle"] = mapping[source];
            changed.Add(new JsonObject
            {
                ["id"] = obj["id"]?.DeepClone(),
                ["name"] = obj["name"]?.DeepClone(),
                ["source"] = source,
                ["fixed_rates"] = new JsonArray(Array(files[source]["emitter"]).Select(e => (JsonNode?)JsonValue.Create(Number(Value(e?["rate"]), 0))).ToArray())
            });
        }
        return new JsonObject { ["audio_response"] = "disabled", ["fallback_rate"] = FallbackRate, ["rewritten_layers"] = changed };
    }
}
