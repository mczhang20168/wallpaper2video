using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wallpaper2Video.Core;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

public sealed record TextureSequence(int Frames, double Duration, IReadOnlyList<double> FrameEnds);
public static class TextureTiming
{
    public static TextureSequence? ReadTail(byte[] tail)
    {
        int at = tail.AsSpan().LastIndexOf("TEXS0003\0"u8); if (at < 0) return null;
        if (tail.Length - at < 21) throw new InvalidDataException("TEXS序列数据截断");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(at + 9, 4));
        if (count is 0 or > 65_000 || tail.Length - at != 21 + (long)count * 32) throw new InvalidDataException("未核对的TEXS序列布局");
        double total = 0;
        var frameEnds = new double[count];
        for (int i = 0; i < count; i++)
        {
            float delay = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(at + 25 + i * 32, 4)));
            if (!float.IsFinite(delay) || delay <= 0 || delay > 3600) throw new InvalidDataException("TEXS帧间隔无效");
            total += delay; frameEnds[i] = total;
        }
        return new((int)count, total, frameEnds);
    }

    public static void Analyze(JsonArray objects, AssetBundle bundle, AlignmentPlan plan)
    {
        var sequences = new Dictionary<string, TextureSequence?>();
        var videos = new Dictionary<string, EmbeddedVideoInfo?>();
        // A scene script can pause or select frames on a different layer. Without an
        // unambiguous target, leave those sequences as metadata rather than retiming.
        bool scriptedSelection = Regex.IsMatch(objects.ToJsonString(), @"\b(?:getTextureAnimation|getTextureFrame|setTextureFrame)\b");
        bool Visible(int index)
        {
            var seen = new HashSet<int>();
            while (seen.Add(index))
            {
                var item = objects[index]; if (False(Value(item?["visible"]))) return false;
                string parent = Text(Value(item?["parent"])); if (parent.Length == 0) return true;
                int next = Enumerable.Range(0, objects.Count).FirstOrDefault(i => Text(objects[i]?["id"]) == parent, -1); if (next < 0) return true; index = next;
            }
            return false;
        }
        for (int oi = 0; oi < objects.Count; oi++)
        {
            var obj = objects[oi]; string model = Text(obj?["image"]); if (!model.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var definition = Parse(bundle.Read(model)); string material = Text(definition["material"]);
                if (material.Length == 0) continue;
                var passes = Array(Parse(bundle.Read(material))["passes"]);
                var primary = passes.FirstOrDefault();
                bool spriteSheet = primary?["combos"] is JsonObject combos &&
                    combos.Any(p => p.Key.Equals("spritesheet", StringComparison.OrdinalIgnoreCase) && Truth(Value(p.Value)));
                foreach (string texture in passes.SelectMany(p => Array(p?["textures"])).OfType<JsonValue>().Select(v => Text(v)).Distinct())
                {
                    if (texture.Length == 0 || texture.StartsWith('_') || texture.StartsWith("rt_", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = texture.StartsWith("materials/", StringComparison.Ordinal) ? texture : "materials/" + texture;
                    if (!name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) name += ".tex";
                    if (!videos.TryGetValue(name, out var video)) { video = EmbeddedVideoTiming.Read(bundle, name); videos[name] = video; }
                    if (video != null)
                    {
                        bool visible = Visible(oi); string label = Text(obj?["name"], model) + " / 内嵌视频";
                        plan.EmbeddedVideos.Add(new(label, name, oi, visible, video));
                        bool supported = visible && texture == Text(Array(passes.FirstOrDefault()?["textures"]).FirstOrDefault()) && !plan.Phases.Any(p => p.Id == $"video:{oi}");
                        plan.Durations.Add(new("embedded_video", label, ["objects", oi.ToString(CultureInfo.InvariantCulture)], video.Duration, video.Duration, "video_texture", supported,
                            $"{video.Frames}帧 · {video.Fps:0.###}fps；" + (supported ? "按实际输出总长调速并同步归零；输出静音" : "隐藏图层或非主视频纹理，不自动调速")));
                        if (supported) plan.Phases.Add(new($"video:{oi}", label, video.Duration, ["objects", oi.ToString(CultureInfo.InvariantCulture)], Raw: JsonValue.Create(1), Factor: video.Duration, Method: "embedded_video_rate", Note: "整次播放适配总长；原视频接缝仍需预览", Settings: new() { ["clip_duration"] = video.Duration }));
                        continue;
                    }
                    if (!sequences.TryGetValue(name, out var sequence)) { sequence = ReadTail(bundle.ReadTail(name)); sequences[name] = sequence; }
                    if (sequence == null) continue;
                    bool hidden = !Visible(oi);
                    bool autoSprite = !hidden && spriteSheet && sequence.Frames > 1 && !scriptedSelection &&
                        !Truth(obj?["instanceoverride"]) && !HasScript(obj) && texture == Text(Array(primary?["textures"]).FirstOrDefault());
                    string spriteLabel = Text(obj?["name"], model) + " / 序列贴图";
                    string[] path = ["objects", oi.ToString(CultureInfo.InvariantCulture)];
                    string reason = autoSprite ? "逐帧时长已提取；录制时核对自动播放并按目标相位定位" :
                        $"{sequence.Frames}帧的序列元数据；" + (hidden ? "当前图层隐藏" : "主纹理、自动播放或脚本选帧尚未验证");
                    plan.Durations.Add(new("texture_sequence", spriteLabel, path, sequence.Duration, autoSprite ? sequence.Duration : null, autoSprite ? "sprite_auto" : "sprite_metadata", autoSprite, reason));
                    if (autoSprite)
                        plan.Phases.Add(new($"sprite:{oi}", spriteLabel, sequence.Duration, path, Raw: JsonValue.Create(1), Factor: sequence.Duration, Method: "sprite_frames",
                            Note: reason, Settings: new() { ["frame_ends"] = new JsonArray(sequence.FrameEnds.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()), ["clip_duration"] = sequence.Duration }));
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException or FormatException) { }
        }
    }
}
