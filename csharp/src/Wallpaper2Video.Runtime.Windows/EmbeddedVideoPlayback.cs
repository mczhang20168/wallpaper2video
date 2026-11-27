using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using Wallpaper2Video.Core;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

/// <summary>Retimes only private TEX video payloads. Native video seeking is unreliable on WPE 2.x.</summary>
internal static class EmbeddedVideoPlayback
{
    public static JsonArray Plan(ScenePlan plan, JsonObject scene, string root)
    {
        var result = new JsonArray(); var objects = Array(scene["objects"]);
        foreach (var phase in plan.Alignment.Phases.Where(p => p.Method == "embedded_video_rate"))
        {
            var original = At(plan.SceneDocument, phase.EffectPath); string id = Text(original?["id"]);
            var obj = objects.FirstOrDefault(o => Text(o?["id"]) == id); if (obj == null) continue;
            var source = plan.Alignment.EmbeddedVideos.First(v => v.ObjectIndex == int.Parse(phase.EffectPath[1], CultureInfo.InvariantCulture));
            int cycles = plan.Cycles[phase.Id]; if (cycles > plan.Settings.Frames) throw new ArgumentException("内嵌视频循环次数不能超过成片帧数");
            double seconds = plan.Duration / cycles, rate = source.Timing.Duration / seconds;
            long frames = Math.Max(1, FrameMath.Quantize(seconds, Math.Clamp((int)Math.Round(source.Timing.Fps), 1, 120)));
            double fps = frames / seconds; if (fps > 240) throw new ArgumentException("内嵌视频单次周期过短，请减少循环次数");
            string uid = Hash(root + id)[..12], texture = "materials/w2v_v_" + uid + ".tex", material = "materials/w2v_v_" + uid + ".json", model = "models/w2v_v_" + uid + ".json";
            var image = Parse(plan.Bundle.Read(Text(original?["image"]))); var definition = Parse(plan.Bundle.Read(Text(image["material"])));
            var first = Array(Array(definition["passes"]).First()?["textures"]); first[0] = "w2v_v_" + uid;
            image["material"] = material; obj["image"] = model;
            foreach (var (path, data) in new[] { (model, image), (material, definition) })
            { string file = SafeFiles.Member(root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, data.ToJsonString(Output)); }
            string input = SafeFiles.Member(root, source.Texture), destination = SafeFiles.Member(root, texture); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(input)) File.Copy(input, destination); else File.WriteAllBytes(destination, plan.Bundle.ReadBinary(source.Texture));
            result.Add(new JsonObject
            {
                ["id"] = id,
                ["texture"] = texture,
                ["source_seconds"] = source.Timing.Duration,
                ["target_seconds"] = seconds,
                ["rate"] = rate,
                ["frames"] = frames,
                ["fps_numerator"] = checked(frames * plan.Settings.Fps * cycles),
                ["fps_denominator"] = plan.Settings.Frames,
                ["rotation_seconds"] = (seconds - plan.ParticlePlan.Overlap % seconds) % seconds
            });
        }
        return result;
    }
    public static async Task PrepareAsync(PreparedScene prepared, string ffmpeg, IProgress<RenderProgress>? progress, CancellationToken cancellation)
    {
        foreach (var row in Array(prepared.ParticleManifest?["video_transcodes"]))
        {
            cancellation.ThrowIfCancellationRequested(); progress?.Report(new("适配任务副本中的内嵌视频速度…", 0));
            string path = SafeFiles.Member(prepared.Project.Root, Text(row?["texture"]));
            byte[] container = await File.ReadAllBytesAsync(path, cancellation);
            var layout = EmbeddedVideoTiming.ReadLayout(container, container.Length) ?? throw new InvalidDataException("内嵌视频纹理布局已变化");
            int size = checked((int)layout.Length), offset = layout.Offset;
            string input = path + ".input.mp4", output = path + ".retimed.mp4";
            try
            {
                await File.WriteAllBytesAsync(input, container.AsMemory(offset, size).ToArray(), cancellation);
                double seconds = Number(row?["target_seconds"]), rotation = Number(row?["rotation_seconds"]);
                string rate = ToolProcess.Number(Number(row?["rate"]));
                long numerator = (long)Number(row?["fps_numerator"]), denominator = (long)Number(row?["fps_denominator"]);
                long Gcd(long a, long b) { while (b != 0) { long t = a % b; a = b; b = t; } return a; }
                long gcd = Gcd(numerator, denominator); string fps = $"{numerator / gcd}/{denominator / gcd}";
                string graph = $"setpts=(PTS-STARTPTS)/{rate},trim=start={ToolProcess.Number(rotation)}:end={ToolProcess.Number(rotation + seconds)},setpts=PTS-STARTPTS,fps={fps}";
                await ToolProcess.RunAsync(ffmpeg, ["-v","error","-nostdin","-y","-stream_loop","-1","-i",input,"-vf",graph,"-an","-frames:v",Text(row?["frames"]),"-r",fps,
                    "-c:v","libx264","-preset","fast","-crf","16","-pix_fmt","yuv420p","-movflags","+faststart",output], cancellation, Math.Max(120, seconds * 12));
                byte[] movie = await File.ReadAllBytesAsync(output, cancellation); byte[] updated = new byte[offset + movie.Length + container.Length - offset - size];
                container.AsSpan(0, offset).CopyTo(updated); BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(layout.SizeOffset, 4), (uint)movie.Length);
                movie.CopyTo(updated, offset); container.AsSpan(offset + size).CopyTo(updated.AsSpan(offset + movie.Length));
                await File.WriteAllBytesAsync(path, updated, cancellation);
                row!["encoded_duration_seconds"] = seconds; row["audio"] = "removed from private video";
            }
            finally { if (File.Exists(input)) File.Delete(input); if (File.Exists(output)) File.Delete(output); }
        }
    }
}
