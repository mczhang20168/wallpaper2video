using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wallpaper2Video.Desktop;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;
using static Program;

internal static class SkeletalV6Checks
{
    public static async Task RunAsync(string output, bool native)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var model = new WorkspaceModel(); await model.LoadAsync(@"E:\SteamLibrary\steamapps\workshop\content\431960\3493992395");
        await Check("realMdla6DurationAndUi", () =>
        {
            var timings = model.CurrentScenePlan!.Alignment.Durations.Where(d => d.Kind == "skeletal").ToArray();
            Require(timings.Length == 2 && timings.All(d => d.Aligned), "人物时长仍未正确提取：" + string.Join(";", timings.Select(d => d.Reason)));
            Require(timings[0].RawSeconds == 15 && timings[1].RawSeconds == 2.1, "原始时长不是450/30和63/30");
            Require(Math.Abs(timings[1].EffectiveSeconds!.Value - 2.1 / .81) < 1e-8, "呼吸速度修正后的周期错误");
            model.SetEffectSelection(["model:6"]); Require(model.Phases.Count == 2, "人物属性未显示两条骨骼周期");
            Require(!model.Detail.Contains("Unable to translate") && !model.Detail.Contains("时长未知"), "界面仍显示解码错误"); return Task.CompletedTask;
        });
        await Check("independentBoneItems", () =>
        {
            var rows = model.Effects.Where(row => row.Kind == "bone").ToArray();
            Require(rows.Length == 2, "两条骨骼动画应在效果列表中各自显示");
            model.SetEffectSelection([rows[1].Key]); int other = model.Phases.Single().Cycles;
            model.SetEffectSelection([rows[0].Key]); model.Phases.Single().Cycles += 1;
            model.SetEffectSelection([rows[1].Key]);
            Require(model.Phases.Single().Cycles == other, "修改单条骨骼动画不能改变同模型另一条动画的次数");
            model.SetEffectSelection(["model:6"]);
            return Task.CompletedTask;
        });
        var window = new MainWindow(); var surface = (FrameworkElement)window.Content; window.Content = null; surface.Resources.MergedDictionaries.Add(window.Resources); surface.DataContext = model;
        TextElement.SetFontFamily(surface, window.FontFamily); TextElement.SetFontSize(surface, window.FontSize);
        model.SearchText = "人物"; surface.Measure(new Size(1440, 1000)); surface.Arrange(new Rect(0, 0, 1440, 1000)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1440, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "skeletal-times.png"))) encoder.Save(file); window.Model.StopPlanning();
        if (native) await Check("nativeMdla6ClipMapping", async () =>
        {
            var source = model.CurrentScenePlan!.Project; var settings = model.CurrentScenePlan.Settings with { Duration = 6, Overlap = 1, Fps = 15, Width = 640, Height = 360 };
            var plan = ScenePlanner.Build(source, settings); var hidden = plan.SourceLayers().Where(l => l.Kind == "sound").ToDictionary(l => l.Index, l => false);
            using var copy = PreparedScene.Create(plan, plan.Select(sourceVisibility: hidden), Path.Combine(Path.GetTempPath(), "w2v"));
            var video = new VideoRenderer(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            var capture = await EngineCapture.CaptureAsync(copy, Path.Combine(output, "capture.mp4"), settings, video.Ffmpeg, EngineCapture.FindEngine(source), cancellation: timeout.Token);
            File.WriteAllText(Path.Combine(output, "native-clips.json"), capture.ParticleValidation!.ToJsonString(Output));
            var first = Array(capture.ParticleValidation["native_events"]).First(); var durations = Array(first?["durations"]).Select(v => Number(v)).ToArray();
            Require(durations.Length == 2 && Math.Abs(durations[0] - 15) < 1e-6 && Math.Abs(durations[1] - 2.1) < 1e-5, "原生引擎的动画时长与静态解析不一致");
            await video.ExportAsync(ProjectLoader.Load(Path.Combine(output, "capture.mp4")), Path.Combine(output, "preview.mp4"), settings, cancellation: timeout.Token);
        });
        model.StopPlanning(); await model.ReleasePreviewAsync();
    }
}
