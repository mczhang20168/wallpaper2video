using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wallpaper2Video.Desktop;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;
using static Program;

internal static class SpinChecks
{
    public static async Task RunAsync(string output, bool native)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        const string source = @"E:\SteamLibrary\steamapps\workshop\content\431960\2468489223";
        var project = ProjectLoader.Load(source); var bundle = new AssetBundle(project);
        var graph = bundle.Effect("effects/spin/effect.json");
        File.WriteAllText(Path.Combine(output, "spin-graph.json"), JsonSerializer.Serialize(graph, Output));
        await Check("spinMinimalActualShaderHasPeriod", () =>
        {
            var effect = Array(Parse(project.Scene!.Value.GetRawText())["objects"])[7]!["effects"]![1]!.DeepClone();
            var scene = new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["name"] = "spin", ["effects"] = new JsonArray(effect) }) };
            var minimal = project with { Scene = JsonSerializer.SerializeToElement(scene) };
            var alignment = AlignmentPlanner.Analyze(minimal, bundle);
            Require(alignment.Phases.Count == 1, "单对象、单spin效果仍没有可编辑周期：" + JsonSerializer.Serialize(alignment.Unsupported));
            Require(Math.Abs(alignment.Phases[0].Origin - 2 * Math.PI / .16) < 1e-9, "spin原始整圈周期错误");
            return Task.CompletedTask;
        });
        var model = new WorkspaceModel(); await model.LoadAsync(source);
        foreach (int index in new[] { 7, 8 }) await Check("spinEditableAndApplied-" + index, async () =>
        {
            string key = $"effect:{index}:1", id = $"{index}:1:主动画";
            model.SetEffectSelection([key]);
            Require(model.Phases.Count == 1 && model.Phases.Single().Id == id, "圈出的spin没有提供周期编辑器");
            Require(model.Effects.Single(e => e.Key == key).Active, "已核对的spin仍默认关闭");
            model.Phases.Single().CycleText = "3"; await Wait(model);
            Require(model.CurrentScenePlan!.Cycles[id] == 3, "手动次数未保存");
            model.DurationText = "12"; await Wait(model);
            Require(model.CurrentScenePlan!.Cycles[id] == 3 && !model.Phases.Single().Automatic, "更改总长后spin手动次数丢失");
            using (var copy = PreparedScene.Create(model.CurrentScenePlan, model.CurrentSceneSelection!, output))
            {
                string objectId = Text(Array(Parse(project.Scene!.Value.GetRawText())["objects"])[index]?["id"]);
                var obj = Array(Parse(copy.Project.Scene!.Value.GetRawText())["objects"]).Single(o => Text(o?["id"]) == objectId);
                var spin = Array(obj?["effects"]).Single(e => Text(e?["file"]) == "effects/spin/effect.json");
                double speed = Number(spin?["passes"]?[0]?["constantshadervalues"]?["speed"]);
                Require(Math.Abs(speed + 2 * Math.PI / 4) < 1e-9, "导出副本未使用4秒一圈或丢失反向旋转");
            }
        });
        model.BrowserGrouping = "按对象"; model.SearchText = "2fish"; model.SetEffectSelection(["effect:8:1"]);
        var window = new MainWindow(); var surface = (FrameworkElement)window.Content; window.Content = null;
        surface.Resources.MergedDictionaries.Add(window.Resources); surface.DataContext = model;
        TextElement.SetFontFamily(surface, window.FontFamily); TextElement.SetFontSize(surface, window.FontSize);
        surface.Measure(new Size(1440, 1000)); surface.Arrange(new Rect(0, 0, 1440, 1000)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1440, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "spin-editor.png"))) encoder.Save(file);
        Require(!window.IsVisible, "周期编辑检查显示了前台窗口"); window.Model.StopPlanning();
        if (native) await Check("nativeSpinPictureRepeatsAtChosenPeriod", async () =>
        {
            var settings = model.CurrentScenePlan!.Settings with { Duration = 6, Overlap = 1, Fps = 20, Width = 640, Height = 360 };
            var alignment = AlignmentPlanner.Analyze(project, bundle); var cycles = alignment.Choose(6); cycles["7:1:主动画"] = 2;
            var plan = ScenePlanner.Build(project, settings, cycles: cycles, bundle: bundle, alignment: alignment);
            var hidden = plan.SourceLayers().Where(s => s.Index != 7).ToDictionary(s => s.Index, s => false);
            var disabled = plan.Targets().Where(t => t.Kind == "effect" && t.Id != "effect:7:1").Select(t => t.Id);
            using var copy = PreparedScene.Create(plan, plan.Select(disabledEffects: disabled, sourceVisibility: hidden), Path.Combine(Path.GetTempPath(), "w2v"));
            var video = new VideoRenderer(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            string raw = Path.Combine(output, "spin-two-cycles-raw.mp4");
            await EngineCapture.CaptureAsync(copy, raw, settings, video.Ffmpeg, EngineCapture.FindEngine(project), cancellation: timeout.Token);
            string pixels = Path.Combine(output, "spin-gray.raw");
            await ToolProcess.RunAsync(video.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-i", raw, "-vf", "scale=160:90,format=gray", "-an", "-f", "rawvideo", pixels], timeout.Token);
            byte[] data = File.ReadAllBytes(pixels); const int size = 160 * 90;
            double Mae(int a, int b) => Enumerable.Range(0, size).Average(i => (double)Math.Abs(data[a * size + i] - data[b * size + i]));
            // Average several phases: same-phase frames are three seconds apart.
            double repeat = Enumerable.Range(10, 30).Average(f => Mae(f, f + 60));
            double changed = Enumerable.Range(10, 30).Average(f => Mae(f, f + 30));
            File.WriteAllText(Path.Combine(output, "spin-pixel-period.json"), JsonSerializer.Serialize(new { period = 3, cycles = 2, repeatMae = repeat, halfTurnMae = changed }, Output));
            Require(changed > .15, "原生画面没有发生可检测的旋转");
            Require(repeat < changed * .35, "原生画面未按设定的3秒周期重复");
            await video.ExportAsync(ProjectLoader.Load(raw), Path.Combine(output, "spin-two-cycles.mp4"), settings, cancellation: timeout.Token);
        });
        model.StopPlanning(); await model.ReleasePreviewAsync();
    }
    private static async Task Wait(WorkspaceModel model)
    {
        var until = DateTime.UtcNow.AddSeconds(20); while (model.Planning) { await Task.Delay(30); Require(DateTime.UtcNow < until, "规划超时"); }
        Require(model.CanRender, "场景不能渲染：" + model.Status);
    }
}
