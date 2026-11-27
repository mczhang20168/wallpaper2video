using System.IO;
using System.Text.Json.Nodes;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;
using static Program;

internal static class TorchReadyChecks
{
    public static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var project = ProjectLoader.Load(@"E:\SteamLibrary\steamapps\workshop\content\431960\3810943704");
        var settings = new ExportSettings { Duration = 6, Overlap = 1, Fps = 15, Width = 640, Height = 360 };
        var plan = ScenePlanner.Build(project, settings);
        var particles = plan.ParticleControls().Select(p => new ParticleChoice(p.Id, p.Id == "particle:22" ? "original" : "off"));
        var hidden = plan.SourceLayers().Where(s => s.Kind == "sound").ToDictionary(s => s.Index, s => false);
        using var copy = PreparedScene.Create(plan, plan.Select(particles: particles, sourceVisibility: hidden), Path.Combine(Path.GetTempPath(), "w2v"));
        File.WriteAllText(Path.Combine(output, "clocks.json"), copy.ParticleManifest!.ToJsonString(Output));
        var scene = Parse(copy.Project.Scene!.Value.GetRawText());
        var host = Array(scene["objects"]).Select(o => o?["visible"]).OfType<JsonObject>().Single(b => Text(b["script"]).Contains("W2V_PARTICLE:"));
        host["script"] = Text(host["script"]).Replace("let elapsed = 0;", "let elapsed = 0;let nextProbe=0;")
            .Replace("stage:stage,elapsed:elapsed,", "stage:stage,elapsed:elapsed,instance_alpha:thisScene.getLayerByID('411').instance.alpha,")
            .Replace("elapsed+=engine.frametime;", "elapsed+=engine.frametime;if(elapsed>=nextProbe){trace('instance_probe');nextProbe+=0.5;}");
        File.WriteAllText(copy.Project.Entry, scene.ToJsonString(Output));
        await Check("torchInstanceTimelineReachesReadyAndRecords", async () =>
        {
            try
            {
                var video = new VideoRenderer(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var result = await EngineCapture.CaptureAsync(copy, Path.Combine(output, "torch-raw.mp4"), settings, video.Ffmpeg, EngineCapture.FindEngine(project), cancellation: timeout.Token);
                File.WriteAllText(Path.Combine(output, "native-validation.json"), result.ParticleValidation!.ToJsonString(Output));
                var samples = Array(result.ParticleValidation["native_events"]).Where(e => Text(e?["stage"]) == "instance_probe").Select(e => Number(e?["instance_alpha"])).ToArray();
                Require(samples.Length > 5 && samples.Max() - samples.Min() > .05, "Torch透明度时间线没有实际驱动实例参数");
            }
            finally
            {
                string log = Path.Combine(Path.GetDirectoryName(EngineCapture.FindEngine(project))!, "log.txt"), prefix = Text(copy.ParticleManifest["telemetry_prefix"]);
                File.WriteAllLines(Path.Combine(output, "native-probes.log"), File.ReadLines(log).Where(l => l.Contains(prefix)));
            }
        });
    }
}
