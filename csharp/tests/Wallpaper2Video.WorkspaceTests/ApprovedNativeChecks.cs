using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;
using static Program;

internal static class ApprovedNativeChecks
{
    public static async Task RunAsync(string output, string? filter = null)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        const string workshop = @"E:\SteamLibrary\steamapps\workshop\content\431960";
        var video = new VideoRenderer();
        foreach (var sample in new[] { ("snow", "3404976219"), ("video", "3369989878"), ("video-slow", "3369989878"), ("video-manual", "3369989878"), ("fish", "2468489223"), ("model", "3253330537"), ("entry-skip", "3629379075"), ("entry-keep", "3629379075"), ("sakura-cold", "3262794021"), ("sakura-warm", "3262794021"), ("sakura-delayed", "3262794021"), ("sakura-natural", "3262794021") })
            if (filter == null || filter.Split(',').Contains(sample.Item1))
                await Check("native-" + sample.Item1, async () =>
                {
                    var source = ProjectLoader.Load(Path.Combine(workshop, sample.Item2));
                    var settings = new ExportSettings { Duration = sample.Item1 == "video-slow" ? 16 : 10, Overlap = 1, Fps = 15, Width = 640, Height = 360, PreserveEntrance = sample.Item1 == "entry-keep" };
                    var plan = ScenePlanner.Build(source, settings);
                    if (sample.Item1 == "video-manual") { var cycles = plan.Cycles.ToDictionary(p => p.Key, p => p.Value); foreach (var p in plan.Alignment.Phases.Where(p => p.Method == "embedded_video_rate")) cycles[p.Id] = 2; plan = ScenePlanner.Build(source, settings, cycles: cycles); }
                    var particles = plan.ParticleControls().Select(p => new ParticleChoice(p.Id, "off")).ToList();
                    if (sample.Item1 == "snow") particles[particles.FindIndex(p => p.Id == "particle:12")] = new("particle:12", "drain", 0, 5);
                    if (sample.Item1.StartsWith("sakura")) particles[0] = new(particles[0].Id, "drain", sample.Item1 == "sakura-delayed" ? 3 : 0, sample.Item1 == "sakura-delayed" ? 7 : 4, sample.Item1 is "sakura-warm" or "sakura-delayed" ? 12 : 0);
                    if (sample.Item1 == "sakura-natural") particles[0] = new(particles[0].Id, "original", Prewarm: 12);
                    var enabled = plan.Optional.Where(i => i.Kind == "model" && i.CanEnable && sample.Item1 is "fish" or "model" or "entry-skip" or "entry-keep").Select(i => i.Id).ToArray();
                    var hidden = plan.SourceLayers().Where(l => l.Kind == "sound").ToDictionary(l => l.Index, l => false);
                    var choice = plan.Select(enabled, particles, sourceVisibility: hidden);
                    using var prepared = PreparedScene.Create(plan, choice, Path.Combine(Path.GetTempPath(), "w2v"));
                    if (sample.Item1.StartsWith("sakura") || sample.Item1 == "snow")
                    {
                        var scene = Parse(prepared.Project.Scene!.Value.GetRawText());
                        scene["objects"] = new JsonArray(Array(scene["objects"]).Where(o => Truth(o?["particle"])).Select(o => o!.DeepClone()).ToArray());
                        scene["general"]!["clearcolor"] = "0 0 0"; scene["general"]!["clearenabled"] = true; scene["general"]!["bloom"] = false;
                        File.WriteAllText(Path.Combine(prepared.Project.Root, "scene.json"), scene.ToJsonString(Output));
                    }
                    string raw = Path.Combine(output, sample.Item1 + "-raw.mp4");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(95));
                    var captured = await EngineCapture.CaptureAsync(prepared, raw, settings, video.Ffmpeg, EngineCapture.FindEngine(source), cancellation: timeout.Token);
                    File.WriteAllText(Path.Combine(output, sample.Item1 + "-capture.json"), JsonSerializer.Serialize(new { window = (long)captured.SourceWindow, captured.Width, captured.Height, captured.ParticleValidation }, Output));
                    string movie = Path.Combine(output, sample.Item1 + ".mp4");
                    if (sample.Item1.StartsWith("sakura") || sample.Item1 == "snow")
                        await ToolProcess.RunAsync(video.Ffmpeg, ["-v", "error", "-y", "-i", raw, "-vf", "trim=start_frame=15:end_frame=165,setpts=PTS-STARTPTS", "-an", "-c:v", "libx264", movie], timeout.Token);
                    else
                    {
                        var artifact = await video.ExportAsync(ProjectLoader.Load(raw), movie, settings, cancellation: timeout.Token);
                        Require(artifact.Validation.Frames == settings.Frames, "成片帧数错误");
                        if (sample.Item1.StartsWith("video"))
                        {
                            Require(artifact.Validation.MedianAdjacentMae0255 > .05, "视频调速后画面冻结");
                            var job = Array(prepared.ParticleManifest?["video_transcodes"]).Single();
                            var probe = EmbeddedVideoTiming.Read(new AssetBundle(ProjectLoader.Load(prepared.Project.Root)), Text(job?["texture"]))!;
                            Require(Math.Abs(probe.Duration - Number(job?["target_seconds"])) < 1d / 120, "私有视频时长没有适配整次循环");
                        }
                    }
                    var samples = new Dictionary<double, int>();
                    foreach (double t in new[] { 0d, 1d, 4d, 8d })
                    {
                        string png = Path.Combine(output, sample.Item1 + "-" + t + ".png");
                        await ToolProcess.RunAsync(video.Ffmpeg, ["-v", "error", "-y", "-ss", t.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", movie, "-frames:v", "1", png], timeout.Token);
                        using var file = File.OpenRead(png); var decoder = new PngBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0); var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4]; frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
                        int bright = 0; for (int y = 8; y < frame.PixelHeight - 8; y++) for (int x = 8; x < frame.PixelWidth - 8; x++) { int i = (y * frame.PixelWidth + x) * 4; if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 18) bright++; }
                        samples[t] = bright;
                    }
                    File.WriteAllText(Path.Combine(output, sample.Item1 + "-pixels.json"), JsonSerializer.Serialize(samples));
                    if (sample.Item1 == "snow") Require(samples.Values.Max() > 5, "开启雪后仍无可见像素");
                    if (sample.Item1 is "sakura-warm" or "sakura-natural") Require(samples[0] > 10, "12秒预热后成片首帧仍无樱花");
                    if (sample.Item1 == "sakura-cold") Require(samples[0] < 3, "预热0秒首帧应为空");
                    if (sample.Item1 == "sakura-delayed") Require(samples[0] < 3 && samples[1] < 3 && samples[4] > 10, "预热后没有保持开始3秒前隐藏");
                    if (sample.Item1 is "video" or "fish" or "model" or "entry-keep") Require(Array(prepared.ParticleManifest?["clocks"]).Count > 0, "没有创建受信任播放时钟");
                });
    }
}
