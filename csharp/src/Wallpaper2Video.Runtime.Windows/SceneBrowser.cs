using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

public sealed record BrowserObject(int Index, string Name, int? Parent, string Path, string Note, bool SourceVisible);
public sealed record BrowserItem(SceneTarget Target, string Name, string Category, int? ObjectIndex, int? EffectOrder);

/// <summary>One catalogue shared by the object and category views. No scene state is changed here.</summary>
public sealed class SceneBrowser
{
    public static readonly string[] Categories = ["周期动态", "周期改写", "粒子", "模型动画", "静态图像", "音频响应", "脚本、时间线与场景控制", "待确认动态", "自动排除 / 不可启用"];
    public IReadOnlyList<BrowserObject> Objects { get; private init; } = [];
    public IReadOnlyList<BrowserItem> Items { get; private init; } = [];

    public static SceneBrowser Build(ScenePlan plan)
    {
        var scene = plan.SceneDocument;
        var objects = Array(scene?["objects"]);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < objects.Count; i++) if (objects[i]?["id"] != null) ids.TryAdd(Text(objects[i]!["id"]), i);
        var parents = new int?[objects.Count];
        var notes = new string[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            string parent = Text(Value(objects[i]?["parent"]));
            notes[i] = "";
            if (parent.Length == 0) continue;
            if (ids.TryGetValue(parent, out int index) && index != i) parents[i] = index;
            else notes[i] = "父对象缺失或引用自身，显示在根级";
        }
        // Detach one edge per cycle, retaining every object instead of losing an entire subtree.
        for (int i = 0; i < objects.Count; i++)
        {
            var seen = new HashSet<int> { i }; int current = i;
            while (parents[current] is int next)
            {
                if (!seen.Add(next)) { parents[current] = null; notes[current] = "父对象循环引用，已展开为根级"; break; }
                current = next;
            }
        }
        string Name(int i) => Text(objects[i]?["name"], "对象 " + Text(objects[i]?["id"], (i + 1).ToString(CultureInfo.InvariantCulture)));
        var nodes = new List<BrowserObject>();
        for (int i = 0; i < objects.Count; i++)
        {
            var chain = new List<string> { Name(i) }; int current = i;
            while (parents[current] is int next) { chain.Add(Name(next)); current = next; }
            chain.Reverse();
            nodes.Add(new(i, Name(i), parents[i], string.Join(" › ", chain), notes[i], !False(Value(objects[i]?["visible"]))));
        }
        var targets = plan.Targets().ToList();
        foreach (var required in plan.Required)
        {
            // A blocked effect/particle already explains its exclusion. Other removed inputs remain inspectable.
            if (targets.Any(t => t.Blocked.Length > 0 && Prefix(required.Path, t.Path))) continue;
            targets.Add(new(required.Id, required.Label, required.Kind, [.. required.Path], required.Reason));
        }
        var items = new List<BrowserItem>();
        foreach (var target in targets)
        {
            int? oi = target.Path.Length > 1 && target.Path[0] == "objects" && int.TryParse(target.Path[1], out int index) ? index : null;
            int? order = target.Kind == "effect" && target.Path.Length > 3 && int.TryParse(target.Path[3], out int ei) ? ei + 1 : null;
            string title = target.Label;
            if (order != null) title = AlignmentPlanner.Family(Text(At(scene, target.Path)?["file"]));
            else if (target.Kind == "particle" && oi is int particle) title = Name(particle);
            else if (target.Kind == "timeline" && plan.Alignment.Phases.FirstOrDefault(p => p.Id == target.Id) is PeriodPhase motion) title = TimelineName(motion);
            items.Add(new(target, title, Category(plan, target, scene), oi, order));
        }
        return new() { Objects = nodes, Items = items };
    }
    private static string Category(ScenePlan plan, SceneTarget target, JsonObject? scene)
    {
        if (target.Blocked.Length > 0) return Categories[8];
        var binding = At(scene, target.Path);
        if (target.Kind == "script" && Regex.IsMatch(CodeOnly(Text(binding?["script"])), @"\b(?:g_Audio\w*|audioBuffer|audioSpectrum|registerAudioListener)\b", RegexOptions.IgnoreCase)) return Categories[5];
        if (target.Kind == "particle") return Categories[2];
        if (target.Kind == "model") return Categories[3];
        if (target.Kind is "bone" or "sprite") return Categories[0];
        if (target.Kind == "timeline" && plan.Alignment.Phases.Any(p => p.Id == target.Id && p.Method == "timeline_retime")) return Categories[0];
        if (target.Kind is "script" or "timeline" or "camera") return Categories[6];
        if (target.Kind != "effect") return Categories[7];
        string family = AlignmentPlanner.Family(Text(binding?["file"]));
        var graph = plan.Alignment.Graphs.GetValueOrDefault(target.Path[1] + ":" + target.Path[3]);
        JsonNode? audio = graph?.Combos.GetValueOrDefault("AUDIOPROCESSING");
        foreach (var pass in Array(binding?["passes"])) if (pass?["combos"] is JsonObject combos && combos.ContainsKey("AUDIOPROCESSING")) audio = combos["AUDIOPROCESSING"];
        if (family.Contains("audio", StringComparison.OrdinalIgnoreCase) || Truth(Value(audio))) return Categories[5];
        var phases = plan.Alignment.Phases.Where(p => Prefix(target.Path, p.EffectPath)).ToArray();
        if (phases.Any(p => p.ReferenceKind == "synthetic")) return Categories[1];
        if (phases.Length > 0) return Categories[0];
        if (graph?.Category == "STATIC" && !plan.Optional.Any(i => i.Id == target.Id)) return Categories[4];
        return Categories[7];
    }
    public static string TimelineName(PeriodPhase phase)
    {
        string Property(string key) => key switch { "origin" => "位置移动", "angles" => "旋转", "scale" => "缩放", "alpha" => "透明度", "color" => "颜色", _ => key + "变化" };
        var names = new[] { phase.EffectPath[^1] }.Concat(Array(phase.Settings?["combined_paths"]).Select(p => Text(Array(p).Last()))).Select(Property).Distinct().ToArray();
        return string.Join(" + ", names) + (names.Length > 1 ? "（组合时间线）" : "（时间线）");
    }
}
