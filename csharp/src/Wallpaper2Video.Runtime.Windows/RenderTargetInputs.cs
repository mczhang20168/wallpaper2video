using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

internal readonly partial record struct RenderTargetInput(int Consumer, int Effect, int Producer)
{
    [GeneratedRegex(@"\A_rt_imageLayerComposite_([0-9]+)_[ab]\z")]
    private static partial Regex CompositeTexture();

    public static List<RenderTargetInput> Read(JsonArray objects)
    {
        var layersById = Enumerable.Range(0, objects.Count)
            .GroupBy(i => Text(objects[i]?["id"]), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0 && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var inputs = new HashSet<RenderTargetInput>();
        for (int oi = 0; oi < objects.Count; oi++)
        {
            var effects = Array(objects[oi]?["effects"]);
            for (int ei = 0; ei < effects.Count; ei++)
            {
                var effect = effects[ei]!;
                if (False(Value(effect["visible"])) || AlignmentPlanner.DedicatedMouse.Contains(AlignmentPlanner.Family(Text(effect["file"])))) continue;
                foreach (var pass in Array(effect["passes"]).OfType<JsonObject>())
                    foreach (var texture in Array(pass["textures"]))
                    {
                        var reference = CompositeTexture().Match(Text(Value(texture)));
                        if (reference.Success && layersById.TryGetValue(reference.Groups[1].Value, out int producer))
                            inputs.Add(new(oi, ei, producer));
                    }
            }
        }
        return [.. inputs];
    }

    public static HashSet<int> Reachable(IEnumerable<RenderTargetInput> inputs, IEnumerable<int> visibleLayers,
        IReadOnlySet<int> removedLayers, IReadOnlySet<(int Oi, int Ei)>? removedEffects = null)
    {
        var outgoing = inputs.ToLookup(input => input.Consumer);
        var visited = visibleLayers.ToHashSet();
        var pending = new Queue<int>(visited);
        var producers = new HashSet<int>();
        while (pending.TryDequeue(out int consumer))
        {
            foreach (var input in outgoing[consumer])
            {
                if (removedLayers.Contains(input.Producer) || removedEffects?.Contains((consumer, input.Effect)) == true) continue;
                producers.Add(input.Producer);
                if (visited.Add(input.Producer)) pending.Enqueue(input.Producer);
            }
        }
        return producers;
    }
}
