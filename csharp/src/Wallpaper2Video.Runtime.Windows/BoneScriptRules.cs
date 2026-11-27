using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

internal sealed class BoneScriptRules
{
    private const string Player = @"\bthisLayer\s*\.\s*getAnimationLayer\s*\(\s*(?<target>[^()\r\n]+?)\s*\)";
    private const string Self = @"(?:['""]addEndedCallback['""]\s+in\s+thisObject\s*\?\s*thisObject\s*:\s*thisObject\.getAnimation\(\)|thisObject)";
    private const string PhaseArgument = @"(?<phase>scriptProperties\.\w+|[0-9.]+)";

    public Dictionary<int, double> Phases { get; } = [];
    public HashSet<int> Stopped { get; } = [];
    public HashSet<int> Unknown { get; } = [];

    public static bool ControlsBones(string script) =>
        Regex.IsMatch(CodeOnly(script), @"\b(?:getAnimationLayer|createAnimationLayer|destroyAnimationLayer|playSingleAnimation|setLocalBoneTransform|setBoneTransform)\b");

    public static BoneScriptRules Inspect(JsonNode obj, JsonArray layers, JsonArray? sceneObjects)
    {
        var result = new BoneScriptRules();
        void ReadBindings(JsonNode? node, int? index)
        {
            if (node is not JsonObject binding) return;
            string code = Regex.Replace(Text(binding["script"]), @"//[^\n]*|/\*[\s\S]*?\*/", " ");
            if (code.Length == 0) return;
            var init = Regex.Match(code, @"\bexport\s+function\s+init\s*\(\s*value\s*\)\s*\{(?<body>[^{}]*)\}");
            string body = init.Groups["body"].Value;
            var stop = Regex.Match(body, @"^\s*" + Player + @"\s*\.\s*stop\s*\(\s*\)\s*;?\s*return\s+value\s*;?\s*$");
            if (stop.Success)
            {
                var target = Argument(stop.Groups["target"].Value, binding);
                int ai = -1;
                if (target is JsonValue v && v.TryGetValue<string>(out var name))
                {
                    int[] matches = Enumerable.Range(0, layers.Count).Where(i => Text(layers[i]?["name"]) == name).ToArray();
                    if (matches.Length == 1) ai = matches[0];
                }
                else ai = (int)Number(target, -1);
                if (ai >= 0 && ai < layers.Count) { result.Stopped.Add(ai); return; }
            }
            if (index is not int own) return;
            var direct = Regex.Match(body, @"^\s*(?:const|let|var)\s+(?<name>\w+)\s*=\s*" + Self +
                @"\s*;?\s*\k<name>\.play\(\)\s*;?\s*\k<name>\.setFrame\(\s*\k<name>\.frameCount\s*\*\s*" + PhaseArgument + @"\s*\)\s*;?\s*return\s+value\s*;?\s*$");
            var shared = Regex.Match(body, @"^\s*shared\.(?<name>\w+)\(\s*" + Self + @"\s*,\s*" + PhaseArgument + @"\s*\)\s*;?\s*return\s+value\s*;?\s*$");
            if (direct.Success || shared.Success && HasPhaseHelper(sceneObjects, shared.Groups["name"].Value))
            {
                string token = (direct.Success ? direct : shared).Groups["phase"].Value;
                double phase = Number(Argument(token, binding), double.NaN);
                if (double.IsFinite(phase) && phase is >= 0 and <= 1) { result.Phases[own] = phase; return; }
            }
            result.Unknown.Add(own);
        }
        foreach (var field in (obj.AsObject())) if (field.Key != "animationlayers") ReadBindings(field.Value, null);
        for (int i = 0; i < layers.Count; i++)
            if (layers[i] is JsonObject layer)
                foreach (var field in layer) ReadBindings(field.Value, i);
        return result;
    }

    private static JsonNode? Argument(string token, JsonObject binding)
    {
        token = token.Trim();
        if (Regex.IsMatch(token, @"^scriptProperties\.\w+$")) return binding["scriptproperties"]?[token["scriptProperties.".Length..]];
        if (token.Length >= 2 && token[0] is '\'' or '"' && token[^1] == token[0]) return JsonValue.Create(token[1..^1]);
        return double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) ? JsonValue.Create(n) : null;
    }

    private static bool HasPhaseHelper(JsonArray? objects, string name)
    {
        if (objects == null) return false;
        foreach (var node in objects.OfType<JsonObject>())
            foreach (var field in node)
            {
                if (field.Value is not JsonObject binding) continue;
                string code = CodeOnly(Text(binding["script"]));
                var alias = Regex.Match(code, @"\bshared\." + Regex.Escape(name) + @"\s*=\s*(?<fn>\w+)\b");
                if (!alias.Success) continue;
                if (Regex.IsMatch(code, @"\bfunction\s+" + Regex.Escape(alias.Groups["fn"].Value) +
                    @"\s*\(\s*(?<player>\w+)\s*,\s*(?<progress>\w+)(?:\s*=\s*[0-9.]+)?\s*\)\s*\{\s*\k<player>\.play\(\)\s*;?\s*\k<player>\.setFrame\(\s*\k<player>\.frameCount\s*\*\s*\k<progress>\s*\)\s*;?\s*\}")) return true;
            }
        return false;
    }
}
