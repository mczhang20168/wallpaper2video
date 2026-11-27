using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;
using static Program;
using static Wallpaper2Video.Core.SceneJson;

// Isolates the user's real shooting-star graph on black with no sound layer, then
// checks rendered pixels, not just the controller's playback-state telemetry.
internal static class SilentParticleChecks
{
    public static async Task RunAsync(string output, string source)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var project = ProjectLoader.Load(source); var settings = new ExportSettings { Duration = 20, Overlap = 1, Fps = 12, Width = 640, Height = 360 };
        var plan = ScenePlanner.Build(project, settings); var controls = plan.ParticleControls();
        var shooting = controls.First(p => p.Label.StartsWith("Shooting_Star_01", StringComparison.Ordinal));
        string engine = EngineCapture.FindEngine(project); var video = new VideoRenderer(); var evidence = new List<object>();
        foreach (bool timed in new[] { false, true }) await Check(timed ? "silentTimedShootingStarPixels" : "silentNaturalShootingStarPixels", async () =>
        {
            var choices = controls.Select(p => new ParticleChoice(p.Id, !p.CanOriginal ? "off" : p.Id == shooting.Id && timed ? "drain" : "original", p.Id == shooting.Id && timed ? 2 : null, p.Id == shooting.Id && timed ? 8 : null));
            using var prepared = PreparedScene.Create(plan, plan.Select(particles: choices), Path.Combine(Path.GetTempPath(), "w2v"));
            var scene = Parse(prepared.Project.Scene!.Value.GetRawText());
            string id = Text(plan.Scene!["objects"]![shooting.Index]?["id"]);
            var particle = (System.Text.Json.Nodes.JsonObject)Array(scene["objects"]).Single(o => Text(o?["id"]) == id)!.DeepClone();
            particle.Remove("parent"); particle["origin"] = "1920 1080 0"; particle["instanceoverride"] = Parse("""{"count":1,"rate":1,"lifetime":1,"size":3,"alpha":1}""");
            scene["objects"] = new System.Text.Json.Nodes.JsonArray(particle);
            scene["general"]!["clearcolor"] = "0 0 0"; scene["general"]!["clearenabled"] = true; scene["general"]!["bloom"] = false; scene["general"]!["hdr"] = false;
            File.WriteAllText(Path.Combine(prepared.Project.Root, "scene.json"), scene.ToJsonString(Output));
            string name = timed ? "timed" : "natural", raw = Path.Combine(output, name + "-no-music.mp4");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            await EngineCapture.CaptureAsync(prepared, raw, settings, video.Ffmpeg, engine, cancellation: cancellation.Token, rawDuration: timed ? null : 8);
            var samples = new Dictionary<double, int>();
            foreach (double second in timed ? new[] { 1d, 5d, 7d, 8d, 9d, 19d } : new[] { 2d, 5d })
            {
                string png = Path.Combine(output, $"{name}-{second:0}.png");
                await ToolProcess.RunAsync(video.Ffmpeg, ["-v", "error", "-y", "-ss", ToolProcess.Number(second), "-i", raw, "-frames:v", "1", png], cancellation.Token);
                using var file = File.OpenRead(png); var decoder = new PngBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0); var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4]; frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
                int bright = 0; for (int y = 8; y < frame.PixelHeight - 8; y++) for (int x = 8; x < frame.PixelWidth - 8; x++)
                { int i = (y * frame.PixelWidth + x) * 4; if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 12) bright++; }
                samples[second] = bright;
            }
            evidence.Add(new { name, soundLayers = 0, samples }); File.WriteAllText(Path.Combine(output, "silent-particle-pixels.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            Require(samples.Where(p => p.Key >= 2 && p.Key <= 9).Max(p => p.Value) > 3, "无音乐场景没有可见流星粒子");
            if (timed) { Require(samples[1] < 3, "设定开始前已经发射"); Require(samples[19] < 3, "停发并等待消亡后仍有粒子残留"); }
        });
    }
}
