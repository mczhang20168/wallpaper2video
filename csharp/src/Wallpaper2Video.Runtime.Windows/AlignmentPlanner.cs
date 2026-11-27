using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wallpaper2Video.Core;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

public sealed record PeriodPhase(string Id, string Label, double Origin, string[] EffectPath,
    int PassIndex = 0, string? Parameter = null, JsonNode? Raw = null, double Factor = 1, int Power = 1,
    int? Component = null, string[]? Members = null, string Method = "", string ReferenceKind = "natural",
    string Note = "", JsonObject? Settings = null);

public sealed class AlignmentPlan
{
    public List<PeriodPhase> Phases { get; } = [];
    public List<object> Unsupported { get; } = [];
    public List<DurationSource> Durations { get; } = [];
    public List<EmbeddedVideoSource> EmbeddedVideos { get; } = [];
    internal List<string[]> SuppressedBones { get; } = [];
    internal List<RenderTargetInput> RenderTargetInputs { get; set; } = [];
    public IReadOnlySet<int> HiddenRenderTargetLayers { get; internal set; } = new HashSet<int>();
    public double? DefaultVideoDuration => EmbeddedVideos.Where(v => v.Visible).Select(v => (double?)v.Timing.Duration).Max();
    public HashSet<string> Covered { get; } = [];
    public Dictionary<string, EffectGraph> Graphs { get; } = [];
    public string Id { get; internal set; } = "";
    public double? Tmax => Phases.Where(p => p.ReferenceKind == "natural").Select(p => (double?)p.Origin).Max();
    public double ReferenceDuration => 20;
    public Dictionary<string, int> Choose(double duration, IReadOnlyDictionary<string, double>? periods = null)
    {
        if (!double.IsFinite(duration) || duration <= 0) throw new ArgumentException("视频周期长度必须为有限正数");
        if (periods != null && periods.Keys.Except(Phases.Select(p => p.Id)).Any()) throw new ArgumentException("未知的周期项目");
        var result = new Dictionary<string, int>();
        foreach (var phase in Phases)
        {
            double ratio = periods != null && periods.TryGetValue(phase.Id, out double requested) ? duration / requested : phase.ReferenceKind == "natural" ? duration / phase.Origin : 1;
            long n = FrameMath.NearestCycles(ratio);
            if (n > 1_000_000_000) throw new ArgumentException("循环次数过大，请增大单次周期");
            result[phase.Id] = (int)n;
        }
        return result;
    }
    public void Validate(IReadOnlyDictionary<string, int> cycles)
    {
        if (!cycles.Keys.ToHashSet().SetEquals(Phases.Select(p => p.Id)) || cycles.Values.Any(n => n is < 1 or > 1_000_000_000))
            throw new ArgumentException("周期项目不完整或循环次数无效");
    }
    public object Summary(double duration, IReadOnlyDictionary<string, int> cycles) => new
    {
        plan_id = Id,
        tmax = Tmax,
        tuser = duration,
        reference_duration = duration,
        rule = "natural: n=max(1,floor(Tuser/Tx_origin+0.5)); synthetic: n=1; Tx_set=Tuser/n",
        phases = Phases.Select(p => new
        {
            id = p.Id,
            label = p.Label,
            tx_origin = p.ReferenceKind == "natural" ? (double?)p.Origin : null,
            reference_period = p.Origin,
            reference_kind = p.ReferenceKind,
            tx_set = duration / cycles[p.Id],
            cycles = cycles[p.Id],
            reference_cycles = p.ReferenceKind == "natural" ? duration / p.Origin : 1,
            method = p.Method.Length > 0 ? p.Method : p.Parameter != null ? "parameter" : "private_shader_clock",
            note = p.Note,
            settings = p.Settings ?? new JsonObject()
        }),
        unsupported = Unsupported,
        duration_sources = Durations,
        embedded_video_default_duration = DefaultVideoDuration,
        embedded_videos = EmbeddedVideos
    };
}

