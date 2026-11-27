using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;

internal static class InstanceTimelineChecks
{
    public static void Run()
    {
        string parent = Path.GetTempPath(), root = SafeFiles.NewWorkDirectory(parent);
        try
        {
            File.WriteAllText(Path.Combine(root, "project.json"), """{"type":"Scene","file":"scene.json"}""");
            string original = """
                {"objects":[{"id":1,"image":"x"},{"id":411,"name":"Torch","particle":"torch.json","instanceoverride":{"alpha":{"value":0.26,"animation":{"options":{"fps":30,"length":360,"mode":"loop"},"c0":[{"frame":0,"value":0.26},{"frame":240,"value":0.01},{"frame":360,"value":0.26}]}}}}]}
                """;
            File.WriteAllText(Path.Combine(root, "scene.json"), original);
            File.WriteAllText(Path.Combine(root, "torch.json"), """{"initializer":[{"name":"lifetimerandom","min":1,"max":2}],"renderer":[{"name":"sprite"}],"emitter":[{"name":"boxrandom","rate":1}]}""");
            var plan = ScenePlanner.Build(ProjectLoader.Load(root), new ExportSettings { Duration = 6, Overlap = 1 });
            using (var copy = PreparedScene.Create(plan, plan.Select(), parent))
            {
                if (Array(copy.ParticleManifest?["clocks"]).Any(c => Text(c?["id"]) == "411")) throw new InvalidOperationException("粒子实例动画被当成图层动画外部同步");
                var torch = Array(Parse(copy.Project.Scene!.Value.GetRawText())["objects"]).Single(o => Text(o?["id"]) == "411");
                var binding = torch?["instanceoverride"]?["alpha"];
                if (Truth(binding?["script"]) || Math.Abs(Number(binding?["animation"]?["options"]?["fps"]) - 60) > 1e-9) throw new InvalidOperationException("粒子实例曲线未按原生时钟重定时为6秒");
                if (OperatingSystem.IsWindows())
                {
                    bool stopped = false;
                    try { EngineCapture.CaptureAsync(copy, Path.Combine(root, "unused.mp4"), plan.Settings, "unused-ffmpeg.exe", "w2v-engine-not-running-" + Guid.NewGuid().ToString("N") + ".exe").GetAwaiter().GetResult(); }
                    catch (IOException error) { stopped = error.Message.Contains("Wallpaper Engine 未运行") && error.Message.Contains("Steam"); }
                    if (!stopped) throw new InvalidOperationException("引擎未运行时仍尝试打开原生任务");
                }
            }
            using (var copy = PreparedScene.Create(plan, plan.Select(particles: [new("particle:1", "off")]), parent))
                if (Array(copy.ParticleManifest?["clocks"]).Count != 0) throw new InvalidOperationException("关闭粒子后仍创建实例时钟");
            if (File.ReadAllText(Path.Combine(root, "scene.json")) != original) throw new InvalidOperationException("实例时钟注册修改了原素材");
        }
        finally { SafeFiles.DeleteWorkDirectory(root, parent); }
        Console.WriteLine("PASS particle instance native curve retiming / disabled layers / source preservation");
    }
}
