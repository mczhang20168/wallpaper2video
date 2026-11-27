using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

public static class EntrancePolicy
{
    private static string Property(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            string candidate = Text(obj["user"] is JsonObject user ? user["name"] : obj["user"]);
            if (Regex.IsMatch(candidate, "entryanimation|entranceanimation", RegexOptions.IgnoreCase)) return candidate;
            foreach (var (_, child) in obj) { string found = Property(child); if (found.Length > 0) return found; }
        }
        else if (node is JsonArray array) foreach (var child in array) { string found = Property(child); if (found.Length > 0) return found; }
        return "";
    }
    public static bool Identified(JsonNode obj) => Property(obj).Length > 0 && obj is JsonObject item
        && item.Any(p => Text(p.Value is JsonObject b ? b["animation"]?["options"]?["mode"] : null) == "single");
    public static void Apply(ScenePlan plan, JsonObject scene, JsonObject metadata)
    {
        var originals = Array(plan.SceneDocument?["objects"]);
        foreach (var obj in Array(scene["objects"]).OfType<JsonObject>())
        {
            var original = originals.FirstOrDefault(o => Text(o?["id"]) == Text(obj["id"])); if (original == null || !Identified(original)) continue;
            string key = Property(original);
            if (metadata["general"]?["properties"]?[key] is JsonObject property) property["value"] = plan.Settings.PreserveEntrance;
            if (original["visible"] is JsonObject visible && Text(visible["user"]) == key) obj["visible"] = plan.Settings.PreserveEntrance;
            foreach (var (name, value) in (JsonObject)original)
            {
                if (value is not JsonObject binding || Text(binding["animation"]?["options"]?["mode"]) != "single") continue;
                if (plan.Settings.PreserveEntrance)
                {
                    var clean = (JsonObject)binding.DeepClone(); clean.Remove("script"); clean.Remove("scriptproperties");
                    obj[name] = clean;
                    if (clean["animation"]!["options"] is JsonObject options && Text(options["name"]).Length == 0) options["name"] = "w2v_entry_" + Text(obj["id"]) + "_" + name;
                }
                else
                {
                    var animation = binding["animation"]!;
                    var curves = ((JsonObject)animation).Where(p => Regex.IsMatch(p.Key, @"^c\d+$")).OrderBy(p => int.Parse(p.Key[1..], CultureInfo.InvariantCulture)).ToArray();
                    if (curves.Length == 0) continue;
                    double[] values = curves.Select(p => RequiredNumber(Array(p.Value).LastOrDefault()?["value"])).ToArray();
                    if (Truth(animation["relative"]))
                    {
                        double[] reference = AlignmentPlanner.Vector(Value(binding["value"]), values.Length);
                        for (int i = 0; i < values.Length; i++) values[i] += reference[i];
                    }
                    obj[name] = values.Length == 1 ? JsonValue.Create(values[0]) : JsonValue.Create(string.Join(' ', values.Select(v => v.ToString("G17", CultureInfo.InvariantCulture))));
                }
            }
        }
    }
}
