using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Wallpaper2Video.Core;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

public sealed record ReviewItem(string Id, string Label, string Reason, string Kind, ImmutableArray<string> Path, string Action, bool CanEnable = true);
public sealed class SceneSelection
{
    internal SceneSelection(string planId, HashSet<string> enabled, List<ParticleChoice> particles,
        HashSet<string> disabledEffects, Dictionary<int, bool> sourceVisibility)
    {
        PlanId = planId;
        Enabled = enabled.ToFrozenSet(StringComparer.Ordinal);
        Particles = [.. particles];
        DisabledEffects = disabledEffects.ToFrozenSet(StringComparer.Ordinal);
        SourceVisibility = new ReadOnlyDictionary<int, bool>(sourceVisibility);
    }

    public string PlanId { get; }
    public IReadOnlySet<string> Enabled { get; }
    public ImmutableArray<ParticleChoice> Particles { get; }
    public IReadOnlySet<string> DisabledEffects { get; }
    public IReadOnlyDictionary<int, bool> SourceVisibility { get; }
}
public sealed record SourceLayerControl(int Index, string Name, string Kind, string Source, bool DefaultVisible, bool CanToggle, string Reason);
public sealed record ParticleControl(string Id, int Index, string Label, bool CanDrain, bool CanOriginal, string DefaultMode,
    double DefaultStart, double? DefaultStop, double? DrainSeconds, string Reason);
public sealed record SceneTarget(string Id, string Label, string Kind, string[] Path, string Blocked = "");

