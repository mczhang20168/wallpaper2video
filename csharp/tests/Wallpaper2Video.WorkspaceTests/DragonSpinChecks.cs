using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wallpaper2Video.Desktop;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;
using static Program;

internal static class DragonSpinChecks
{
    public static async Task RunAsync(string output, bool native)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var project = ProjectLoader.Load(@"E:\SteamLibrary\steamapps\workshop\content\431960\3810943704");
        var bundle = new AssetBundle(project); var effect = Array(Parse(project.Scene!.Value.GetRawText())["objects"])[8]!["effects"]![0]!.DeepClone();
        File.WriteAllText(Path.Combine(output, "spin-graph.json"), JsonSerializer.Serialize(bundle.Effect(Text(effect["file"])), Output));
        var scene = new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["id"] = 644, ["name"] = "dragonink1", ["effects"] = new JsonArray(effect) }) };
        var minimal = project with { Scene = JsonSerializer.SerializeToElement(scene) };
        await Check("dragonSpinMinimalPeriod", () =>
        {
            var alignment = AlignmentPlanner.Analyze(minimal, bundle);
            Require(alignment.Phases.Count == 1, "单对象单spin效果没有周期：" + JsonSerializer.Serialize(alignment.Unsupported));
            Require(Math.Abs(alignment.Phases[0].Origin - 2 * Math.PI / .25999999) < 1e-8, "原始旋转周期不正确");
            return Task.CompletedTask;
        });
        var model = new WorkspaceModel(); await model.LoadAsync(project.Root);
        await Check("dragonSpinEditorAndExportCopy", async () =>
        {
            model.SetEffectSelection(["effect:8:0"]);
            Require(model.Phases.Count == 1 && model.Phases.Single().Id == "8:0:主动画", "真实效果面板没有独立周期编辑入口");
            model.Phases.Single().CycleText = "2"; await Wait(model); model.DurationText = "6"; await Wait(model);
            Require(model.CurrentScenePlan!.Cycles["8:0:主动画"] == 2 && !model.Phases.Single().Automatic, "手动次数在修改总长后丢失");
            model.ToggleEffect("effect:8:0", true);
            using var copy = PreparedScene.Create(model.CurrentScenePlan, model.CurrentSceneSelection!, Path.Combine(Path.GetTempPath(), "w2v"));
            var obj = Array(Parse(copy.Project.Scene!.Value.GetRawText())["objects"]).Single(o => Text(o?["id"]) == "644");
            var spin = Array(obj?["effects"]).Single(e => Text(e?["id"]) == "647");
            Require(Math.Abs(Number(spin?["passes"]?[0]?["constantshadervalues"]?["speed"]) - 2 * Math.PI / 3) < 1e-8, "导出副本没有按3秒一圈调速");
        });
        if (native) await Check("dragonSpinNativeThreeSecondPeriod", async () =>
        {
            var settings = model.CurrentScenePlan!.Settings with { Duration = 6, Overlap = 1, Fps = 20, Width = 640, Height = 360 };
            var alignment = AlignmentPlanner.Analyze(project, bundle); var cycles = alignment.Choose(6); cycles["8:0:主动画"] = 2;
            var plan = ScenePlanner.Build(project, settings, cycles: cycles, bundle: bundle, alignment: alignment);
            var hidden = plan.SourceLayers().Where(s => s.Index is not (0 or 8)).ToDictionary(s => s.Index, s => false);
            var disabled = plan.Targets().Where(t => t.Kind == "effect" && t.Id != "effect:8:0").Select(t => t.Id);
            using var copy = PreparedScene.Create(plan, plan.Select(disabledEffects: disabled, sourceVisibility: hidden), Path.Combine(Path.GetTempPath(), "w2v"));
            var video = new VideoRenderer(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            string raw = Path.Combine(output, "dragon-spin-raw.mp4"), pixels = Path.Combine(output, "dragon-spin-gray.raw");
            await EngineCapture.CaptureAsync(copy, raw, settings, video.Ffmpeg, EngineCapture.FindEngine(project), cancellation: timeout.Token);
            await ToolProcess.RunAsync(video.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-i", raw, "-vf", "scale=160:90,format=gray", "-an", "-f", "rawvideo", pixels], timeout.Token);
            byte[] data = File.ReadAllBytes(pixels); const int size = 160 * 90;
            double Lag(int lag) => Enumerable.Range(10, 30).Average(f => Enumerable.Range(0, size).Average(i => (double)Math.Abs(data[f * size + i] - data[(f + lag) * size + i])));
            double repeat = Enumerable.Range(59, 3).Min(Lag), changed = Lag(15);
            File.WriteAllText(Path.Combine(output, "native-period.json"), JsonSerializer.Serialize(new { period = 3, cycles = 2, repeatMae = repeat, quarterTurnMae = changed }, Output));
            Require(changed > .1 && repeat < changed * .4, "实际spin画面未按3秒一圈重复，或效果不可见");
            await video.ExportAsync(ProjectLoader.Load(raw), Path.Combine(output, "dragon-spin.mp4"), settings, cancellation: timeout.Token);
        });
        model.StopPlanning(); await model.ReleasePreviewAsync();
    }
    private static async Task Wait(WorkspaceModel model)
    {
        var until = DateTime.UtcNow.AddSeconds(20); while (model.Planning) { await Task.Delay(30); Require(DateTime.UtcNow < until, "规划超时"); }
        Require(model.CanRender, "场景不能渲染：" + model.Status);
    }
}
