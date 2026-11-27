using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;
using static Program;

// Opt-in integration regression: the real engine must load timed child emitters through
// PreviewLease's directory nesting, including a longer user-selected cache location.
internal static class ParticleChildChecks
{
    public static async Task RunAsync(string output, string source)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var project = ProjectLoader.Load(source);
        var originals = new[] { project.Entry, project.PackagePath }.Where(p => p != null).ToDictionary(p => p!, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p!))));
        var settings = new ExportSettings { Duration = 20, Overlap = 1, Fps = 12, Width = 640, Height = 360 };
        var plan = ScenePlanner.Build(project, settings);
        var controls = plan.ParticleControls();
        var shooting = controls.Where(p => p.Label.StartsWith("Shooting_Star_01", StringComparison.Ordinal)).ToArray();
        Require(shooting.Length == 6, "用户壁纸的六个语言分支流星清单不符");
        var stars = controls.Where(p => p.Label.StartsWith("Blinking Stars_01", StringComparison.Ordinal)).Select(p => p.Id).ToHashSet();
        var selection = plan.Select(particles: controls.Select(p => new ParticleChoice(p.Id, shooting.Any(s => s.Id == p.Id) ? "drain" : stars.Contains(p.Id) ? "original" : "off", shooting.Any(s => s.Id == p.Id) ? 2 : null, shooting.Any(s => s.Id == p.Id) ? 8 : null)));
        var graph = new List<object>();
        await Check("timedShootingStarChildFilesExist", () =>
        {
            using var prepared = PreparedScene.Create(plan, selection, output);
            var copied = new AssetBundle(prepared.Project);
            foreach (var particle in SceneJson.Array(SceneJson.Parse(prepared.Project.Scene!.Value.GetRawText())["objects"]).Where(p => SceneJson.Text(p?["particle"]).Contains("/w2v_", StringComparison.Ordinal)))
            {
                string name = SceneJson.Text(particle?["particle"]);
                var visited = new HashSet<string>();
                void Visit(string path)
                {
                    if (!visited.Add(path)) return;
                    string full = SafeFiles.Member(prepared.Project.Root, path);
                    Require(File.Exists(full), "导出副本缺少子粒子：" + path);
                    var definition = SceneJson.Parse(copied.Read(path));
                    var children = SceneJson.Array(definition["children"]).Select(c => SceneJson.Text(c?["name"])).ToArray();
                    graph.Add(new { file = path, fullPathLength = full.Length, children });
                    foreach (string child in children) Visit(child);
                }
                Visit(name);
                Require(visited.Count > 1, "流星内部子粒子未进入回归范围");
            }
            Require(graph.Count >= 12, "未覆盖全部流星子系统");
            File.WriteAllText(Path.Combine(output, "particle-child-graph.json"), JsonSerializer.Serialize(graph, new JsonSerializerOptions { WriteIndented = true }));
            return Task.CompletedTask;
        });
        await Check("timedShootingStarsLoadFromNestedPreview", async () =>
        {
            string cache = Path.Combine(RepositoryRoot, "output", "csharp-preview-crash-repro");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            string previous = "";
            var progress = new Progress<RenderProgress>(p => { if (p.Stage != previous) { Console.WriteLine(p.Stage); previous = p.Stage; } });
            await using (var preview = await PreviewLease.RenderSceneAsync(new SceneRenderer(), plan, selection, cache, 640, null, progress, null, cancellation.Token))
            {
                Require(preview.Artifact.Validation.Frames == 240, "流星预览帧数不符");
                File.Copy(preview.Artifact.Path, Path.Combine(output, "timed-shooting-stars.mp4"), true);
                File.Copy(preview.Artifact.ReportPath, Path.Combine(output, "timed-shooting-stars.report.json"), true);
                using var report = JsonDocument.Parse(File.ReadAllText(preview.Artifact.ReportPath));
                Require(report.RootElement.GetProperty("particle_validation").GetProperty("plan").GetProperty("managed_layers").GetArrayLength() == 6, "预览没有实际管理六个定时流星层");
                Require(report.RootElement.GetProperty("source_window_closed").GetBoolean(), "流星测试窗口未关闭");
                Require(report.RootElement.GetProperty("engine_project_max_path_length").GetInt32() < 260, "引擎副本仍有超过原生路径限制的资源");
            }
            Require(!Directory.EnumerateDirectories(cache).Any(), "预览临时目录未清理");
            Require(originals.All(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value), "原壁纸文件发生变化");
        });
    }
}
