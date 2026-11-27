using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wallpaper2Video.Core;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

public static class ScenePlanner
{
    public static ScenePlan Build(ProjectSnapshot project, ExportSettings settings, string? engine = null, IReadOnlyDictionary<string, int>? cycles = null, AssetBundle? bundle = null, AlignmentPlan? alignment = null)
    {
        settings.Validate();
        if (project.Kind == "web") throw new NotSupportedException("当前无法可靠排除 Web 壁纸脚本中的鼠标和日期时间内容，暂不支持此筛选流程");
        bundle ??= new(project, engine); alignment ??= AlignmentPlanner.Analyze(project, bundle);
        double duration = (double)settings.Frames / settings.Fps;
        cycles ??= alignment.Choose(duration);
        alignment.Validate(cycles);
        var particlePlan = ParticlePlanner.Analyze(project, bundle, duration, settings.Fps, (double)settings.FadeFrames / settings.Fps, settings.ParticleMode);
        var required = new List<ReviewItem>();
        var optional = new List<ReviewItem>();
        var scene = project.Scene is { } sourceScene ? Parse(sourceScene.GetRawText()) : null;
        if (scene != null)
        {
            var objects = Array(scene["objects"]);
            var removed = new HashSet<int>();
            for (int i = 0; i < objects.Count; i++)
                if (AlignmentPlanner.ClockLayer(objects[i]!))
                {
                    removed.Add(i);
                    required.Add(new($"required:layer:{i}", Text(objects[i]?["name"], "日期时间层"), "日期时间显示层，导出时直接排除", "layer", ["objects", i.ToString(CultureInfo.InvariantCulture)], "remove_layer"));
                }
            var initial = removed.ToHashSet(); AlignmentPlanner.ExpandChildren(objects, removed);
            foreach (int i in removed.Except(initial).Order()) required.Add(new($"required:layer:{i}", Text(objects[i]?["name"], i.ToString(CultureInfo.InvariantCulture)), "属于已排除日期时间层的子层", "layer", ["objects", i.ToString(CultureInfo.InvariantCulture)], "remove_layer"));
            if (scene["general"] is not JsonObject general) scene["general"] = general = new();
            if (Truth(Value(general["cameraparallax"])) || Truth(Value(general["cameraparallaxmouseinfluence"]))) required.Add(new("required:camera-parallax", "相机鼠标视差", "导出时关闭鼠标视差输入", "camera", ["general"], "disable_parallax"));
            foreach (string key in new[] { "camerashake", "camerafade", "windenabled" }) if (Truth(Value(general[key]))) optional.Add(new("camera:" + key, key, "相机或风场动画尚未确认闭合", "camera", ["general", key], "false"));
            void Walk(JsonNode? node, string[] path, string label)
            {
                if (node is JsonObject o)
                {
                    if (o["script"] is JsonValue script && script.TryGetValue<string>(out var code) && code.Trim().Length > 0)
                    {
                        bool boneControl = !settings.PreserveEntrance && (path.Length > 2 && path[0] == "objects" && path[2] == "animationlayers" || BoneScriptRules.ControlsBones(code));
                        string? reason = boneControl ? "循环导出抑制骨骼事件；已验证的初始相位由导出时钟接管" : Restricted(code);
                        if (reason != null) required.Add(new("required:script:" + Key(path), label + " / 强制排除脚本", reason, "script", [.. path], "strip_script"));
                        else optional.Add(new("script:" + Key(path), label + " / 参数脚本", "脚本状态、外部输入与副作用尚未证明周期闭合", "script", [.. path], "strip_script"));
                    }
                    if (o["animation"] is JsonObject anim && anim.Count > 0)
                    {
                        var phase = alignment.Phases.FirstOrDefault(p => p.EffectPath.SequenceEqual(path) && p.Method == "timeline_retime");
                        string? reason = !alignment.Covered.Contains(Key(path)) ? "时间线端点、插值和目标时长尚未验证" : phase?.ReferenceKind == "synthetic" ? phase.Note + "；默认关闭，勾选表示采用此变化" : null;
                        bool entrance = path.Length > 2 && path[0] == "objects" && EntrancePolicy.Identified(objects[int.Parse(path[1], CultureInfo.InvariantCulture)]!);
                        if (reason != null && !entrance) optional.Add(new("timeline:" + Key(path), label + " / 时间线", reason, "timeline", [.. path], "strip_animation"));
                    }
                    foreach (var (key, child) in o) if (key is not ("script" or "animation")) Walk(child, [.. path, key], label);
                }
                else if (node is JsonArray a)
                    for (int i = 0; i < a.Count; i++)
                    {
                        if (path.SequenceEqual(new[] { "objects" }) && removed.Contains(i)) continue;
                        string nextLabel = path.SequenceEqual(new[] { "objects" }) && a[i] is JsonObject item ? Text(item["name"], Text(item["id"], label)) : label;
                        Walk(a[i], [.. path, i.ToString(CultureInfo.InvariantCulture)], nextLabel);
                    }
            }
            Walk(scene, [], "场景");
            for (int oi = 0; oi < objects.Count; oi++)
            {
                var obj = objects[oi]!;
                bool renderTargetOnly = alignment.HiddenRenderTargetLayers.Contains(oi);
                if (removed.Contains(oi) || False(Value(obj["visible"])) && !Truth(obj["particle"]) && !renderTargetOnly) continue;
                string label = Text(obj["name"], Text(obj["id"], oi.ToString(CultureInfo.InvariantCulture)));
                string[] path = ["objects", oi.ToString(CultureInfo.InvariantCulture)];
                if (Truth(obj["particle"]))
                {
                    try
                    {
                        if (bundle.ParticleMouse(Text(obj["particle"]))) required.Add(new($"required:particle:{oi}", label + " / 鼠标粒子层", "粒子控制点跟随鼠标，导出时直接排除", "particle", [.. path], "remove_layer"));
                        else if (!particlePlan.Layers.Any(r => Number(r["index"]) == oi))
                        {
                            string reason = Text(particlePlan.Unsupported.FirstOrDefault(r => Number(r["index"]) == oi)?["reason"], "已选择保留原始随机粒子");
                            optional.Add(new($"particle:{oi}", label + " / 原始粒子（风险）", reason + "；保留时不保证首尾为空", "particle", [.. path], "remove_layer"));
                        }
                        else if (False(Value(obj["visible"]))) optional.Add(new($"particle:{oi}", label, "原素材默认隐藏，可明确开启", "particle", [.. path], "hidden_particle"));
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException)
                    { optional.Add(new($"particle:{oi}", label + " / 粒子层", "粒子依赖无法核对鼠标控制点，暂不可启用", "particle", [.. path], "remove_layer", false)); }
                }
                if (Truth(obj["animationlayers"]) || Truth(obj["model"]) || Truth(obj["instance"]))
                {
                    int extracted = alignment.Phases.Count(p => p.Method == "skeletal_rate" && Prefix(path, p.EffectPath));
                    optional.Add(new($"model:{oi}", label + " / 模型或实例层", Truth(obj["instance"]) ? "实例内部尚无法应用强制交互筛选，暂不可启用" : extracted > 0 ? $"已提取{extracted}段独立骨骼循环，可分别调整次数或速度；其它模型状态仍需预览确认" : "骨骼或模型内部动画尚未确认闭合", "model", [.. path], "remove_layer", !Truth(obj["instance"])));
                }
                var effects = Array(obj["effects"]);
                for (int ei = 0; ei < effects.Count; ei++)
                {
                    var effect = effects[ei]!;
                    if (False(effect["visible"]) || renderTargetOnly && False(Value(effect["visible"]))) continue;
                    string name = Text(effect["file"]), family = AlignmentPlanner.Family(name);
                    string[] effectPath = [.. path, "effects", ei.ToString(CultureInfo.InvariantCulture)];
                    if (AlignmentPlanner.DedicatedMouse.Contains(family)) { required.Add(new($"required:effect:{oi}:{ei}", label + " / " + family, "专用鼠标交互效果，导出时直接排除", "effect", [.. effectPath], "remove_effect")); continue; }
                    string? reason;
                    bool canEnable = true;
                    try
                    {
                        var graph = bundle.Effect(name);
                        if (graph.Files.Values.Any(s => Regex.IsMatch(CodeOnly(s), @"\bg_(?:Pointer|Parallax|Mouse)\w*\b"))) required.Add(new($"required:pointer:{oi}:{ei}", label + " / " + family + " 的鼠标输入", "在导出副本中将鼠标/视差输入固定为中性常量，保留其他部分", "shader", [.. effectPath], "neutralize_pointer"));
                        reason = EffectReason(name, effect, graph, duration);
                        if (alignment.Covered.Contains(Key(effectPath)))
                        {
                            var rewrite = alignment.Phases.FirstOrDefault(p => p.EffectPath.SequenceEqual(effectPath) && p.ReferenceKind == "synthetic");
                            reason = rewrite == null ? null : "可采用周期改写：" + rewrite.Note + "；默认关闭，勾选表示采用此运动变化";
                        }
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException)
                    { canEnable = false; reason = "效果依赖未能核对强制排除规则，暂不可启用：" + error.Message; }
                    if (reason != null) optional.Add(new($"effect:{oi}:{ei}", label + " / " + family, reason, "effect", [.. effectPath], "remove_effect", canEnable));
                }
            }
        }
        string id = Hash(project.Root + project.Metadata.GetRawText() + scene?.ToJsonString() + JsonSerializer.Serialize(settings, Output) +
            string.Join(';', bundle.Stamps.OrderBy(p => p.Key).Select(p => p.Key + p.Value)) + JsonSerializer.Serialize(cycles) + (project.PackagePath == null ? "" : new FileInfo(project.PackagePath).LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)));
        return new ScenePlan(project, bundle, alignment, settings, cycles)
        {
            Id = id,
            SceneDocument = scene,
            ParticlePlan = particlePlan,
            Required = required.ToImmutableArray(),
            Optional = optional.ToImmutableArray()
        };
    }
    private static string? EffectReason(string name, JsonNode effect, EffectGraph graph, double duration)
    {
        string? review = graph.Category;
        if (review == null) return "效果实现或版本尚未核对，不能保证闭合";
        string source = string.Join('\n', graph.Files.Where(p => Path.GetExtension(p.Key) is ".vert" or ".frag" or ".h").Select(p => CodeOnly(p.Value)));
        bool time = Regex.IsMatch(Regex.Replace(source, @"\buniform\s+\w+\s+g_Time\s*;", ""), @"\bg_Time\b"), audio = Regex.IsMatch(source, @"\bg_Audio\w*");
        if (review == "STATIC" && !time && !audio) return null;
        var values = graph.Defaults.ToDictionary(p => p.Key, p => p.Value);
        var combos = graph.Combos.ToDictionary(p => p.Key, p => p.Value);
        foreach (var pass in Array(effect["passes"]))
        {
            if (pass?["combos"] is JsonObject cb) foreach (var (key, value) in cb) combos[key] = value;
            if (pass?["constantshadervalues"] is JsonObject cv) foreach (var (key, value) in cv) values[key] = Value(value);
        }
        if (Truth(Value(combos.GetValueOrDefault("AUDIOPROCESSING")))) return "依赖实时音频；尚未重放周期频谱与响应状态";
        string family = AlignmentPlanner.Family(name);
        if (review == "STATIC" && family == "chromatic_aberration" && Number(values.GetValueOrDefault("speed"), 0) == 0) return null;
        bool Aligned(double turns) => double.IsFinite(turns) && Math.Abs(turns - Math.Round(turns)) < 1e-6;
        try
        {
            if (family is "waterwaves" or "shake" or "spin" or "swing" or "twirl" && !Truth(Value(combos.GetValueOrDefault("NOISE"))))
            {
                var speeds = new List<double> { Number(values.GetValueOrDefault("speed")) };
                if (Truth(Value(combos.GetValueOrDefault("DUALWAVES")))) speeds.Add(Number(values.GetValueOrDefault("speed2")));
                if (speeds.All(s => Aligned(s * duration / (2 * Math.PI)))) return null;
            }
            if (family == "waterflow" && Aligned(Number(values.GetValueOrDefault("speed")) * duration)) return null;
            if (review == "NATIVE_REPEAT_1S" && Aligned(duration)) return "颗粒过程虽每秒重复，但相位重置可能跳变；是否保留由你选择";
        }
        catch (Exception error) when (error is FormatException or ArgumentException) { }
        if (review is "STATEFUL" or "STATE_SOLVE") return "包含上一帧模拟或历史缓存；当前未求解循环初态";
        return review == "REWRITE" ? "需要改写时间公式才能适配短周期；本版尚未改写" : $"有动态输入或独立时间相位，当前未确认与 {duration:g} 秒对齐";
    }
}