public static class AlignmentPlanner
{
    public static readonly HashSet<string> DedicatedMouse = ["cursorripple", "depthparallax", "xray", "iris_follow_cursor"];
    public static double[] Vector(JsonNode? node, int count)
    {
        double[] values = node is JsonArray a ? a.Select(v => Number(v)).ToArray() : node is JsonValue v && v.TryGetValue<string>(out var text)
            ? text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray()
            : Enumerable.Repeat(Number(node), count).ToArray();
        if (values.Length != count || values.Any(n => !double.IsFinite(n))) throw new InvalidDataException("效果向量参数无效");
        return values;
    }
    public static bool ClockLayer(JsonNode obj)
    {
        if (obj["text"] == null) return false;
        string script = obj["text"] is JsonObject text ? Text(text["script"]) : "";
        return Regex.IsMatch(Text(obj["name"]), @"\bclock\b|\bcalendar\b|\bdate\b|时钟|日期|日历|时间显示", RegexOptions.IgnoreCase) ||
            (script.Length > 0 && Restricted(script) == "脚本读取系统日期时间");
    }
    public static void ExpandChildren(JsonArray objects, HashSet<int> removed)
    {
        while (true)
        {
            var ids = removed.Where(i => objects[i]?["id"] != null).Select(i => Text(objects[i]!["id"])).ToHashSet();
            var children = Enumerable.Range(0, objects.Count).Where(i => ids.Contains(Text(Value(objects[i]?["parent"]))) && !removed.Contains(i)).ToArray();
            if (children.Length == 0) break;
            removed.UnionWith(children);
        }
    }
    public static AlignmentPlan Analyze(ProjectSnapshot project, AssetBundle bundle)
    {
        var plan = new AlignmentPlan();
        if (project.Scene == null) { plan.Id = Hash(project.Root + project.Kind); return plan; }
        var scene = Parse(project.Scene.Value.GetRawText());
        var objects = Array(scene["objects"]);
        var blocked = new HashSet<int>();
        var hidden = new HashSet<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            var obj = objects[i]!;
            if (ClockLayer(obj)) blocked.Add(i);
            if (False(Value(obj["visible"]))) hidden.Add(i);
            if (Truth(obj["particle"]))
                try { if (bundle.ParticleMouse(Text(obj["particle"]))) blocked.Add(i); }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException) { }
        }
        ExpandChildren(objects, blocked);
        ExpandChildren(objects, hidden);
        var excluded = blocked.Concat(hidden).ToHashSet();
        plan.RenderTargetInputs = RenderTargetInput.Read(objects);
        var producers = RenderTargetInput.Reachable(plan.RenderTargetInputs,
            Enumerable.Range(0, objects.Count).Where(i => !excluded.Contains(i)), blocked);
        producers.IntersectWith(hidden);
        plan.HiddenRenderTargetLayers = producers;
        for (int oi = 0; oi < objects.Count; oi++)
        {
            var obj = objects[oi]!;
            bool renderTargetOnly = producers.Contains(oi);
            if (excluded.Contains(oi) && !renderTargetOnly) continue;
            if (!renderTargetOnly) SkeletalTiming.Analyze(obj, oi, bundle, plan, objects);
            var effects = Array(obj["effects"]);
            for (int ei = 0; ei < effects.Count; ei++)
            {
                var effect = effects[ei]!;
                if (False(effect["visible"]) || renderTargetOnly && False(Value(effect["visible"]))) continue;
                string name = Text(effect["file"]), family = Family(name);
                if (DedicatedMouse.Contains(family)) continue;
                string[] path = ["objects", oi.ToString(CultureInfo.InvariantCulture), "effects", ei.ToString(CultureInfo.InvariantCulture)];
                string title = Text(obj["name"], Text(obj["id"], oi.ToString(CultureInfo.InvariantCulture))) + " / " + family;
                int first = plan.Phases.Count;
                try
                {
                    var graph = bundle.Effect(name);
                    string? category = graph.Category;
                    if (category == null)
                    {
                        var proof = ShaderCycleProof.Analyze(bundle, graph, effect);
                        if (proof == null) throw new InvalidDataException("周期未验证：活动着色器表达式或输入未满足通用规则");
                        if (proof.Period is not double period)
                        {
                            foreach (double component in proof.Components)
                                plan.Durations.Add(new("shader", title, path, component, component, "component", false, "时间分量已核实；共同周期未验证"));
                            throw new InvalidDataException("周期未验证：活动时间分量没有可验证的共同周期");
                        }
                        plan.Graphs[$"{oi}:{ei}"] = graph;
                        plan.Phases.Add(new($"{oi}:{ei}:clock", title + " · 原始时钟", period, path,
                            Members: proof.Members, Method: "shader_clock", Note: "从活动着色器表达式提取；整帧仍须检查接缝"));
                        plan.Covered.Add(SceneJson.Key(path));
                        continue;
                    }
                    plan.Graphs[$"{oi}:{ei}"] = graph;
                    var values = graph.Defaults.ToDictionary(p => p.Key, p => p.Value?.DeepClone());
                    var combos = graph.Combos.ToDictionary(p => p.Key, p => p.Value?.DeepClone());
                    var locations = new Dictionary<string, int>();
                    var passes = Array(effect["passes"]);
                    for (int pi = 0; pi < passes.Count; pi++)
                    {
                        if (passes[pi]?["combos"] is JsonObject cb) foreach (var (key, value) in cb) combos[key] = Value(value)?.DeepClone();
                        if (passes[pi]?["constantshadervalues"] is JsonObject cv) foreach (var (key, value) in cv) { values[key] = Value(value)?.DeepClone(); locations[key] = pi; }
                    }
                    bool Combo(string key, double fallback = 0) => combos.TryGetValue(key, out var value) ? Truth(Value(value)) : fallback != 0;
                    double Num(string key, double fallback = 1) => values.TryGetValue(key, out var value) ? RequiredNumber(value) : fallback;
                    string Key(params string[] keys) => keys.FirstOrDefault(values.ContainsKey) ?? keys[0];
                    void Add(string suffix, string parameter, double factor = 1, int power = 1, int? pass = null)
                    {
                        JsonNode? raw = values.TryGetValue(parameter, out var input) ? input : JsonValue.Create(1);
                        if (raw == null) throw new InvalidDataException("效果参数为空");
                        double v = Number(raw);
                        if (Math.Abs(v) < 1e-14) return;
                        double origin = factor / Math.Pow(Math.Abs(v), power);
                        if (!double.IsFinite(origin) || origin <= 0) throw new InvalidDataException("无法确定有限正周期");
                        plan.Phases.Add(new($"{oi}:{ei}:{suffix}", title + " · " + suffix, origin, path, pass ?? locations.GetValueOrDefault(parameter), parameter, raw?.DeepClone(), factor, power));
                    }
                    void Clock(string suffix, double origin) => plan.Phases.Add(new($"{oi}:{ei}:{suffix}", title + " · " + suffix, origin, path,
                        Members: graph.Files.Keys.Where(k => Path.GetExtension(k) is ".vert" or ".frag").ToArray()));
                    void Rewrite(string method, string note, JsonObject? settings = null)
                    {
                        string[] members = graph.Files.Where(p => Path.GetExtension(p.Key) is ".vert" or ".frag" or ".h" && Regex.IsMatch(CodeOnly(p.Value), @"\bg_Time\b")).Select(p => p.Key).ToArray();
                        if (members.Length == 0 || category is "STATEFUL" or "STATE_SOLVE" or "REPLAY") throw new InvalidDataException("外部输入或历史状态不能仅靠改写时间闭合");
                        plan.Phases.Add(new($"{oi}:{ei}:{method}", title + " · " + note.Split('；')[0], 20, path, Members: members, Method: method, ReferenceKind: "synthetic", Note: note, Settings: settings));
                    }
                    if (Combo("AUDIOPROCESSING") || family.Contains("audio", StringComparison.OrdinalIgnoreCase))
                    { plan.Unsupported.Add(new { effect_path = path, label = title, kind = "audio_out_of_scope", reason = "音频驱动内容不纳入本轮周期对齐，默认关闭" }); continue; }
                    if (category == "STATIC") continue;
                    switch (family)
                    {
                        case "waterwaves":
                        case "shake":
                        case "spin":
                        case "swing":
                        case "twirl":
                        case "pulse":
                        case "pulse_":
                            if (Combo("NOISE") || (family is "pulse" or "pulse_" && Num(Key("noiseamount", "ui_editor_properties_noise_amount"), 0) != 0 && Num(Key("noisespeed", "ui_editor_properties_noise_speed"), 0) != 0))
                                Rewrite("bounded_replay", "噪声往返重放；所有时间输入在有限范围内平滑往返，运动会反向；不保证原噪声内部没有离散跳变");
                            else { Add("主动画", Key("speed", "ui_editor_properties_speed", "ui_editor_properties_pulse_speed"), 2 * Math.PI); if (Combo("DUALWAVES")) Add("第二波", "speed2", 2 * Math.PI); }
                            break;
                        case "waterflow": Add("流动", Key("speed", "ui_editor_properties_speed")); break;
                        case "shine":
                        case "godrays":
                            double scale = Num("noisescale", 3);
                            if (Combo("NOISE", 1) && scale != 0 && Num("noisespeed", .15) != 0)
                            { values.TryAdd("noisespeed", JsonValue.Create(.15)); Add("噪声滚动", "noisespeed", 2 / Math.Abs(scale), pass: locations.GetValueOrDefault("noisespeed", 0)); }
                            if (family == "shine") { values.TryAdd("speed", JsonValue.Create(0)); Add("光线旋转", "speed", 2 * Math.PI, pass: locations.GetValueOrDefault("speed", 1)); }
                            break;
                        case "scroll":
                            double[] repeat = Vector(values.GetValueOrDefault("repeat") ?? JsonValue.Create("1 1"), 2);
                            for (int axis = 0; axis < 2; axis++) if (repeat[axis] != 0) Add("滚动" + (axis == 0 ? "X" : "Y"), axis == 0 ? "speedx" : "speedy", 1 / Math.Abs(repeat[axis]), 2);
                            break;
                        case "cloudmotion":
                            string cloudSpeed = graph.Defaults.ContainsKey("ui_editor_properties_speed") ? "ui_editor_properties_speed" : "speed";
                            if (!graph.Defaults.ContainsKey(cloudSpeed)) throw new InvalidDataException("云层滚动着色器缺少速度绑定");
                            Add("云层滚动", cloudSpeed);
                            break;
                        case "glitter": double density = Num("density", .5); if (density != 0) Add("闪烁", "speed", 1 / (density * density)); break;
                        case "fire": Add("火焰", "speed", Combo("REFRACT", 1) ? 10 : 5); break;
                        case "shimmer": Add("扫光", "ui_editor_properties_speed", Combo("MODE") ? 2 * Math.PI : Num("ui_editor_properties_granularity") * Num("ui_editor_properties_delay", 2)); break;
                        case "filmgrain":
                            if (Regex.IsMatch(string.Join('\n', graph.Files.Where(p => p.Key.EndsWith(".vert", StringComparison.Ordinal)).Select(p => p.Value)), @"frac\s*\(\s*g_Time\s*\)")) Clock("颗粒相位", 1);
                            else Rewrite("bounded_replay", "旧颗粒往返重放；保留有限噪声过程并反向回放，未宣称原始自然周期");
                            break;
                        case "shift_hue": case "hue_shift": Add("色相轮转", family == "shift_hue" ? "Speed" : "speed"); break;
                        case "lens_flare_sun": Add("主变化", "speed", 2 * Math.PI); Add("次变化", "speed secondary", 2 * Math.PI); Add("旋转", "rotationspeed", 2 * Math.PI); break;
                        case "foliagesway":
                            double speed = Num(Combo("MODE") ? "speed" : "speeduv", Combo("MODE") ? 1 : 5);
                            if (speed != 0) Rewrite("foliage_harmonics", "风动整数谐波；保留空间相位和振幅，改变各分量频率使其闭合", new() { ["speed"] = speed, ["mode"] = Combo("MODE") ? 1 : 0 });
                            break;
                        case "iris":
                            double iris = Num("speed");
                            if (iris != 0) Rewrite("iris_sequence", "闭合注视序列；有限目标首尾插值并同步微动，改变原注视过程", new() { ["speed"] = iris, ["steps"] = Math.Clamp(FrameMath.NearestCycles(Math.Abs(iris) * 20), 2, 4096) });
                            break;
                        case "lightshafts":
                        case "watercaustics":
                        case "vhs":
                        case "crt_screen":
                        case "distortion_glitch":
                        case "procedural_noise":
                            Rewrite("bounded_replay", "有限过程往返重放；保留原公式和空间效果，时间平滑往返；原有hash/step跳变仍可能存在"); break;
                        case "clouds":
                        case "nitro":
                        case "waterripple":
                            Rewrite("bounded_replay", "纹理运动往返重放；时间输入返回同值，避免依赖运行时纹理尺寸与Repeat假设，运动会反向"); break;
                        default: throw new InvalidDataException("此实现需要公式改写或状态重放，尚不能自动计算原始周期");
                    }
                    plan.Covered.Add(SceneJson.Key(path));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException or FormatException or InvalidOperationException or OverflowException)
                {
                    plan.Phases.RemoveRange(first, plan.Phases.Count - first);
                    plan.Unsupported.Add(new { effect_path = path, label = title, reason = error.Message });
                }
            }
        }
        void Timelines(JsonNode? node, string[] path)
        {
            if (node is JsonObject o)
            {
                if (False(Value(o["visible"]))) return;
                if (o["animation"] is JsonObject animation && animation.Count > 0)
                {
                    var options = animation["options"];
                    double? raw = null;
                    try { double auditLength = Number(options?["length"], 0), fps = Number(options?["fps"], 0); if (auditLength > 0 && fps > 0) raw = auditLength / fps; }
                    catch (Exception error) when (error is InvalidDataException or FormatException or OverflowException) { }
                    string auditMode = Text(options?["mode"]); string reason = "端点、插值或绑定未通过周期核查"; bool aligned = false;
                    try
                    {
                        var (origin, mode, length) = TimelinePeriod(o);
                        bool shaderParameter = path.Length >= 7 && path[2] == "effects" && path[5] == "constantshadervalues";
                        if (shaderParameter && plan.Phases.Any(p => p.EffectPath.SequenceEqual(path[..4]) &&
                            (p.Parameter == path[^1] || Regex.IsMatch(path[^1], @"speed|phase|density|scale|repeat|ratio", RegexOptions.IgnoreCase))))
                            throw new InvalidDataException("属性周期已提取；其与着色器时钟的组合尚未验证");
                        plan.Phases.Add(new("timeline:" + string.Join(':', path), "时间线 / " + string.Join(" / ", path), origin, path,
                            Method: "timeline_retime", Note: mode == "loop" ? "保留作者Loop回场；参数首尾可能回跳，需检查画面接缝" : "完整往返周期；保留作者插值及相对参考", Settings: new() { ["mode"] = mode, ["length"] = length }));
                        plan.Covered.Add(Key(path));
                        aligned = true; reason = "已提取并可对齐";
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or FormatException or InvalidOperationException) { reason = error.Message; }
                    plan.Durations.Add(new("timeline", "时间线 / " + string.Join(" / ", path), path, raw, raw.HasValue ? raw * (auditMode == "mirror" ? 2 : 1) : null, auditMode, aligned, reason));
                }
                foreach (var (key, child) in o) if (key is not ("script" or "animation")) Timelines(child, [.. path, key]);
            }
            else if (node is JsonArray a) for (int i = 0; i < a.Count; i++) if (!(path.SequenceEqual(new[] { "objects" }) && excluded.Contains(i))) Timelines(a[i], [.. path, i.ToString(CultureInfo.InvariantCulture)]);
        }
        Timelines(scene, []);
        // Combined property animations share one clock and one editable repetition count.
        foreach (var child in plan.Phases.Where(p => p.Method == "timeline_retime").ToArray())
        {
            var parent = At(scene, child.EffectPath)?["animation"]?["options"]?["parent"];
            if (parent == null) continue;
            string key = Text(parent["key"]); string[] parentPath = [.. child.EffectPath[..^1], key];
            var main = plan.Phases.FirstOrDefault(p => p.Method == "timeline_retime" && p.EffectPath.SequenceEqual(parentPath));
            if (main == null || At(scene, parentPath)?["animation"]?["options"]?["parent"] != null || Math.Abs(main.Origin - child.Origin) > 1e-6 || Text(main.Settings?["mode"]) != Text(child.Settings?["mode"]))
                plan.Covered.Remove(Key(child.EffectPath));
            else
            {
                if (main.Settings!["combined_paths"] is not JsonArray paths) main.Settings["combined_paths"] = paths = [];
                paths.Add(new JsonArray(child.EffectPath.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()));
            }
            plan.Phases.Remove(child);
        }
        TextureTiming.Analyze(objects, bundle, plan);
        plan.Id = Hash(project.Root + scene.ToJsonString() + string.Join(';', plan.Graphs.Select(p => p.Key + p.Value.Signature)) + string.Join(';', bundle.Stamps.Where(p => p.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || p.Key.EndsWith("#timing-tail", StringComparison.Ordinal)).OrderBy(p => p.Key).Select(p => p.Key + p.Value)) + ":3");
        return plan;
    }
    public static string Family(string name)
    {
        string[] parts = name.Replace('\\', '/').Split('/');
        return parts.Length > 1 ? parts[^2] : "unknown";
    }
    public static (double Origin, string Mode, double Length) TimelinePeriod(JsonObject binding)
    {
        var animation = binding["animation"] as JsonObject ?? throw new InvalidDataException("时间线无效");
        var options = animation["options"];
        string mode = Text(options?["mode"]).ToLowerInvariant();
        double length = Number(options?["length"], 0), fps = Number(options?["fps"], 0);
        if (mode == "single") throw new InvalidDataException("一次性动画：保留Single语义，不作为循环周期");
        if (mode is not ("loop" or "mirror") || !double.IsFinite(length) || !double.IsFinite(fps) || length <= 0 || fps <= 0 ||
            Truth(binding["script"]) || Truth(options?["paused"]) || Truth(options?["startpaused"]))
            throw new InvalidDataException("时间线模式、长度、暂停状态或混合绑定未核对");
        if (animation.Any(p => p.Key is not ("options" or "relative" or "previewvalue") && !Regex.IsMatch(p.Key, @"^c\d+$")) ||
            options is JsonObject opt && opt.Any(p => p.Key.Contains("event", StringComparison.OrdinalIgnoreCase) &&
                !(p.Key == "events" && p.Value is JsonArray { Count: 0 })))
            throw new InvalidDataException("时间线含事件或未核对状态");
        if (Truth(animation["relative"]) && Value(binding["value"]) is not JsonValue) throw new InvalidDataException("相对时间线缺少固定参考值");
        var curves = animation.Where(p => Regex.IsMatch(p.Key, @"^c\d+$")).Select(p => p.Value as JsonArray ?? throw new InvalidDataException("时间线曲线无效")).ToArray();
        if (curves.Length == 0) throw new InvalidDataException("没有可验证的时间线曲线");
        foreach (var curve in curves)
        {
            if (curve.Count < 2) throw new InvalidDataException("时间线曲线点不足");
            double[] frames = curve.Select(p => RequiredNumber(p?["frame"])).ToArray();
            double[] values = curve.Select(p => RequiredNumber(p?["value"])).ToArray();
            if (frames[0] < 0 || frames[^1] > length || frames.Zip(frames.Skip(1)).Any(p => p.Second <= p.First)) throw new InvalidDataException("时间线关键帧范围不可靠");
            foreach (var point in curve) foreach (string side in new[] { "front", "back" })
                if (Truth(point?[side]?["enabled"])) { _ = Number(point?[side]?["x"], side == "front" ? 1 : -1); _ = Number(point?[side]?["y"], 0); }
        }
        return (length / fps * (mode == "loop" ? 1 : 2), mode, length);
    }
}
