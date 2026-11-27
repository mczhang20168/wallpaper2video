using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wallpaper2Video.Desktop;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;
using static Program;

internal static class VideoV4Checks
{
    public static async Task RunAsync(string output, bool native)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        const string source = @"E:\SteamLibrary\steamapps\workshop\content\431960\3432999904";
        var project = ProjectLoader.Load(source); var bundle = new AssetBundle(project);
        var obj = Array(Parse(project.Scene!.Value.GetRawText())["objects"])[0]!.DeepClone();
        var minimal = project with { Scene = JsonSerializer.SerializeToElement(new JsonObject { ["objects"] = new JsonArray(obj) }) };
        await Check("videoV4MainImageHasPeriod", () =>
        {
            var alignment = AlignmentPlanner.Analyze(minimal, bundle);
            Require(alignment.EmbeddedVideos.Count == 1 && alignment.Phases.Any(p => p.Id == "video:0"), "仅保留主画面仍未识别视频周期");
            Require(Math.Abs(alignment.DefaultVideoDuration!.Value - 754d / 60) < 1e-9, "主视频周期不是754帧/60fps");
            File.WriteAllText(Path.Combine(output, "periods.json"), JsonSerializer.Serialize(alignment.Summary(20, alignment.Choose(20)), Output));
            return Task.CompletedTask;
        });
        var model = new WorkspaceModel(); await model.LoadAsync(source);
        await Check("videoV4PeriodEditorAndRecommendation", () =>
        {
            var timing = model.CurrentScenePlan!.Alignment.EmbeddedVideos.SingleOrDefault();
            Require(timing != null, "完整壁纸没有识别内嵌视频");
            Require(Math.Abs(double.Parse(model.DurationText, System.Globalization.CultureInfo.InvariantCulture) - timing!.Timing.Duration) < 1e-7, "初始总长未采用主视频时长");
            model.SetEffectSelection(["video:0"]); Require(model.Phases.Count == 1, "内嵌视频周期没有编辑入口");
            return Task.CompletedTask;
        });
        if (native) await Check("nativeVideoV4RetimingAndPicturePeriod", async () =>
        {
            var settings = model.CurrentScenePlan!.Settings with { Duration = 6, Overlap = 1, Fps = 20, Width = 640, Height = 296 };
            var alignment = AlignmentPlanner.Analyze(project, bundle); var cycles = alignment.Choose(6); cycles["video:0"] = 2;
            var plan = ScenePlanner.Build(project, settings, cycles: cycles, bundle: bundle, alignment: alignment);
            var hidden = plan.SourceLayers().Where(s => s.Index != 0).ToDictionary(s => s.Index, s => false);
            using var copy = PreparedScene.Create(plan, plan.Select(sourceVisibility: hidden), Path.Combine(Path.GetTempPath(), "w2v"));
            var video = new VideoRenderer(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            string raw = Path.Combine(output, "video-two-cycles-raw.mp4");
            await EngineCapture.CaptureAsync(copy, raw, settings, video.Ffmpeg, EngineCapture.FindEngine(project), cancellation: timeout.Token);
            string texture = Text(Array(copy.ParticleManifest?["video_transcodes"])[0]?["texture"]);
            var privateBundle = new AssetBundle(copy.Project); var timing = EmbeddedVideoTiming.Read(privateBundle, texture);
            Require(timing != null && Math.Abs(timing.Duration - 3) < 1e-6, "任务副本的视频未变速为3秒");
            Require(privateBundle.ReadRange(texture, 46, 9).Data.AsSpan().SequenceEqual("TEXB0004\0"u8), "变速破坏了TEXB4封装版本");
            string pixels = Path.Combine(output, "video-gray.raw");
            await ToolProcess.RunAsync(video.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-i", raw, "-vf", "scale=160:74,format=gray", "-an", "-f", "rawvideo", pixels], timeout.Token);
            byte[] data = File.ReadAllBytes(pixels); const int size = 160 * 74;
            double Mae(int a, int b) => Enumerable.Range(0, size).Average(i => (double)Math.Abs(data[a * size + i] - data[b * size + i]));
            double Lag(int lag) => Enumerable.Range(10, 30).Average(f => Mae(f, f + lag));
            // The source movie contains two similar action loops. Half a clip also
            // repeats; use a quarter clip as the distinct-pose control instead.
            double repeat = Enumerable.Range(59, 3).Min(Lag), changed = Lag(15);
            File.WriteAllText(Path.Combine(output, "native-period.json"), JsonSerializer.Serialize(new { seconds = 3, repeatMae = repeat, quarterPeriodMae = changed, halfPeriodMae = Lag(30), privateTiming = timing }, Output));
            Require(changed > .05 && repeat < changed * .4, "原生画面未按3秒重复，或视频未播放");
            await video.ExportAsync(ProjectLoader.Load(raw), Path.Combine(output, "video-two-cycles.mp4"), settings, cancellation: timeout.Token);
        });
        model.StopPlanning(); await model.ReleasePreviewAsync();
    }
}
