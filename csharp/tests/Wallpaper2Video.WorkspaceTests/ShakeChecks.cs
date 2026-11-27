using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;
using static Program;

internal static class ShakeChecks
{
    public static async Task RunAsync(string output, bool native)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var project = ProjectLoader.Load(@"E:\SteamLibrary\steamapps\workshop\content\431960\2854083091");
        var settings = new ExportSettings { Duration = 10, Overlap = 1, Fps = 30, Width = 640, Height = 426 };
        var plan = ScenePlanner.Build(project, settings);
        var hidden = plan.SourceLayers().Where(s => s.Index != 0).ToDictionary(s => s.Index, s => false);
        using var copy = PreparedScene.Create(plan, plan.Select(disabledEffects: ["effect:0:1"], sourceVisibility: hidden), Path.Combine(Path.GetTempPath(), "w2v"));
        var scene = Parse(copy.Project.Scene!.Value.GetRawText());
        var effect = Array(Array(scene["objects"])[0]?["effects"]).Single(e => Text(e?["file"]) == "effects/shake/effect.json");
        var speed = effect?["passes"]?[0]?["constantshadervalues"]?["speed"];
        File.WriteAllText(Path.Combine(output, "prepared-speed.json"), JsonSerializer.Serialize(new { speed, author_property = Parse(copy.Project.Metadata.GetRawText())["general"]?["properties"]?["breathingspeed"] }, Output));
        File.WriteAllText(Path.Combine(output, "shake-graph.json"), JsonSerializer.Serialize(plan.Bundle.Effect("effects/shake/effect.json"), Output));
        await Check("shakeExportedSpeedSurvivesAuthorProperty", () =>
        {
            var effective = speed is JsonObject binding && Truth(binding["user"])
                ? Parse(copy.Project.Metadata.GetRawText())["general"]?["properties"]?[Text(binding["user"])]?["value"] : Value(speed);
            Require(Math.Abs(Number(effective) - 2 * Math.PI / 5) < 1e-9, "界面设为5秒周期，但导出绑定的实际速度仍为 " + Text(effective));
            return Task.CompletedTask;
        });
        if (native) await Check("nativeShakePictureUsesFiveSecondPeriod", async () =>
        {
            var video = new VideoRenderer(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            string raw = Path.Combine(output, "shake-raw.mp4");
            await EngineCapture.CaptureAsync(copy, raw, settings, video.Ffmpeg, EngineCapture.FindEngine(project), cancellation: timeout.Token);
            string pixels = Path.Combine(output, "shake-gray.raw");
            await ToolProcess.RunAsync(video.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-i", raw, "-vf", "scale=160:106,format=gray", "-an", "-f", "rawvideo", pixels], timeout.Token);
            byte[] data = File.ReadAllBytes(pixels); const int size = 160 * 106;
            double Mae(int a, int b) => Enumerable.Range(0, size).Average(i => (double)Math.Abs(data[a * size + i] - data[b * size + i]));
            double Lag(int lag) => Enumerable.Range(15, 75).Average(f => Mae(f, f + lag));
            double expected = Enumerable.Range(149, 3).Min(Lag), original = Enumerable.Range(187, 3).Min(Lag), half = Lag(75);
            File.WriteAllText(Path.Combine(output, "shake-pixel-period.json"), JsonSerializer.Serialize(new { expectedPeriod = 5, originalPeriod = 2 * Math.PI, expectedMae = expected, originalMae = original, halfPeriodMae = half }, Output));
            Require(half > .02, "实际画面没有可检测的运动");
            Require(expected < original * .4, "画面没有按设定的5秒重复；5秒差值=" + expected + "，原6.283秒差值=" + original);
        });
    }
}
