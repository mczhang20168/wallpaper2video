using Wallpaper2Video.Desktop;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;
using static Program;

internal static class FishMotionChecks
{
    public static async Task RunAsync(string output)
    {
        var model = new WorkspaceModel();
        await model.LoadAsync(@"E:\SteamLibrary\steamapps\workshop\content\431960\2468489223");
        foreach (int index in new[] { 1, 3, 4, 5 })
            await Check("fishMovementEditor-" + index, () =>
            {
                string key = $"timeline:objects:{index}:origin";
                var row = model.Effects.SingleOrDefault(e => e.Key == key);
                Require(row != null, $"鱼对象{index}的游动时间线已解析，却未出现在效果面板，无法选择周期");
                model.SetEffectSelection([key]);
                Require(model.Phases.Count == 1 && model.Phases[0].Id == key, "游动未提供独立的周期编辑器");
                Require(row!.Category == "周期动态", "鱼的游动未归入周期动态");
                return Task.CompletedTask;
            });
        await Check("fishMotionSwitchDoesNotHideSourceAndStopsCombinedProperties", () =>
        {
            const string key = "timeline:objects:4:origin"; model.ToggleEffect(key, false);
            Require(!model.Effects.Single(e => e.Key == key).Active, "关闭游动没有保存状态");
            Require(!model.CurrentScenePlan!.RemovedLayers(model.CurrentSceneSelection!).Contains(4), "关闭游动错误地移除了鱼素材");
            using (var copy = PreparedScene.Create(model.CurrentScenePlan, model.CurrentSceneSelection!, output))
            {
                var fish = Array(Parse(copy.Project.Scene!.Value.GetRawText())["objects"]).Single(o => Text(o?["id"]) == "417");
                Require(fish?["origin"] is not JsonObject position || position["animation"] == null, "关闭后位置时间线仍在播放");
                Require(fish?["angles"] is not JsonObject angles || angles["animation"] == null, "关闭后组合角度仍在播放");
                Require(Array(copy.ParticleManifest?["clocks"]).All(c => Text(c?["id"]) != "417" || Text(c?["kind"]) != "timeline"), "关闭的游动仍被运行时控制器驱动");
            }
            model.UndoEffectsCommand.Execute(null); Require(model.Effects.Single(e => e.Key == key).Active, "撤销没有恢复游动"); return Task.CompletedTask;
        });
        await Check("fishManualCyclesSurviveDurationAndPreviewCopy", async () =>
        {
            const string key = "timeline:objects:3:origin"; model.SetEffectSelection([key]); model.Phases.Single().CycleText = "3";
            await Wait(model); model.DurationText = "12"; await Wait(model);
            Require(model.CurrentScenePlan!.Cycles[key] == 3 && !model.Phases.Single().Automatic, "手动次数在修改总长后丢失");
            using (var copy = PreparedScene.Create(model.CurrentScenePlan, model.CurrentSceneSelection!, output))
            {
                var clock = Array(copy.ParticleManifest?["clocks"]).Single(c => Text(c?["id"]) == "333" && Text(c?["kind"]) == "timeline");
                Require(Number(clock?["seconds"]) == 4, "手动3次没有传入导出副本");
            }
        });
        var window = new MainWindow(); var surface = (FrameworkElement)window.Content; window.Content = null;
        surface.Resources.MergedDictionaries.Add(window.Resources); surface.DataContext = model;
        TextElement.SetFontFamily(surface, window.FontFamily); TextElement.SetFontSize(surface, window.FontSize);
        model.BrowserGrouping = "按对象"; model.SearchText = "pezmediano2"; model.SetEffectSelection(["timeline:objects:3:origin"]);
        surface.Measure(new Size(1440, 1000)); surface.Arrange(new Rect(0, 0, 1440, 1000)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1440, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "fish-motion-editor.png"))) encoder.Save(file);
        Require(!window.IsVisible, "界面检查意外显示前台窗口"); window.Model.StopPlanning();
        model.StopPlanning(); await model.ReleasePreviewAsync();
    }
    private static async Task Wait(WorkspaceModel model)
    {
        var until = DateTime.UtcNow.AddSeconds(20); while (model.Planning) { await Task.Delay(30); Require(DateTime.UtcNow < until, "规划超时"); }
        Require(model.CanRender, "场景不能渲染：" + model.Status);
    }
    public static async Task RunNativeAsync(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var model = new WorkspaceModel(); await model.LoadAsync(@"E:\SteamLibrary\steamapps\workshop\content\431960\2468489223");
        model.DurationText = "6"; model.OverlapText = "1"; model.FpsText = "15"; model.WidthText = "640"; model.HeightText = "360"; await Wait(model);
        foreach (int index in new[] { 3, 4 }) { model.SetEffectSelection([$"timeline:objects:{index}:origin"]); model.Phases.Single().CycleText = "2"; await Wait(model); }
        var plan = model.CurrentScenePlan!;
        model.ApplySourceVisibility(plan.SourceLayers().Where(s => s.Kind == "sound").Select(s => s.Index), false);
        await Check("nativeFishPositionsFollowTwoCycles", async () =>
        {
            using var copy = PreparedScene.Create(plan, model.CurrentSceneSelection!, Path.Combine(Path.GetTempPath(), "w2v"));
            var scene = Parse(copy.Project.Scene!.Value.GetRawText());
            var host = Array(scene["objects"]).Select(o => o?["visible"]).OfType<JsonObject>().Single(o => Text(o["script"]).Contains("W2V_PARTICLE:"));
            string script = Text(host["script"]);
            script = script.Replace("let elapsed = 0;", "let elapsed = 0;let nextProbe=0;")
                .Replace("stage:stage,elapsed:elapsed,", "stage:stage,elapsed:elapsed,positions:['333','417'].map(id=>{let p=thisScene.getLayerByID(id);return {id:id,origin:[p.origin.x,p.origin.y,p.origin.z],angles:[p.angles.x,p.angles.y,p.angles.z]};}),")
                .Replace("    for(let i=0;i<roots.length;i++) {", "    if(elapsed>=overlap+nextProbe){trace('motion_probe');nextProbe+=0.5;}\n    for(let i=0;i<roots.length;i++) {");
            host["script"] = script; File.WriteAllText(Path.Combine(copy.Project.Root, "scene.json"), scene.ToJsonString(Output));
            var video = new VideoRenderer(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            var result = await EngineCapture.CaptureAsync(copy, Path.Combine(output, "fish-two-cycles-raw.mp4"), plan.Settings, video.Ffmpeg, EngineCapture.FindEngine(plan.Project), cancellation: timeout.Token);
            File.WriteAllText(Path.Combine(output, "runtime-positions.json"), result.ParticleValidation!.ToJsonString(Output));
            var probes = Array(result.ParticleValidation["native_events"]).Where(e => Text(e?["stage"]) == "motion_probe").ToArray();
            JsonNode AtTime(double seconds) => probes.MinBy(e => Math.Abs(Number(e?["elapsed"]) - 1 - seconds))!;
            double X(double t, string id) => Number(Array(AtTime(t)["positions"]).Single(p => Text(p?["id"]) == id)?["origin"]?[0]);
            Require(Math.Abs(X(.5, "333") - X(1.5, "333")) > 20, "鱼的实际位置没有随时间线变化，只有帧号改变");
            foreach (string id in new[] { "333", "417" })
                Require(Math.Abs(X(.5, id) - X(3.5, id)) < (id == "333" ? 8 : 100), "手动两次循环未反映在鱼的实际位置：" + id);
            await video.ExportAsync(ProjectLoader.Load(Path.Combine(output, "fish-two-cycles-raw.mp4")), Path.Combine(output, "fish-two-cycles.mp4"), plan.Settings, cancellation: timeout.Token);
        });
        model.StopPlanning(); await model.ReleasePreviewAsync();
    }
}
