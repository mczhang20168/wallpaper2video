using System.IO;
using System.Text.Json;
using Wallpaper2Video.Desktop;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;
using static Program;

internal static class IndexSceneChecks
{
    public static async Task RunAsync(string output, bool native)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var model = new WorkspaceModel(); await model.LoadAsync(@"E:\SteamLibrary\steamapps\workshop\content\431960\3810943704");
        await Check("3810943704DefaultScenePreparation", () =>
        {
            Require(model.CanRender, "素材无法载入：" + model.Status);
            using var copy = PreparedScene.Create(model.CurrentScenePlan!, model.CurrentSceneSelection!, Path.Combine(Path.GetTempPath(), "w2v"));
            File.WriteAllText(Path.Combine(output, "prepared-scene.json"), copy.Project.Scene!.Value.GetRawText());
            File.WriteAllText(Path.Combine(output, "clocks.json"), copy.ParticleManifest?.ToJsonString(Output) ?? "null");
            var petals = Array(Parse(copy.Project.Scene.Value.GetRawText())["objects"]).Single(o => Text(o?["id"]) == "708");
            var opacity = Array(petals?["effects"]).Single(e => Text(e?["id"]) == "695");
            string name = Text(opacity?["passes"]?[0]?["constantshadervalues"]?["alpha"]?["animation"]?["options"]?["name"]);
            Require(Array(copy.ParticleManifest?["clocks"]).Any(c => Text(c?["id"]) == "708" && Text(c?["name"]) == name), "花瓣透明度时间线在过滤后丢失或定位错位");
            return Task.CompletedTask;
        });
        if (native) await Check("3810943704PreviewAndExport", async () =>
        {
            var settings = model.CurrentScenePlan!.Settings with { Duration = 6, Overlap = 1, Fps = 15, Width = 640, Height = 360 };
            var plan = ScenePlanner.Build(model.CurrentScenePlan.Project, settings);
            var hidden = plan.SourceLayers().Where(s => s.Kind == "sound").ToDictionary(s => s.Index, s => false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            var artifact = await new SceneRenderer().ExportAsync(plan, plan.Select(sourceVisibility: hidden), Path.Combine(output, "empress-loop.mp4"), cancellation: timeout.Token);
            Require(artifact.Validation.Frames == 90, "实际合成未完成90帧");
            var report = Parse(File.ReadAllText(artifact.ReportPath));
            Require(Truth(report["source_window_closed"]), "测试渲染窗口未关闭");
            Require(Array(report["particle_validation"]?["plan"]?["clocks"]).Any(c => Text(c?["id"]) == "708" && Text(c?["kind"]) == "timeline"), "实际渲染未同步花瓣透明度时间线");
        });
        model.StopPlanning(); await model.ReleasePreviewAsync();
    }
}
