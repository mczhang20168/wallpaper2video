using System.Text.Json.Nodes;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

internal static class CaptureClocks
{
    private static IEnumerable<JsonObject> Timelines(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["animation"] is JsonObject) yield return obj;
            foreach (var (key, child) in obj)
                if (key is not ("animation" or "script")) foreach (var binding in Timelines(child)) yield return binding;
        }
        else if (node is JsonArray array) foreach (var child in array) foreach (var binding in Timelines(child)) yield return binding;
    }
    public static JsonArray Build(ScenePlan? plan, JsonObject scene)
    {
        var clocks = new JsonArray(); if (plan == null) return clocks;
        var objects = Array(scene["objects"]);
        foreach (var phase in plan.Alignment.Phases)
        {
            if (phase.Method is not ("embedded_video_rate" or "sprite_frames" or "skeletal_rate" or "timeline_retime")) continue;
            // Particle-instance curves keep their native loop clock after FPS retiming.
            // IParticleSystemInstance has no public IAnimation lookup; treating it as
            // a layer animation aborts preparation and cannot be used for synchronization.
            if (phase.Method == "timeline_retime" && phase.EffectPath.Length > 3 && phase.EffectPath[2] == "instanceoverride") continue;
            var original = At(plan.SceneDocument, phase.EffectPath[..2]); string id = Text(original?["id"]);
            if (id.Length == 0) continue;
            var obj = objects.FirstOrDefault(o => Text(o?["id"]) == id); if (obj == null || False(Value(obj["visible"]))) continue;
            string kind = phase.Method switch { "embedded_video_rate" => "video", "sprite_frames" => "sprite", "skeletal_rate" => "bone", _ => "timeline" };
            int index = kind == "bone" ? int.Parse(phase.EffectPath[^1]) : 0;
            if (kind == "bone" && False(Value(Array(obj["animationlayers"])[index]?["visible"]))) continue;
            JsonObject? binding = null;
            if (kind == "timeline")
            {
                // Filtering compacts effect arrays, so source indices no longer locate
                // the same property. Resolve the surviving binding by its export animation name.
                string name = AlignmentPatcher.TimelineName(phase, At(plan.SceneDocument, phase.EffectPath)?["animation"]?["options"]);
                var matches = Timelines(obj).Where(b => Text(b["animation"]?["options"]?["name"]) == name).ToArray();
                if (matches.Length > 1) throw new InvalidDataException("同一图层存在重名时间线，无法唯一同步：" + name);
                binding = matches.SingleOrDefault();
            }
            if (kind == "timeline" && (binding is not JsonObject timeline || timeline["animation"] == null)) continue;
            double rate = kind == "timeline" ? 1 : Math.CopySign(phase.Factor * plan.Cycles[phase.Id] / plan.Duration, Number(phase.Raw));
            var clock = new JsonObject
            {
                ["id"] = id,
                ["kind"] = kind,
                ["index"] = index,
                ["name"] = kind == "timeline" ? Text(binding?["animation"]?["options"]?["name"], phase.EffectPath[^1]) : "",
                ["rate"] = kind == "video" ? 1 : rate,
                ["source_rate"] = rate,
                ["seconds"] = plan.Duration / plan.Cycles[phase.Id],
                ["mirror"] = Text(phase.Settings?["mode"]) == "mirror",
                ["frames"] = Number(phase.Settings?["intervals"], Number(phase.Settings?["length"], 0)),
                ["fps"] = Number(phase.Settings?["fps"], 0),
                ["start_phase"] = Number(phase.Settings?["start_phase"], 0)
            };
            if (kind == "sprite") clock["frame_ends"] = phase.Settings!["frame_ends"]!.DeepClone();
            if (kind == "timeline" && phase.EffectPath.Length > 5 && phase.EffectPath[2] == "effects")
            {
                var effects = Array(obj["effects"]); JsonNode? effect = binding;
                while (effect != null && !ReferenceEquals(effect.Parent, effects)) effect = effect.Parent;
                if (effect == null) throw new InvalidDataException("效果时间线已脱离导出对象");
                clock["effect_index"] = effects.IndexOf(effect);
                if (phase.EffectPath[4] == "passes")
                {
                    var passes = Array(effect["passes"]); JsonNode? material = binding;
                    while (material != null && !ReferenceEquals(material.Parent, passes)) material = material.Parent;
                    if (material == null) throw new InvalidDataException("材质时间线已脱离导出效果");
                    clock["material_index"] = passes.IndexOf(material);
                }
            }
            clocks.Add(clock);
        }
        if (plan.Settings.PreserveEntrance)
            foreach (var obj in objects.OfType<JsonObject>().Where(o => Array(plan.SceneDocument?["objects"]).Any(v => v != null && Text(v["id"]) == Text(o["id"]) && EntrancePolicy.Identified(v))))
                foreach (var (name, value) in obj)
                    if (value is JsonObject binding && Text(binding["animation"]?["options"]?["mode"]) == "single" && binding["animation"]?["options"]?["parent"] == null)
                        clocks.Add(new JsonObject { ["id"] = Text(obj["id"]), ["kind"] = "timeline", ["index"] = 0, ["name"] = Text(binding["animation"]?["options"]?["name"], name), ["rate"] = 1, ["single"] = true });
        return clocks;
    }
}
