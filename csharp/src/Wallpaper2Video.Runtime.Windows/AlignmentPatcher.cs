using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wallpaper2Video.Core;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

public static class AlignmentPatcher
{
    internal static string TimelineName(PeriodPhase phase, JsonNode? options) =>
        Text(options?["name"]).Length > 0 ? Text(options!["name"]) : "w2v_" + Hash(phase.Id)[..10];
    public static string ReplaceClock(string source, string expression) => Regex.Replace(source,
        "//[^\\n]*|/\\*[\\s\\S]*?\\*/|\"(?:\\\\.|[^\"\\\\])*\"|\\buniform\\s+float\\s+g_Time\\s*;|\\bg_Time\\b",
        m => m.Value == "g_Time" ? expression : m.Value);
    public static string Rewrite(string source, PeriodPhase phase, double target)
    {
        if (phase.Method == "bounded_replay") return ReplaceClock(source,
            $"(0.137 + {Glsl(phase.Origin * .5)} * (1.0 - cos(6.283185307179586 * frac(g_Time / {Glsl(target)}))))");
        if (phase.Method == "foliage_harmonics")
        {
            double speed = Number(phase.Settings?["speed"]);
            return Regex.Replace(source, @"g_Speed\s*\*\s*g_Time\s*\*\s*vec4\(([^()]+)\)", m =>
            {
                double[] coefficients = m.Groups[1].Value.Split(',').Select(v => double.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
                if (coefficients.Length != 4) throw new InvalidDataException("风动频率向量格式已变化");
                return "(g_Time * vec4(" + string.Join(", ", coefficients.Select(c =>
                {
                    double rate = c * speed;
                    long n = rate != 0 ? FrameMath.NearestCycles(Math.Abs(rate) * phase.Origin / (2 * Math.PI)) : 0;
                    return Glsl(Math.CopySign(n * 2 * Math.PI / target, rate));
                })) + "))";
            });
        }
        if (phase.Method == "iris_sequence")
        {
            double steps = Number(phase.Settings?["steps"]), speed = Number(phase.Settings?["speed"]);
            source = Regex.Replace(source, @"\(g_Time\s*\*\s*g_Speed\)", $"(g_Time * {Glsl(Math.CopySign(steps / target, speed))})");
            foreach (string vector in new[] { "vec2(0, 1)", "vec4(0, 0, 1, 1)" })
            {
                string original = "(lowDt + " + vector + ")";
                source = source.Replace(original, $"(frac({original} / {Glsl(steps)}) * {Glsl(steps)})", StringComparison.Ordinal);
            }
            double scale = FrameMath.NearestCycles(steps / (2 * Math.PI)) * 2 * Math.PI / steps;
            return source.Replace("sin(time)", $"sin(time * {Glsl(scale)})", StringComparison.Ordinal).Replace("cos(time)", $"cos(time * {Glsl(scale)})", StringComparison.Ordinal);
        }
        return ReplaceClock(source, $"(g_Time * {Glsl(phase.Origin / target)})");
    }
    public static void Apply(JsonObject scene, string root, AlignmentPlan plan, IReadOnlyDictionary<string, int> cycles, double duration,
        IReadOnlySet<string>? appliedPhaseIds = null)
    {
        plan.Validate(cycles);
        var privateGroups = new Dictionary<string, List<PeriodPhase>>();
        foreach (var phase in plan.Phases)
        {
            if (appliedPhaseIds != null && !appliedPhaseIds.Contains(phase.Id)) continue;
            double target = duration / cycles[phase.Id];
            if (phase.Method is "embedded_video_rate" or "sprite_frames") continue; // The capture controller owns texture playback.
            if (phase.Method == "skeletal_rate")
            {
                var layer = At(scene, phase.EffectPath) ?? throw new InvalidDataException("骨骼动画层已变化");
                double rate = Math.CopySign(phase.Factor / target, Number(phase.Raw));
                if (layer["rate"] is JsonObject binding)
                {
                    // Wallpaper Engine reapplies the author slider to a bound rate.
                    binding.Remove("user");
                    binding["value"] = rate;
                }
                else layer["rate"] = rate;
                continue;
            }
            if (phase.Method == "timeline_retime")
            {
                var options = At(scene, phase.EffectPath)?["animation"]?["options"] ?? throw new InvalidDataException("时间线绑定已变化");
                string mode = Text(phase.Settings?["mode"]);
                options["name"] = TimelineName(phase, options);
                options["fps"] = Number(phase.Settings?["length"]) * (mode == "loop" ? 1 : 2) / target;
                foreach (var path in Array(phase.Settings?["combined_paths"]))
                    At(scene, Array(path).Select(p => Text(p)).ToArray())!["animation"]!["options"]!["fps"] = Number(options["fps"]);
                continue;
            }
            if (phase.Parameter != null)
            {
                var effect = At(scene, phase.EffectPath) ?? throw new InvalidDataException("效果绑定已变化");
                if (effect["passes"] is not JsonArray passes) effect["passes"] = passes = [];
                while (passes.Count <= phase.PassIndex) passes.Add(new JsonObject());
                if (passes[phase.PassIndex]?["constantshadervalues"] is not JsonObject values) passes[phase.PassIndex]!["constantshadervalues"] = values = new();
                double raw = phase.Component.HasValue ? AlignmentPlanner.Vector(phase.Raw, Text(phase.Raw).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)[phase.Component.Value] : Number(phase.Raw);
                double value = (raw < 0 ? -1 : 1) * Math.Pow(phase.Factor / target, 1d / phase.Power);
                if (!phase.Component.HasValue)
                {
                    if (values[phase.Parameter] is JsonObject binding && binding.ContainsKey("value"))
                    {
                        // WPE reapplies author user properties after reading the fallback value.
                        // The aligned parameter has an independent export clock/rate now.
                        binding.Remove("user");
                        binding["value"] = value;
                    }
                    else values[phase.Parameter] = value;
                }
                else
                {
                    double[] vector = AlignmentPlanner.Vector(Value(values[phase.Parameter] ?? phase.Raw), Text(phase.Raw).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                    vector[phase.Component.Value] = value;
                    values[phase.Parameter] = string.Join(' ', vector.Select(v => v.ToString("G17", CultureInfo.InvariantCulture)));
                }
            }
            else
            {
                string key = Key(phase.EffectPath);
                if (!privateGroups.TryGetValue(key, out var group)) privateGroups[key] = group = [];
                group.Add(phase);
            }
        }
        foreach (var phases in privateGroups.Values)
        {
            var first = phases[0];
            var graph = plan.Graphs[first.EffectPath[1] + ":" + first.EffectPath[3]];
            string uid = Hash(plan.Id + Key(first.EffectPath))[..12];
            var mapping = graph.Files.Keys.Where(m => Path.GetExtension(m) is ".json" or ".vert" or ".frag" or ".h")
                .ToDictionary(m => m, m => m[..m.IndexOf('/')] + "/w2v_" + uid + "/" + m[(m.IndexOf('/') + 1)..]);
            JsonNode? Remap(JsonNode? node)
            {
                if (node is JsonObject o)
                {
                    var result = new JsonObject();
                    foreach (var (key, value) in o) result[key] = key == "shader" && value is JsonValue v && v.TryGetValue<string>(out var shader)
                        ? JsonValue.Create("w2v_" + uid + "/" + shader) : Remap(value);
                    return result;
                }
                if (node is JsonArray a) return new JsonArray(a.Select(Remap).ToArray());
                if (node is JsonValue text && text.TryGetValue<string>(out var path)) return JsonValue.Create(mapping.GetValueOrDefault(path, path));
                return node?.DeepClone();
            }
            var changed = phases.Where(p => p.Method.Length > 0).ToDictionary(p => p.Id, _ => false);
            var output = new Dictionary<string, string>();
            foreach (var (member, destination) in mapping)
            {
                string source = graph.Files[member];
                if (member.EndsWith(".json", StringComparison.Ordinal)) source = Remap(Parse(source))!.ToJsonString(Output);
                else
                {
                    foreach (var phase in phases)
                        if (phase.Members?.Contains(member) == true)
                        {
                            string rewritten = Rewrite(source, phase, duration / cycles[phase.Id]);
                            if (phase.Method.Length > 0) changed[phase.Id] |= rewritten != source;
                            source = rewritten;
                        }
                    source = Regex.Replace(source, "(#include\\s+[\"<])([^\">]+)([\">])", m =>
                    {
                        string key = "shaders/" + m.Groups[2].Value;
                        string path = mapping.GetValueOrDefault(key, key);
                        return m.Groups[1].Value + path["shaders/".Length..] + m.Groups[3].Value;
                    });
                }
                output[destination] = source;
            }
            if (changed.Values.Any(v => !v)) throw new InvalidDataException("周期改写没有匹配预期公式，已停止导出");
            foreach (var (destination, source) in output)
            {
                string path = SafeFiles.Member(root, destination);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, source);
            }
            var effect = At(scene, first.EffectPath)!;
            effect["file"] = mapping[Text(effect["file"])];
        }
    }
}
