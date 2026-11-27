using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Wallpaper2Video.Core;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

public sealed record SkeletalClip(uint Id, string Name, string Mode, double Fps, uint Intervals, bool Simple)
{
    public double Duration => Intervals / Fps;
}
public sealed record DurationSource(string Kind, string Label, string[] Path, double? RawSeconds, double? EffectiveSeconds, string Mode, bool Aligned, string Reason);

public static class SkeletalTiming
{
    // MDLA track payloads contain N+1 samples for N frame intervals. The trailer
    // changed across versions; accept only the layouts verified against real assets.
    public static IReadOnlyList<SkeletalClip> Read(byte[] data)
    {
        if (data.Length < 17 || !data.AsSpan(0, 4).SequenceEqual("MDLV"u8)) throw new InvalidDataException("MDL文件头无效");
        int start = data.AsSpan().IndexOf("MDLA"u8);
        if (start < 0) throw new InvalidDataException("模型没有MDLA动画段");
        int pos = start, end = data.Length;
        void Need(int n) { if (n < 0 || pos > end - n) throw new InvalidDataException("MDLA动画数据越界或截断"); }
        uint U32() { Need(4); uint n = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4)); pos += 4; return n; }
        string Text0()
        {
            Need(1); int length = data.AsSpan(pos, Math.Min(end - pos, 4097)).IndexOf((byte)0);
            if (length < 0 || length > 4096) throw new InvalidDataException("MDLA字符串无效");
            string s = new UTF8Encoding(false, true).GetString(data, pos, length); pos += length + 1; return s;
        }
        string tag = Text0();
        int tail = tag switch { "MDLA0001" or "MDLA0002" => 4, "MDLA0003" => 9, "MDLA0004" => 10, "MDLA0005" => 34, "MDLA0006" => 35, _ => throw new InvalidDataException("尚未核对的动画格式：" + tag) };
        uint sectionEnd = U32();
        if (sectionEnd > data.Length || sectionEnd <= pos) throw new InvalidDataException("MDLA段范围无效"); end = (int)sectionEnd;
        uint count = U32(); if (count is 0 or > 4096) throw new InvalidDataException("MDLA动画数量无效");
        var clips = new List<SkeletalClip>(); var ids = new HashSet<uint>();
        for (uint i = 0; i < count; i++)
        {
            uint id = U32(), flags = U32(); string name = Text0(), mode = Text0();
            double fps = BitConverter.Int32BitsToSingle((int)U32()); uint intervals = U32(), extra = U32(), tracks = U32();
            if (!double.IsFinite(fps) || fps <= 0 || intervals is 0 or > 10_000_000 || tracks is 0 or > 100_000 || !ids.Add(id)) throw new InvalidDataException("MDLA帧率、帧区间或轨道无效");
            bool simple = flags == 0 && extra == 0;
            long transformBytes = ((long)intervals + 1) * 36;
            for (uint j = 0; j < tracks; j++)
            {
                uint trackFlags = U32(), size = U32();
                if (size > int.MaxValue) throw new InvalidDataException("MDLA骨骼轨道无效");
                simple &= trackFlags <= 1;
                Need((int)size); simple &= size == transformBytes; pos += (int)size;
            }
            // Some exporters omit helper transforms from the declared count. Count only
            // complete sample records before reading the versioned trailer and section end.
            if (simple && tag is "MDLA0004" or "MDLA0005" or "MDLA0006")
                while (tracks < 100_000 && transformBytes <= end - pos - 8 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4)) <= 1 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4)) == transformBytes)
                { pos += 8 + (int)transformBytes; tracks++; }
            void Samples(string label)
            {
                uint sampleFlags = U32(), size = U32();
                if (size != ((long)intervals + 1) * 4) throw new InvalidDataException(label + "采样长度无效");
                Need((int)size); simple &= sampleFlags == 0;
                for (int at = pos; at < pos + (int)size; at += 4)
                    if (!float.IsFinite(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at, 4))))) throw new InvalidDataException(label + "包含非有限采样");
                pos += (int)size;
            }
            if (tag is "MDLA0005" or "MDLA0006")
            {
                simple &= U32() == 0;
                Need(1); byte scalar = data[pos++]; if (scalar > 1) throw new InvalidDataException("未知MDLA标量轨道");
                if (scalar == 1)
                {
                    Samples("MDLA标量");
                    if (tag == "MDLA0006")
                    {
                        uint scalarTracks = 1;
                        int scalarBytes = checked((int)(((long)intervals + 1) * 4));
                        while (scalarTracks < tracks && scalarBytes <= end - pos - 8 &&
                            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4)) == 0 &&
                            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4)) == scalarBytes)
                        { Samples("MDLA6逐骨骼标量"); scalarTracks++; }
                        if (scalarTracks > 1 && scalarTracks != tracks) throw new InvalidDataException("MDLA6逐骨骼标量数量错误");
                    }
                }
                Need(1); byte morph = data[pos++]; if (morph > 1) throw new InvalidDataException("未知MDLA变形轨道");
                if (morph == 1)
                {
                    uint channels = U32(); if (channels > 4096) throw new InvalidDataException("MDLA变形通道过多");
                    for (uint c = 0; c < channels; c++)
                    {
                        float reference = BitConverter.Int32BitsToSingle((int)U32()); uint samples = U32();
                        if (!float.IsFinite(reference) || samples > 4096) throw new InvalidDataException("MDLA变形通道无效");
                        for (uint t = 0; t < samples; t++) { uint size = U32(); if (size != ((long)intervals + 1) * 4) throw new InvalidDataException("MDLA变形采样长度无效"); Need((int)size); pos += (int)size; }
                    }
                }
                Need(24); simple &= data.AsSpan(pos, 24).IndexOfAnyExcept((byte)0) < 0; pos += 24;
                if (tag == "MDLA0006")
                {
                    // V6 may append N+1 scalar samples for EACH bone track.
                    // Skipping a fixed 35-byte tail mistakes these floats for the next clip's name.
                    Need(1); byte perBone = data[pos++]; if (perBone > 1) throw new InvalidDataException("未知MDLA6附加骨骼轨道");
                    if (perBone == 1) for (uint j = 0; j < tracks; j++) Samples("MDLA6附加骨骼");
                }
                uint events = U32(); if (events > 4096) throw new InvalidDataException("MDLA事件数量无效");
                simple &= events == 0;
                for (uint e = 0; e < events; e++) { _ = U32(); _ = Text0(); }
            }
            else { Need(tail); simple &= data.AsSpan(pos, tail).IndexOfAnyExcept((byte)0) < 0; pos += tail; }
            clips.Add(new(id, name, mode, fps, intervals, simple));
        }
        if (pos != end) throw new InvalidDataException("MDLA段长度与轨道布局不一致");
        return clips;
    }

    public static void Analyze(JsonNode obj, int oi, AssetBundle bundle, AlignmentPlan plan, JsonArray? sceneObjects = null)
    {
        var layers = Array(obj["animationlayers"]); if (layers.Count == 0) return;
        var playback = BoneScriptRules.Inspect(obj, layers, sceneObjects);
        foreach (int index in playback.Stopped) plan.SuppressedBones.Add(["objects", oi.ToString(CultureInfo.InvariantCulture), "animationlayers", index.ToString(CultureInfo.InvariantCulture)]);
        string title = Text(obj["name"], "模型"), model = Text(obj["model"], Text(obj["image"]));
        IReadOnlyList<SkeletalClip> clips;
        try
        {
            string puppet = model.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ? model : Text(Parse(bundle.Read(model))["puppet"]);
            clips = Read(bundle.ReadBinary(puppet));
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidDataException or System.Text.Json.JsonException or DecoderFallbackException)
        {
            plan.Durations.Add(new("skeletal", title, ["objects", oi.ToString(CultureInfo.InvariantCulture)], null, null, "unknown", false, e.Message)); return;
        }
        for (int ai = 0; ai < layers.Count; ai++)
        {
            var layer = layers[ai]; if (layer == null) continue;
            string[] path = ["objects", oi.ToString(CultureInfo.InvariantCulture), "animationlayers", ai.ToString(CultureInfo.InvariantCulture)];
            string label = title + " / " + Text(layer["name"], "动画" + (ai + 1));
            var clip = clips.FirstOrDefault(c => c.Id == Number(layer["animation"], -1));
            if (clip == null) { plan.Durations.Add(new("skeletal", label, path, null, null, "unknown", false, "场景动画ID未在模型中找到")); continue; }
            double rate, blend;
            bool dynamic = false;
            try
            {
                dynamic = !TryResolveRate(layer["rate"], bundle, out rate);
                blend = Number(layer["blend"], 1);
            }
            catch (Exception e) when (e is InvalidDataException or FormatException or OverflowException) { rate = blend = double.NaN; }
            bool valid = double.IsFinite(rate) && Math.Abs(rate) > 1e-12;
            if (clip.Mode == "single" && !playback.Stopped.Contains(ai)) plan.SuppressedBones.Add(path);
            bool stopped = playback.Stopped.Contains(ai), unknown = playback.Unknown.Contains(ai);
            double factor = clip.Duration * (clip.Mode == "mirror" ? 2 : 1);
            bool visible = !False(Value(layer["visible"])) && blend > 0;
            bool aligned = clip.Mode is "loop" or "mirror" && clip.Simple && valid && !dynamic && visible && !stopped && !unknown;
            string reason = aligned ? "按独立动画层速度对齐整次循环；名义周期不保证逐像素闭合" : stopped ? "事件启动的骨骼动画保持停止" : clip.Mode == "single" ? "一次性骨骼动画在循环导出中禁用" : !clip.Simple ? "模型含未核对的轨道或附加状态" : unknown || dynamic ? "周期未验证：骨骼播放状态受动态绑定控制" : rate == 0 ? "静止骨骼保持原姿态" : !valid ? "周期未验证：播放速度或权重无效" : !visible ? "动画层已隐藏或权重为零" : "周期未验证：播放模式未知";
            plan.Durations.Add(new("skeletal", label, path, clip.Duration, valid && !dynamic && !stopped && !unknown && clip.Mode is "loop" or "mirror" ? factor / Math.Abs(rate) : null, clip.Mode, aligned, reason));
            if (!aligned) continue;
            plan.Phases.Add(new($"skeletal:{oi}:{ai}", label, factor / Math.Abs(rate), path, Parameter: "rate", Raw: JsonValue.Create(rate), Factor: factor, Method: "skeletal_rate", Note: reason,
                Settings: new() { ["fps"] = clip.Fps, ["intervals"] = clip.Intervals, ["animation_id"] = clip.Id, ["mode"] = clip.Mode, ["clip_duration"] = clip.Duration, ["start_phase"] = playback.Phases.GetValueOrDefault(ai) }));
        }
    }

    private static bool TryResolveRate(JsonNode? rate, AssetBundle bundle, out double value)
    {
        value = double.NaN;
        if (rate is JsonObject binding)
        {
            if (Truth(binding["script"]) || Truth(binding["animation"])) return false;
            var user = binding["user"];
            if (Truth(user))
                return user is JsonValue property && property.TryGetValue<string>(out var name) &&
                    !string.IsNullOrEmpty(name) && bundle.TryGetSavedSliderValue(name, out value);
        }
        value = Number(rate, 1);
        return true;
    }
}