public sealed class ScenePlan
{
    internal ScenePlan(ProjectSnapshot project, AssetBundle bundle, AlignmentPlan alignment, ExportSettings settings,
        IReadOnlyDictionary<string, int> cycles)
    {
        Project = project;
        Bundle = bundle;
        Alignment = alignment;
        Settings = settings;
        Cycles = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(cycles));
    }

    public ProjectSnapshot Project { get; }
    public AssetBundle Bundle { get; }
    public AlignmentPlan Alignment { get; }
    public ExportSettings Settings { get; }
    public IReadOnlyDictionary<string, int> Cycles { get; }
    public double Duration => (double)Settings.Frames / Settings.Fps;
    public JsonObject? Scene => (JsonObject?)SceneDocument?.DeepClone();
    public bool HasEntrance => Array(SceneDocument?["objects"]).Any(obj => obj != null && EntrancePolicy.Identified(obj));
    public IReadOnlyList<ReviewItem> Required { get; internal init; } = ImmutableArray<ReviewItem>.Empty;
    public IReadOnlyList<ReviewItem> Optional { get; internal init; } = ImmutableArray<ReviewItem>.Empty;
    public ParticlePlan ParticlePlan { get; internal init; } = null!;
    public string Id { get; internal init; } = "";
    internal JsonObject? SceneDocument { get; init; }

    public double RecommendedDuration(SceneSelection selection)
    {
        var removed = RemovedLayers(selection);
        double? video = Alignment.EmbeddedVideos.Where(v => v.Visible && !removed.Contains(v.ObjectIndex))
            .Select(v => (double?)v.Timing.Duration).Max();
        return video ?? Alignment.Phases.Where(p => p.Method is "skeletal_rate" or "timeline_retime" &&
            !selection.DisabledEffects.Contains(p.Id) && p.EffectPath.Length > 1 && p.EffectPath[0] == "objects" &&
            !removed.Contains(int.Parse(p.EffectPath[1], CultureInfo.InvariantCulture)))
            .Select(p => p.Origin).DefaultIfEmpty(20).Max();
    }

    public IReadOnlyList<ParticleControl> ParticleControls()
    {
        var result = new List<ParticleControl>();
        var objects = Array(SceneDocument?["objects"]);
        var blocked = Required.Where(i => i.Action == "remove_layer").Select(i => int.Parse(i.Path[1], CultureInfo.InvariantCulture)).ToHashSet();
        for (int i = 0; i < objects.Count; i++)
        {
            var obj = objects[i]!;
            if (!Truth(obj["particle"])) continue;
            var candidate = ParticlePlan.Layers.FirstOrDefault(r => Number(r["index"]) == i);
            string reason = Text(ParticlePlan.Unsupported.FirstOrDefault(r => Number(r["index"]) == i)?["reason"], blocked.Contains(i) ? "鼠标粒子自动排除" : "");
            var item = Optional.FirstOrDefault(r => r.Kind == "particle" && r.Path[1] == i.ToString(CultureInfo.InvariantCulture));
            bool raw = !blocked.Contains(i) && (item == null || item.CanEnable);
            string mode = False(Value(obj["visible"])) ? "off" : Settings.ParticleMode;
            if (!raw || (mode == "drain" && candidate == null)) mode = "off";
            result.Add(new($"particle:{i}", i, Text(obj["name"], Text(obj["id"], i.ToString(CultureInfo.InvariantCulture))), candidate != null && raw, raw, mode, Math.Min(.5, Duration),
                candidate == null ? null : Math.Floor((Number(candidate["emit_stop_after_arm"]) - ParticlePlan.Overlap) * Settings.Fps + 1e-8) / Settings.Fps,
                candidate == null ? null : Number(candidate["drain_bound_seconds"]), reason));
        }
        return result;
    }
    public IReadOnlyList<SourceLayerControl> SourceLayers()
    {
        var objects = Array(SceneDocument?["objects"]); var blocked = Required.Where(r => r.Action == "remove_layer").Concat(Optional.Where(r => r.Action == "remove_layer" && !r.CanEnable)).ToArray();
        var accessibleParticles = ParticleControls().Where(control => control.CanOriginal).Select(control => control.Index).ToHashSet();
        var result = new List<SourceLayerControl>();
        for (int i = 0; i < objects.Count; i++)
        {
            var obj = objects[i]!; bool visible = !False(Value(obj["visible"]));
            string reason = blocked.FirstOrDefault(r => r.Path.Length == 2 && r.Path[1] == i.ToString(CultureInfo.InvariantCulture))?.Reason ?? "";
            if (!visible && reason.Length == 0 && Alignment.HiddenRenderTargetLayers.Contains(i)) reason = "作为其它效果的渲染输入，保留原图层的隐藏状态";
            if (!visible && reason.Length == 0 && !accessibleParticles.Contains(i)) reason = "原素材中已隐藏，未纳入启用检查";
            string kind = Truth(obj["particle"]) ? "particle" : Truth(obj["sound"]) ? "sound" : Truth(obj["image"]) ? "image" : Truth(obj["text"]) ? "text" : Truth(obj["model"]) || Truth(obj["instance"]) ? "model" : "folder";
            string source = Text(obj["particle"] ?? obj["image"] ?? obj["model"] ?? obj["instance"] ?? obj["sound"], kind == "text" ? Text(Value(obj["text"])) : "");
            result.Add(new(i, Text(obj["name"], Text(obj["id"], "对象 " + (i + 1))), kind, source, visible, reason.Length == 0, reason));
        }
        return result;
    }
    public SceneSelection Select(IEnumerable<string>? enabled = null, IEnumerable<ParticleChoice>? particles = null, IEnumerable<string>? disabledEffects = null, IReadOnlyDictionary<int, bool>? sourceVisibility = null)
    {
        var selected = enabled?.ToHashSet() ?? [];
        if (enabled == null)
            foreach (var item in Optional.Where(i => i.Kind == "model" && i.CanEnable))
            {
                var durations = Alignment.Durations.Where(d => d.Kind == "skeletal" && Prefix(item.Path, d.Path)).ToArray();
                if (durations.Any(d => d.Aligned) && durations.All(d => d.Aligned ||
                    (!Settings.PreserveEntrance && Alignment.SuppressedBones.Any(p => p.SequenceEqual(d.Path))) ||
                    d.Reason.StartsWith("动画层已隐藏", StringComparison.Ordinal) || d.Reason == "静止骨骼保持原姿态")) selected.Add(item.Id);
            }
        var disabled = disabledEffects?.ToHashSet() ?? [];
        var effectIds = new HashSet<string>();
        var objects = Array(SceneDocument?["objects"]);
        for (int oi = 0; oi < objects.Count; oi++) for (int ei = 0; ei < Array(objects[oi]?["effects"]).Count; ei++) effectIds.Add($"effect:{oi}:{ei}");
        effectIds.UnionWith(Alignment.Phases.Where(p => p.Method is "timeline_retime" or "skeletal_rate").Select(p => p.Id));
        if (disabled.Except(effectIds).Any()) throw new ArgumentException("未知的效果或时间线开关ID");
        selected.ExceptWith(disabled);
        var controls = ParticleControls();
        var input = particles?.ToList() ?? [];
        if (input.Select(p => p.Id).Distinct().Count() != input.Count) throw new ArgumentException("粒子设置ID重复");
        if (input.Select(p => p.Id).Except(controls.Select(c => c.Id)).Any()) throw new ArgumentException("未知粒子设置ID");
        var resolved = new List<ParticleChoice>();
        foreach (var row in controls)
        {
            string defaultMode = selected.Contains(row.Id) && row.DefaultMode == "off" ? "original" : row.DefaultMode;
            var choice = input.SingleOrDefault(p => p.Id == row.Id) ?? new ParticleChoice(row.Id, defaultMode);
            if (choice.Mode is not ("drain" or "off" or "original")) throw new ArgumentException("未知粒子模式");
            if (choice.Prewarm is double warm && (!double.IsFinite(warm) || warm < 0 || warm > 120)) throw new ArgumentException("粒子预热时间须在0～120秒之间");
            if (choice.Prewarm.HasValue && choice.Mode != "off" && !row.CanDrain) throw new ArgumentException("该粒子尚未核对独立预热能力：" + row.Reason);
            if (choice.Mode == "drain" && !row.CanDrain) throw new ArgumentException(row.Label + "暂不支持定时发射：" + row.Reason);
            if (choice.Mode == "original" && !row.CanOriginal) throw new ArgumentException(row.Label + "不能绕过强制排除规则");
            if (choice.Mode != "drain" && (choice.Start.HasValue || choice.Stop.HasValue)) throw new ArgumentException("发射时间只用于自定义发射模式");
            if (choice.Mode == "drain")
            {
                double start = choice.Start ?? row.DefaultStart, stop = choice.Stop ?? row.DefaultStop!.Value;
                if (!double.IsFinite(start) || !double.IsFinite(stop) || start < 0 || stop < 0 || start > Duration || stop > Duration) throw new ArgumentException($"开始和停止发射时间均须在0～{Duration:g}秒之间");
                choice = choice with { Start = FrameMath.Quantize(start, Settings.Fps) / (double)Settings.Fps, Stop = FrameMath.Quantize(stop, Settings.Fps) / (double)Settings.Fps };
            }
            if (Optional.Any(i => i.Id == choice.Id)) { if (choice.Mode == "original") selected.Add(choice.Id); else selected.Remove(choice.Id); }
            resolved.Add(choice);
        }
        ParticlePlan.Configure(resolved);
        if (selected.Except(Optional.Select(i => i.Id)).Any()) throw new ArgumentException("无效或不能重新启用的项目");
        if (Optional.Any(i => !i.CanEnable && selected.Contains(i.Id))) throw new ArgumentException("这些项目尚不能应用强制交互排除规则，暂不可启用");
        var visibility = sourceVisibility?.ToDictionary(p => p.Key, p => p.Value) ?? [];
        if (visibility.Keys.Any(i => i < 0 || i >= objects.Count)) throw new ArgumentException("未知原素材对象");
        if (visibility.Values.Any(v => v))
        {
            var sourceControls = SourceLayers().ToDictionary(r => r.Index);
            if (visibility.Any(p => p.Value && !sourceControls[p.Key].CanToggle)) throw new ArgumentException("不能启用已排除或未检查的原素材对象");
        }
        return new(Id, selected, resolved, disabled, visibility);
    }
    public void Validate(SceneSelection selection)
    {
        if (selection.PlanId != Id) throw new ArgumentException("素材或配置已变化，请重新检查导出清单");
        _ = Select(selection.Enabled, selection.Particles, selection.DisabledEffects, selection.SourceVisibility);
    }
    public IReadOnlyList<SceneTarget> Targets(bool includeReview = true)
    {
        var targets = new List<SceneTarget>();
        var objects = Array(SceneDocument?["objects"]);
        var denied = Required.Where(i => i.Action is "remove_layer" or "remove_effect").Concat(Optional.Where(i => !i.CanEnable)).ToArray();
        void Add(string id, string label, string kind, string[] path, JsonNode obj)
        {
            string reason = denied.FirstOrDefault(i => Prefix(i.Path, path))?.Reason ?? "";
            var seen = new HashSet<string>();
            JsonNode? parent = Value(obj["parent"]);
            while (parent != null && seen.Add(Text(parent)))
            {
                int ai = Enumerable.Range(0, objects.Count).FirstOrDefault(i => Text(objects[i]?["id"]) == Text(parent), -1);
                if (ai < 0) break;
                reason = reason.Length > 0 ? reason : denied.FirstOrDefault(i => i.Path.SequenceEqual(new[] { "objects", ai.ToString(CultureInfo.InvariantCulture) }))?.Reason ?? "";
                parent = Value(objects[ai]?["parent"]);
            }
            targets.Add(new(id, label, kind, [.. path], reason));
        }
        for (int oi = 0; oi < objects.Count; oi++)
        {
            var obj = objects[oi]!;
            if (False(Value(obj["visible"])) && !Alignment.HiddenRenderTargetLayers.Contains(oi)) continue;
            string title = Text(obj["name"], Text(obj["id"], oi.ToString(CultureInfo.InvariantCulture)));
            if (Truth(obj["particle"])) Add($"particle:{oi}", $"{title} · 图层{oi + 1}", "particle", ["objects", oi.ToString(CultureInfo.InvariantCulture)], obj);
            if (Alignment.Phases.Any(p => p.Id == $"video:{oi}")) Add($"video:{oi}", $"{title} / 内嵌视频", "video", ["objects", oi.ToString(CultureInfo.InvariantCulture)], obj);
            if (Alignment.Phases.Any(p => p.Id == $"sprite:{oi}")) Add($"sprite:{oi}", $"{title} / 序列贴图", "sprite", ["objects", oi.ToString(CultureInfo.InvariantCulture)], obj);
            foreach (var phase in Alignment.Phases.Where(p => p.Method == "skeletal_rate" && p.EffectPath[1] == oi.ToString(CultureInfo.InvariantCulture)))
                Add(phase.Id, phase.Label, "bone", phase.EffectPath, obj);
            foreach (var phase in Alignment.Phases.Where(p => p.Method == "timeline_retime" && p.EffectPath.Length > 1 && p.EffectPath[0] == "objects" && p.EffectPath[1] == oi.ToString(CultureInfo.InvariantCulture)))
                Add(phase.Id, $"{title} / {SceneBrowser.TimelineName(phase)}", "timeline", phase.EffectPath, obj);
            var effects = Array(obj["effects"]);
            for (int ei = 0; ei < effects.Count; ei++)
                if (!False(Value(effects[ei]?["visible"]))) Add($"effect:{oi}:{ei}", $"{AlignmentPlanner.Family(Text(effects[ei]?["file"]))} / {title} · {oi + 1}.{ei + 1}", "effect", ["objects", oi.ToString(CultureInfo.InvariantCulture), "effects", ei.ToString(CultureInfo.InvariantCulture)], obj);
        }
        if (includeReview) foreach (var item in Optional) if (targets.All(t => t.Id != item.Id)) targets.Add(new(item.Id, item.Label, item.Kind, [.. item.Path], item.CanEnable ? "" : item.Reason));
        return targets;
    }
    public HashSet<int> RemovedLayers(SceneSelection choice)
        => RemovedLayers(choice, Exclusions(choice));

    private HashSet<int> RemovedLayers(SceneSelection choice, IEnumerable<ReviewItem> exclusions)
    {
        var removed = exclusions.Where(i => i.Action == "remove_layer").Select(i => int.Parse(i.Path[1], CultureInfo.InvariantCulture)).ToHashSet();
        removed.UnionWith(choice.Particles.Where(p => p.Mode == "off").Select(p => int.Parse(p.Id.Split(':')[1], CultureInfo.InvariantCulture)));
        removed.UnionWith(choice.SourceVisibility.Where(p => !p.Value).Select(p => p.Key));
        AlignmentPlanner.ExpandChildren(Array(SceneDocument?["objects"]), removed);
        return removed;
    }
    private IEnumerable<ReviewItem> Exclusions(SceneSelection choice) => Required.Concat(Optional.Where(item => !choice.Enabled.Contains(item.Id)));

    private static HashSet<(int Oi, int Ei)> RemovedEffects(SceneSelection choice, IEnumerable<ReviewItem> exclusions)
    {
        var slots = exclusions.Where(item => item.Action == "remove_effect")
            .Select(item => (int.Parse(item.Path[1], CultureInfo.InvariantCulture), int.Parse(item.Path[3], CultureInfo.InvariantCulture)))
            .ToHashSet();
        foreach (string id in choice.DisabledEffects.Where(id => id.StartsWith("effect:", StringComparison.Ordinal)))
        {
            string[] parts = id.Split(':');
            slots.Add((int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture)));
        }
        return slots;
    }

    public HashSet<int> InactiveRenderTargetLayers(SceneSelection choice)
    {
        if (Alignment.HiddenRenderTargetLayers.Count == 0) return [];
        var exclusions = Exclusions(choice).ToArray();
        return InactiveRenderTargetLayers(choice, RemovedLayers(choice, exclusions), RemovedEffects(choice, exclusions));
    }

    private HashSet<int> InactiveRenderTargetLayers(SceneSelection choice, HashSet<int> removed, HashSet<(int Oi, int Ei)> removedEffects)
    {
        var inactive = Alignment.HiddenRenderTargetLayers.ToHashSet();
        if (inactive.Count == 0) return inactive;

        var objects = Array(SceneDocument?["objects"]);
        var hidden = removed.ToHashSet();
        var shownParticles = choice.Particles.Where(p => p.Mode != "off")
            .Select(p => int.Parse(p.Id.Split(':')[1], CultureInfo.InvariantCulture)).ToHashSet();
        for (int i = 0; i < objects.Count; i++)
            if (!choice.SourceVisibility.GetValueOrDefault(i, !False(Value(objects[i]?["visible"])) || shownParticles.Contains(i))) hidden.Add(i);
        AlignmentPlanner.ExpandChildren(objects, hidden);

        var visible = Enumerable.Range(0, objects.Count).Where(i => !hidden.Contains(i)).ToHashSet();
        inactive.ExceptWith(visible);
        inactive.ExceptWith(RenderTargetInput.Reachable(Alignment.RenderTargetInputs, visible, removed, removedEffects));
        return inactive;
    }

    internal ResolvedSceneContent ResolveContent(SceneSelection choice)
    {
        var exclusions = Exclusions(choice).ToArray();
        var removedLayers = RemovedLayers(choice, exclusions);
        var removedEffects = RemovedEffects(choice, exclusions);
        var inactiveInputs = InactiveRenderTargetLayers(choice, removedLayers, removedEffects);
        var disabledPhases = Alignment.Phases.Where(p => choice.DisabledEffects.Contains(p.Id)).ToArray();
        var stoppedBones = disabledPhases.Where(p => p.Method == "skeletal_rate").Select(p => p.EffectPath);
        if (!Settings.PreserveEntrance) stoppedBones = stoppedBones.Concat(Alignment.SuppressedBones);
        var stoppedTimelines = disabledPhases.Where(p => p.Method == "timeline_retime")
            .SelectMany(p => new[] { p.EffectPath }.Concat(Array(p.Settings?["combined_paths"]).Select(path => Array(path).Select(v => Text(v)).ToArray())))
            .Select(path => new ReviewItem("disabled:" + Key(path), "关闭的运动时间线", "用户关闭", "timeline", [.. path], "strip_animation"));
        var edits = exclusions.Where(item => item.Action is "strip_script" or "strip_animation" or "false")
            .Concat(stoppedTimelines).OrderByDescending(item => item.Path.Length).ToArray();
        var unappliedPaths = edits.Where(item => item.Action == "strip_animation").Select(item => (IReadOnlyList<string>)item.Path).Concat(stoppedBones).ToArray();
        bool Applies(PeriodPhase phase)
        {
            var path = phase.EffectPath;
            if (choice.DisabledEffects.Contains(phase.Id) || unappliedPaths.Any(removed => Prefix(removed, path))) return false;
            if (path.Length < 2 || path[0] != "objects") return true;
            int layer = int.Parse(path[1], CultureInfo.InvariantCulture);
            return !removedLayers.Contains(layer) && !inactiveInputs.Contains(layer) &&
                (path.Length < 4 || path[2] != "effects" || !removedEffects.Contains((layer, int.Parse(path[3], CultureInfo.InvariantCulture))));
        }

        return new(removedLayers, removedEffects, inactiveInputs, edits, stoppedBones.ToArray(),
            Alignment.Phases.Where(Applies).Select(phase => phase.Id).ToHashSet());
    }

    public IReadOnlySet<string> AppliedPhaseIds(SceneSelection choice) => ResolveContent(choice).AppliedPhaseIds;

    public object Summary(SceneSelection choice)
    {
        Validate(choice);
        var content = ResolveContent(choice);
        var removed = content.RemovedLayers.Concat(content.InactiveInputs).ToHashSet();
        return new
        {
            policy_version = 3,
            review_id = Id,
            duration = Duration,
            required_exclusions = Required.Select(i => new { id = i.Id, label = i.Label, reason = i.Reason, kind = i.Kind, can_enable = false }),
            optional_items = Optional.Select(i => new
            {
                id = i.Id,
                label = i.Label,
                reason = i.Reason,
                kind = i.Kind,
                can_enable = i.CanEnable,
                selected = choice.Enabled.Contains(i.Id),
                enabled = choice.Enabled.Contains(i.Id) && !(i.Path.Length > 1 && i.Path[0] == "objects" && removed.Contains(int.Parse(i.Path[1], CultureInfo.InvariantCulture)))
            }),
            disabled_effects = choice.DisabledEffects.Where(k => k.StartsWith("effect:", StringComparison.Ordinal)).Order().ToArray(),
            disabled_timelines = choice.DisabledEffects.Where(k => k.StartsWith("timeline:", StringComparison.Ordinal)).Order().ToArray(),
            source_visibility = choice.SourceVisibility,
            source_layers = SourceLayers(),
            particle_control = new
            {
                mode = "per_layer",
                duration = Duration,
                overlap = ParticlePlan.Overlap,
                fps = Settings.Fps,
                controls = ParticleControls(),
                managed_layers = ParticlePlan.Configure(choice.Particles).Select(r => { var copy = (JsonObject)r.DeepClone(); copy["enabled"] = !removed.Contains((int)Number(r["index"])); return copy; }),
                unsupported = ParticlePlan.Unsupported,
                layer_choices = choice.Particles
            },
            notes = new[] { "待选项目默认不启用；勾选表示接受其可能不闭合，导出仍会处理首尾混合。", "已知相位匹配不代表原引擎逐像素闭合。" }
        };
    }
}

internal sealed record ResolvedSceneContent(
    HashSet<int> RemovedLayers,
    HashSet<(int Oi, int Ei)> RemovedEffects,
    HashSet<int> InactiveInputs,
    IReadOnlyList<ReviewItem> PropertyEdits,
    IReadOnlyList<string[]> StoppedBones,
    IReadOnlySet<string> AppliedPhaseIds);
