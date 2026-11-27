using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wallpaper2Video.Core;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

public sealed record ParticleChoice(string Id, string Mode, double? Start = null, double? Stop = null, double? Prewarm = null);
public sealed record ParticleProfile(double Lifetime, double Drain, Dictionary<string, string> Files, List<JsonObject> Definitions, double MinimumEmitWindow, bool AudioEmission = false);

public sealed class ParticlePlan(string mode, double duration, double overlap, int fps)
{
    public const double Guard = .5, ArmBudget = 1;
    public string Mode { get; } = mode;
    public double Duration { get; } = duration;
    public double Overlap { get; } = overlap;
    public int Fps { get; } = fps;
    public List<JsonObject> Layers { get; } = [];
    public List<JsonObject> Unsupported { get; } = [];
    public Dictionary<string, ParticleProfile> Profiles { get; } = [];
    public List<JsonObject> Configure(IEnumerable<ParticleChoice> choices)
    {
        var rows = new List<JsonObject>();
        foreach (var choice in choices.Where(c => c.Mode == "drain" || c.Mode == "original" && c.Prewarm.HasValue))
        {
            int index = int.Parse(choice.Id.Split(':')[1], CultureInfo.InvariantCulture);
            var original = Layers.SingleOrDefault(r => Number(r["index"]) == index) ?? throw new ArgumentException("该粒子层尚不能采用严格首尾留空");
            var row = (JsonObject)original.DeepClone();
            bool natural = choice.Mode == "original";
            double start = natural ? 0 : choice.Start ?? Guard, stop = natural ? Duration : choice.Stop ?? Number(row["emit_stop_after_arm"]) - Overlap;
            if (!double.IsFinite(start) || !double.IsFinite(stop) || start < 0 || stop < 0 || start > Duration || stop > Duration) throw new ArgumentException($"开始和停止发射时间均须在0～{Duration:g}秒之间");
            start = FrameMath.Quantize(start, Fps) / (double)Fps;
            stop = FrameMath.Quantize(stop, Fps) / (double)Fps;
            double latest = Duration - Number(row["drain_bound_seconds"]) - Guard - ArmBudget - Overlap;
            string warning = start >= stop ? "开始时间不早于停止时间，该层本次不会发射粒子。" : stop - start < Number(row["minimum_emit_window_seconds"]) ? "发射区间较短，可能来不及产生粒子。" : "";
            bool safe = start >= stop || stop <= latest + 1e-9;
            if (!safe) warning += "停发时间超过建议值，首尾可能残留粒子，不保证闭合。";
            row["video_emit_start"] = start; row["video_emit_stop"] = stop;
            // Warm particles can be revealed in the trimmed overlap so delivered frame zero is populated.
            row["emit_start_after_arm"] = Overlap + start - (choice.Prewarm > 0 && start == 0 ? Math.Min(Overlap, 2d / Fps) : 0); row["emit_stop_after_arm"] = Overlap + stop;
            row["emission_disabled"] = start >= stop; row["emission_warning"] = warning;
            row["prewarm_seconds"] = choice.Prewarm ?? 0; row["natural_playback"] = natural;
            row["head_empty_expected"] = choice.Prewarm.GetValueOrDefault() == 0 || start > 0;
            row["boundary_wait_required"] = safe && !natural; row["allow_empty_window"] = stop - start < Number(row["minimum_emit_window_seconds"]);
            row["suggested_latest_stop"] = Math.Floor(latest * Fps + 1e-8) / Fps;
            rows.Add(row);
        }
        return rows;
    }
    public object Summary() => new { mode = Mode, duration = Duration, overlap = Overlap, fps = Fps, audio_response = "disabled", fallback_emission_rate = ParticleEmissionPolicy.FallbackRate, managed_layers = Layers, unsupported = Unsupported };
}

