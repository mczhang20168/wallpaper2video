using System.Globalization;
using System.Text.Json;
using Wallpaper2Video.Core;

namespace Wallpaper2Video.Runtime.Windows;

public sealed record VideoProbe(double Duration, int Width, int Height, string Fps);
public sealed record VideoMetrics(long Frames, int Width, int Height, string Fps, double Duration,
    double SeamMae0255, double MedianAdjacentMae0255, double MaxAdjacentMae0255, IReadOnlyList<string> Warnings);
public sealed record VideoArtifact(string Path, string ReportPath, VideoMetrics Validation);

public sealed class VideoRenderer
{
    public string Ffmpeg { get; }
    public string Ffprobe { get; }
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public VideoRenderer(string? ffmpeg = null)
    {
        Ffmpeg = ToolProcess.Find("ffmpeg", ffmpeg);
        string sibling = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Ffmpeg)!, "ffprobe" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Ffprobe = ToolProcess.Find("ffprobe", File.Exists(sibling) ? sibling : null);
    }

    public async Task<VideoProbe> ProbeAsync(string path, CancellationToken cancellation = default)
    {
        string output = await ToolProcess.RunAsync(Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_streams", "-show_format", "-of", "json", path], cancellation);
        using var json = JsonDocument.Parse(output);
        var streams = json.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() == 0) throw new InvalidDataException("输入没有视频轨道");
        var stream = streams[0];
        double duration = 0;
        if (json.RootElement.GetProperty("format").TryGetProperty("duration", out var value))
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        return new(duration, stream.GetProperty("width").GetInt32(), stream.GetProperty("height").GetInt32(),
                   stream.TryGetProperty("avg_frame_rate", out var rate) ? rate.GetString() ?? "" : "");
    }

    public async Task<VideoArtifact> ExportAsync(ProjectSnapshot project, string output, ExportSettings settings,
        IProgress<RenderProgress>? progress = null, CancellationToken cancellation = default)
    {
        settings.Validate();
        cancellation.ThrowIfCancellationRequested();
        if (project.Kind != "video") throw new NotSupportedException("此素材当前仅支持查看信息，请选择视频文件导出");
        output = System.IO.Path.GetFullPath(output);
        if (!System.IO.Path.GetExtension(output).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("输出文件必须为.mp4");
        string reportPath = System.IO.Path.ChangeExtension(output, ".report.json");
        if (System.IO.Path.Exists(output) || System.IO.Path.Exists(reportPath)) throw new IOException("输出视频或报告已存在，请使用新文件名");
        progress?.Report(new("检查输入视频", 0));
        VideoProbe input = await ProbeAsync(project.Entry, cancellation);
        double duration = (double)settings.Frames / settings.Fps;
        double fade = (double)settings.FadeFrames / settings.Fps;
        if (!double.IsFinite(input.Duration) || input.Duration + .5 / settings.Fps < duration + fade)
            throw new InvalidDataException($"输入视频至少需要{duration + fade:0.###}秒，目前只有{input.Duration:0.###}秒");
        string parent = System.IO.Path.GetDirectoryName(output)!;
        string work = SafeFiles.NewWorkDirectory(parent);
        var created = new List<string>();
        try
        {
            string staged = System.IO.Path.Combine(work, "loop.mp4");
            string graph = $"[0:v]fps={settings.Fps},scale={settings.Width}:{settings.Height}:force_original_aspect_ratio=decrease," +
                $"pad={settings.Width}:{settings.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p,split=2[body][head];" +
                $"[body]trim=start_frame={settings.FadeFrames}:end_frame={settings.Frames + settings.FadeFrames},setpts=PTS-STARTPTS[a];" +
                $"[head]trim=end_frame={settings.FadeFrames},setpts=PTS-STARTPTS[b];" +
                $"[a][b]xfade=transition=custom:duration={ToolProcess.Number(fade)}:offset={ToolProcess.Number(duration - fade)}:" +
                "expr='A*(P*P*(3-2*P))+B*(1-P*P*(3-2*P))',format=yuv420p[v]";
            if (settings.PreserveEntrance) graph = $"[0:v]fps={settings.Fps},scale={settings.Width}:{settings.Height}:force_original_aspect_ratio=decrease,pad={settings.Width}:{settings.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p,trim=start_frame={settings.FadeFrames}:end_frame={settings.Frames + settings.FadeFrames},setpts=PTS-STARTPTS[v]";
            progress?.Report(new("处理循环接缝并编码", 0, RenderActivity.Encoding, 0));
            await ToolProcess.RunAsync(Ffmpeg, ["-hide_banner", "-loglevel", "warning", "-nostdin", "-y", "-progress", "pipe:1", "-nostats",
                "-i", project.Entry, "-filter_complex", graph, "-map", "[v]", "-an", "-frames:v", settings.Frames.ToString(CultureInfo.InvariantCulture),
                "-r", settings.Fps.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "medium", "-crf", settings.Crf.ToString(CultureInfo.InvariantCulture),
                "-pix_fmt", "yuv420p", "-movflags", "+faststart", staged], cancellation, Math.Max(180, duration * 30), line =>
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], out long micros))
                        progress?.Report(new("处理循环接缝并编码", Math.Clamp(micros / 1e6 / duration, 0, 1) * .85, RenderActivity.Encoding, Math.Clamp(micros / 1e6 / duration, 0, 1)));
                });
            progress?.Report(new("检查帧数与首尾画面", .9, RenderActivity.Validating, 1));
            VideoMetrics metrics = await AnalyzeAsync(staged, settings.Frames, cancellation);
            string report = JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Implementation = "csharp-video-v1",
                GeneratedAt = DateTimeOffset.UtcNow,
                Project = new { project.Title, project.Kind, Source = project.Entry },
                Output = output,
                Settings = settings,
                Validation = metrics,
                Audio = "silent",
                LoopMethod = settings.PreserveEntrance ? "ordinary finite video; entrance preserved; no loop crossfade" : "integer frames; smoothstep tail-to-head crossfade"
            }, ReportOptions);
            cancellation.ThrowIfCancellationRequested();
            await using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created.Add(output);
                await using var source = File.OpenRead(staged);
                await source.CopyToAsync(file, cancellation);
            }
            await using (var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created.Add(reportPath);
                await using var writer = new StreamWriter(file);
                await writer.WriteAsync(report.AsMemory(), cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(new("导出完成", 1, RenderActivity.Completed, 1));
            return new(output, reportPath, metrics);
        }
        catch
        {
            foreach (string path in created) File.Delete(path);
            throw;
        }
        finally { SafeFiles.DeleteWorkDirectory(work, parent); }
    }

    public async Task<VideoMetrics> AnalyzeAsync(string path, long expectedFrames, CancellationToken cancellation = default)
    {
        VideoProbe probe = await ProbeAsync(path, cancellation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(60, probe.Duration * 5 + 30)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        using var process = ToolProcess.Start(Ffmpeg, ["-v", "error", "-nostdin", "-i", path, "-an", "-vf", "scale=64:36,format=gray", "-f", "rawvideo", "pipe:1"]);
        var errors = ToolProcess.DrainAsync(process.StandardError);
        byte[]? first = null, previous = null;
        var changes = new List<double>();
        double maxMean = 0;
        long count = 0;
        try
        {
            while (true)
            {
                byte[] frame = new byte[64 * 36];
                int offset = 0;
                while (offset < frame.Length)
                {
                    int read = await process.StandardOutput.BaseStream.ReadAsync(frame.AsMemory(offset), linked.Token);
                    if (read == 0) break;
                    offset += read;
                }
                if (offset == 0) break;
                if (offset != frame.Length) throw new InvalidDataException("解码得到不完整视频帧");
                if (previous != null) changes.Add(Difference(previous, frame));
                first ??= frame;
                previous = frame;
                maxMean = Math.Max(maxMean, frame.Average(v => (double)v));
                count++;
            }
            await process.WaitForExitAsync(linked.Token);
            string error = await errors;
            if (process.ExitCode != 0) throw new IOException("视频解码验证失败：" + error);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("视频验证超时"); }
        finally { await ToolProcess.StopAsync(process); await errors; }
        if (count != expectedFrames || first == null || previous == null) throw new InvalidDataException($"视频帧数不符：需要{expectedFrames}，实际{count}");
        if (maxMean < 2) throw new InvalidDataException("视频几乎全黑");
        changes.Sort();
        double median = changes.Count % 2 == 0 ? (changes[changes.Count / 2 - 1] + changes[changes.Count / 2]) / 2 : changes[changes.Count / 2];
        double seam = Difference(first, previous);
        double maximum = changes.Max();
        var warnings = new List<string>();
        if (maximum < .01) warnings.Add("画面几乎静止");
        if (seam > Math.Max(1, median * 5)) warnings.Add("首尾变化明显大于普通帧间变化，建议增加交叠并预览");
        return new(count, probe.Width, probe.Height, probe.Fps, probe.Duration, Math.Round(seam, 5), Math.Round(median, 5), Math.Round(maximum, 5), warnings);
    }

    private static double Difference(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }
}