public static class ParticlePlanner
{
    private static readonly HashSet<string> Operators = ["movement", "angularmovement", "alphafade", "sizechange", "colorchange", "alphachange", "oscillatealpha", "oscillateposition", "oscillatesize", "oscillatecolor", "turbulence", "boids", "controlpointattract", "controlpointforce", "vortex", "rotationchange", "rotate", "curlnoise"];
    private static readonly HashSet<string> Initializers = ["lifetimerandom", "sizerandom", "velocityrandom", "colorrandom", "alpharandom", "rotationrandom", "angularvelocityrandom", "hsvcolorrandom", "colorlist", "turbulentvelocityrandom", "positionoffsetrandom", "positionaroundcontrolpoint", "positionbetweencontrolpoints", "inheritcontrolpointvelocity"];
    private static double Positive(JsonNode? node, string label, double minimum = 0)
    {
        double result = RequiredNumber(node);
        if (result < minimum) throw new InvalidDataException(label + "必须是有效有限数");
        return result;
    }
    private static JsonNode? Field(JsonNode? node, string key, double fallback) => node is JsonObject obj && obj.ContainsKey(key) ? obj[key] : JsonValue.Create(fallback);
    public static ParticleProfile Profile(AssetBundle bundle, string name, JsonNode? instance = null)
    {
        instance ??= new JsonObject();
        if (Dynamic(instance)) throw new InvalidDataException("粒子实例参数含脚本或时间线，不能确定消亡上界");
        double rate = Positive(Field(instance, "rate", 1), "粒子模拟速率", 1e-6);
        double lifetime = Positive(Field(instance, "lifetime", 1), "粒子寿命倍率", 1e-6);
        double factor = Math.Max(1, lifetime) / Math.Min(1, rate);
        var definitions = new List<JsonObject>();
        var files = new Dictionary<string, string>();
        bool audioEmission = false;
        (double Life, double Bound) Visit(string path, HashSet<string> ancestors, bool child)
        {
            if (ancestors.Contains(path)) throw new InvalidDataException("子粒子依赖形成循环");
            if (ancestors.Count > 16 || files.Count > 128) throw new InvalidDataException("子粒子依赖过深或过多");
            string source = bundle.Read(path);
            var data = Parse(source);
            audioEmission |= ParticleEmissionPolicy.HasAudioResponse(data);
            ParticleEmissionPolicy.Normalize(data, !child);
            files[path] = data.ToJsonString(Output);
            if (Dynamic(data)) throw new InvalidDataException("粒子定义含脚本或动画，不能确定有限消亡");
            if (Array(data["controlpoint"]).Any(cp => ((int)Number(cp?["flags"], 0) & 1) != 0)) throw new InvalidDataException("鼠标粒子必须排除");
            var initializers = Array(data["initializer"]);
            if (initializers.Any(p => !Initializers.Contains(Text(p?["name"])))) throw new InvalidDataException("尚未核对的粒子初始化器");
            var lifetimes = initializers.Where(p => Text(p?["name"]) == "lifetimerandom").ToArray();
            if (lifetimes.Length != 1) throw new InvalidDataException("需要唯一的有限寿命初始化器");
            double lo = Positive(lifetimes[0]?["min"], "最短粒子寿命", 1e-6), hi = Positive(lifetimes[0]?["max"], "最长粒子寿命", 1e-6);
            if (lo > hi) throw new InvalidDataException("粒子寿命范围倒置");
            if (Array(data["operator"]).Any(p => !Operators.Contains(Text(p?["name"])))) throw new InvalidDataException("尚未核对其寿命影响的算子");
            // Native presets may omit renderer entirely; the engine defaults to a sprite.
            string[] renderers = data["renderer"] == null ? ["sprite"] : Array(data["renderer"]).Select(p => Text(p?["name"])).Distinct().Order().ToArray();
            if (renderers.Length == 0 || renderers.Any(r => r is not ("sprite" or "spritetrail" or "ropetrail" or "rope"))) throw new InvalidDataException("粒子渲染器的残留范围尚未核对");
            var emitters = Array(data["emitter"]);
            if (emitters.Count == 0) throw new InvalidDataException("缺少粒子发射器");
            foreach (var emitter in emitters)
            {
                if (Text(emitter?["name"]) is not ("sphererandom" or "boxrandom")) throw new InvalidDataException("发射器类型尚未接入首尾留空");
                if (child && Positive(Field(emitter, "rate", 0), "子粒子发射率") != 0) throw new InvalidDataException("子系统持续自主发射，无法仅暂停根系统后确定清空时间");
                if (child && (Positive(Field(emitter, "delay", 0), "子发射器延迟") != 0 || Positive(Field(emitter, "starttime", 0), "子发射器起始延迟") != 0)) throw new InvalidDataException("延迟子系统尚未接入消亡计算");
            }
            double bound = 0;
            foreach (var link in Array(data["children"]))
            {
                if (Truth(link?["instanceoverride"]) || link is JsonObject loNode && loNode.Any(p => p.Key.Contains("lifetime", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("子系统另有寿命覆盖，尚未接入上界计算");
                if (Text(link?["type"]) is not ("eventfollow" or "eventspawn" or "eventdeath")) throw new InvalidDataException("独立静态子系统不能由根粒子停发保证清空时间");
                var chain = new HashSet<string>(ancestors) { path };
                bound = Math.Max(bound, Visit(Text(link?["name"]), chain, true).Bound);
            }
            definitions.Add(new() { ["file"] = path, ["lifetime_min"] = lo, ["lifetime_max"] = hi, ["renderers"] = new JsonArray(renderers.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()), ["child_drain_bound"] = bound });
            return (hi, hi + bound);
        }
        var profile = Visit(name, [], false);
        double count = Positive(Field(instance, "count", 1), "粒子数量倍率", 1e-6);
        var waits = new List<double>();
        foreach (var emitter in Array(Parse(files[name])["emitter"]))
        {
            double emission = Positive(Field(emitter, "rate", 0), "发射率") * Math.Min(1, count) * Math.Min(1, rate);
            if (emission != 0) waits.Add(Positive(Field(emitter, "delay", 0), "发射器延迟") + 1 / emission + .1);
        }
        if (waits.Count == 0) throw new InvalidDataException("仅瞬发或无持续发射的系统尚未接入此模式");
        return new(profile.Life * factor, profile.Bound * factor, files, definitions, Math.Max(1, waits.DefaultIfEmpty(1).Min()), audioEmission);
    }
    public static ParticlePlan Analyze(ProjectSnapshot project, AssetBundle bundle, double duration, int fps, double overlap, string mode)
    {
        if (mode is not ("drain" or "off" or "original")) throw new ArgumentException("粒子模式应为drain、off或original");
        overlap = FrameMath.Quantize(overlap, fps) / (double)fps;
        var plan = new ParticlePlan(mode, duration, overlap, fps);
        var scene = project.Scene == null ? new JsonObject() : Parse(project.Scene.Value.GetRawText());
        var objects = Array(scene["objects"]);
        string[] ids = objects.Select(o => Text(o?["id"], "None")).ToArray();
        for (int index = 0; index < objects.Count; index++)
        {
            var obj = objects[index]!;
            if (!Truth(obj["particle"])) continue;
            string label = Text(obj["name"], Text(obj["id"], index.ToString(CultureInfo.InvariantCulture)));
            try
            {
                string id = Text(obj["id"]);
                if (!Regex.IsMatch(id, @"^\d+$") || ids.Count(v => v == id) != 1) throw new InvalidDataException("粒子图层ID缺失或重复，无法定向控制");
                var own = (JsonObject)obj.DeepClone(); own.Remove("effects");
                if (Dynamic(own)) throw new InvalidDataException("粒子图层含脚本或时间线，不能覆盖其生命周期控制");
                var ancestry = new HashSet<string> { id };
                JsonNode? parent = obj["parent"];
                while (Value(parent) != null)
                {
                    string pid = Text(Value(parent));
                    if (!ancestry.Add(pid)) throw new InvalidDataException("粒子父层依赖形成循环");
                    var ancestor = objects.FirstOrDefault(o => Text(o?["id"], "None") == pid) ?? throw new InvalidDataException("粒子父层依赖无法核对");
                    if (Dynamic(ancestor)) throw new InvalidDataException("粒子父层含脚本或时间线，需单独核对生命周期");
                    parent = ancestor["parent"];
                }
                var profile = Profile(bundle, Text(obj["particle"]), obj["instanceoverride"]);
                double start = overlap + Math.Min(ParticlePlan.Guard, duration), stop = duration - profile.Drain - ParticlePlan.Guard - ParticlePlan.ArmBudget;
                // Being too short for a guaranteed empty tail must not disable manual timing.
                // Configure() reports the actual boundary risk for the chosen interval.
                stop = Math.Min(duration + overlap, Math.Max(stop, start + Math.Max(profile.MinimumEmitWindow, 2d / fps)));
                plan.Layers.Add(new()
                {
                    ["index"] = index,
                    ["id"] = id,
                    ["label"] = label,
                    ["source"] = Text(obj["particle"]),
                    ["lifetime_bound_seconds"] = profile.Lifetime,
                    ["drain_bound_seconds"] = profile.Drain,
                    ["minimum_emit_window_seconds"] = profile.MinimumEmitWindow,
                    ["simulation_rate"] = Number(obj["instanceoverride"]?["rate"], 1),
                    ["instance_alpha"] = Number(obj["instanceoverride"]?["alpha"], 1),
                    ["pre_simulation_reset"] = true,
                    ["initial_burst_disabled"] = true,
                    ["emit_start_after_arm"] = start,
                    ["emit_stop_after_arm"] = stop,
                    ["audio_emission"] = false,
                    ["source_audio_response"] = profile.AudioEmission,
                    ["audio_response_disabled"] = true,
                    ["fixed_emitter_rates"] = new JsonArray(Array(Parse(profile.Files[Text(obj["particle"])])["emitter"]).Select(e => (JsonNode?)JsonValue.Create(Number(Value(e?["rate"])))).ToArray()),
                    ["definitions"] = new JsonArray(profile.Definitions.Select(d => d.DeepClone()).ToArray())
                });
                plan.Profiles[id] = profile;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or FormatException or InvalidOperationException or JsonException)
            { plan.Unsupported.Add(new() { ["index"] = index, ["label"] = label, ["reason"] = error.Message }); }
        }
        return plan;
    }
    public static string Controller(string job, string key, IReadOnlyList<JsonObject> rows, JsonArray? clocks = null, double overlap = 0)
    {
        using var stream = typeof(ParticlePlanner).Assembly.GetManifestResourceStream("Wallpaper2Video.particle-controller.js") ?? throw new InvalidDataException("缺少粒子控制资源");
        using var reader = new StreamReader(stream);
        var entries = rows.Select(r => new
        {
            id = Text(r["id"]),
            start = Number(r["emit_start_after_arm"]),
            stop = Number(r["emit_stop_after_arm"]),
            drain = Number(r["drain_bound_seconds"]),
            warm = Number(r["prewarm_seconds"], 0),
            config = r["spawn_config"]?.DeepClone(),
            rate = Number(r["simulation_rate"], 1),
            alpha = Number(r["instance_alpha"], 1),
            natural = Truth(r["natural_playback"]),
            disabled = Truth(r["emission_disabled"]),
            safe = r["boundary_wait_required"] == null || Truth(r["boundary_wait_required"]),
            allowEmpty = Truth(r["allow_empty_window"]),
            stage = 0
        });
        return reader.ReadToEnd().Replace("ENTRIES", JsonSerializer.Serialize(entries), StringComparison.Ordinal)
            .Replace("CLOCKS", clocks?.ToJsonString() ?? "[]", StringComparison.Ordinal).Replace("OVERLAP", overlap.ToString("R", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("KEY", JsonSerializer.Serialize(key), StringComparison.Ordinal).Replace("PREFIX", JsonSerializer.Serialize("W2V_PARTICLE:" + job + " "), StringComparison.Ordinal);
    }
    public static JsonObject? Prepare(JsonObject scene, JsonObject metadata, string root, ParticlePlan plan, IReadOnlyList<ParticleChoice> choices, ScenePlan? scenePlan = null)
    {
        var objects = Array(scene["objects"]).ToDictionary(o => Text(o?["id"], "None"), o => o!);
        var rows = plan.Configure(choices).Where(r => objects.ContainsKey(Text(r["id"]))).ToList();
        var clocks = CaptureClocks.Build(scenePlan, scene);
        if (rows.Count == 0 && clocks.Count == 0) return null;
        if (HasScript(scene)) throw new InvalidDataException("播放同步、粒子定时或独立预热不能与保留的用户脚本混用；请关闭这些待选脚本");
        string job = Hash(JsonSerializer.Serialize(rows) + root + plan.Duration.ToString("R", CultureInfo.InvariantCulture))[..16], key = "w2v_particle_" + job;
        foreach (var row in rows)
        {
            string id = Text(row["id"]);
            var profile = plan.Profiles[id];
            var mapping = profile.Files.Keys.ToDictionary(n => n, n => "particles/w2v_" + job + "_" + id + "/" + (n.StartsWith("particles/", StringComparison.Ordinal) ? n[10..] : n));
            foreach (var (name, source) in profile.Files)
            {
                var data = Parse(source); data["starttime"] = name == Text(row["source"]) ? Number(row["prewarm_seconds"], 0) : 0;
                if (name == Text(row["source"])) foreach (var emitter in Array(data["emitter"])) emitter!["instantaneous"] = 0;
                foreach (var child in Array(data["children"])) if (mapping.TryGetValue(Text(child?["name"]), out string? target)) child!["name"] = target;
                string destination = SafeFiles.Member(root, mapping[name]); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.WriteAllText(destination, data.ToJsonString(Output));
            }
            objects[id]["particle"] = mapping[Text(row["source"])];
            if (Number(row["prewarm_seconds"], 0) > 0)
            {
                var config = (JsonObject)objects[id].DeepClone(); config.Remove("id"); config["visible"] = true;
                row["spawn_config"] = config; row["prewarm_method"] = "native_preset_on_creation"; row["pre_simulation_reset"] = false; row["prewarm_unit"] = "native simulation seconds";
            }
        }
        var host = rows.Count > 0 ? objects[Text(rows[0]["id"])] : objects.Values.First(o => !False(Value(o["visible"])) && Truth(o["image"]));
        host["visible"] = new JsonObject { ["value"] = true, ["script"] = Controller(job, key, rows, clocks, plan.Overlap) };
        if (metadata["general"] is not JsonObject general) metadata["general"] = general = new();
        if (general["properties"] is not JsonObject properties) general["properties"] = properties = new();
        properties[key] = new JsonObject { ["type"] = "slider", ["value"] = 0, ["min"] = 0, ["max"] = 2, ["step"] = 1, ["text"] = "粒子录制控制（由导出器管理）" };
        properties[key + "_offset"] = new JsonObject { ["type"] = "slider", ["value"] = 0, ["min"] = 0, ["max"] = 5, ["step"] = .001, ["text"] = "录制首帧同步" };
        var result = JsonSerializer.SerializeToNode(plan.Summary(), Output)!.AsObject();
        result["job"] = job; result["property"] = key; result["managed_layers"] = new JsonArray(rows.Select(r => r.DeepClone()).ToArray());
        result["telemetry_prefix"] = "W2V_PARTICLE:" + job + " "; result["window_playback_rate"] = 100;
        result["clocks"] = clocks; result["prewarm_seconds"] = rows.Select(r => Number(r["prewarm_seconds"], 0)).DefaultIfEmpty(0).Max();
        File.WriteAllText(Path.Combine(root, ".w2v-particles.json"), result.ToJsonString(Output));
        return result;
    }
    public static JsonObject ValidateTelemetry(IReadOnlyList<JsonObject> events, IReadOnlyList<JsonObject> rows, double tailSeconds)
    {
        var stages = events.Select(e => Text(e["stage"])).ToHashSet();
        var required = new HashSet<string> { "ready", "armed", "drain_wait_completed", "verify" };
        if (rows.Any(r => !Truth(r["allow_empty_window"]) && !False(r["boundary_wait_required"]) && !Truth(r["emission_disabled"]))) required.UnionWith(["emitting", "paused"]);
        if (rows.Any(r => Truth(r["emission_disabled"]))) required.Add("emission_skipped");
        if (!required.IsSubsetOf(stages)) throw new InvalidDataException("原生粒子控制未完成状态确认：" + string.Join(", ", required.Except(stages)));
        if (stages.Contains("missed_window")) throw new InvalidDataException("原生帧步跨过发射窗口，不能交付本次粒子视频");
        bool[] started = rows.Select(r => Truth(r["emission_disabled"]) || Truth(r["allow_empty_window"]) || False(r["boundary_wait_required"])).ToArray();
        foreach (var e in events)
        {
            var active = Array(e["active"]);
            if (active.Count != rows.Count) throw new InvalidDataException("原生粒子状态的图层数不一致");
            if (Text(e["stage"]) == "emitting") for (int i = 0; i < started.Length; i++) started[i] |= Truth(active[i]);
        }
        if (started.Any(s => !s)) throw new InvalidDataException("部分原生粒子图层未确认启动发射");
        var verification = events.Last(e => Text(e["stage"]) == "verify");
        var safe = rows.Select((r, i) => (r, i)).Where(p => !False(p.r["boundary_wait_required"])).ToArray();
        double expected = safe.Where(p => !Truth(p.r["emission_disabled"])).Select(p => Number(p.r["emit_stop_after_arm"]) + Number(p.r["drain_bound_seconds"])).DefaultIfEmpty(0).Max();
        if ((expected != 0 && Number(verification["elapsed"]) < expected + tailSeconds) || safe.Any(p => Truth(Array(verification["active"])[p.i]))) throw new InvalidDataException("录制结束时原生粒子仍活动或消亡等待不足");
        return new()
        {
            ["native_events"] = new JsonArray(events.Select(e => e.DeepClone()).ToArray()),
            ["verified_wait_complete"] = safe.Length == rows.Count,
            ["native_playback_inactive_at_end"] = !Array(verification["active"]).Any(Truth),
            ["boundary_risk_layers"] = new JsonArray(rows.Where(r => False(r["boundary_wait_required"])).Select(r => r["id"]?.DeepClone()).ToArray()),
            ["pixel_empty_verified"] = false,
            ["note"] = "原生状态信号不替代隔离背景像素验证。"
        };
    }
}
