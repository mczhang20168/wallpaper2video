using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;
using static Program;
using static Wallpaper2Video.Core.SceneJson;

internal static class FixedParticleChecks
{
    public static async Task RunAsync(string output, string source)
    {
        Directory.CreateDirectory(output);
        var project = ProjectLoader.Load(source); var plan = ScenePlanner.Build(project, new ExportSettings());
        var hashes = new[] { project.Entry, project.PackagePath }.Where(p => p != null).ToDictionary(p => p!, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p!))));
        var evidence = new List<object>();
        foreach (bool timed in new[] { false, true }) await Check(timed ? "timedParticlesIgnoreAudio" : "naturalParticlesIgnoreAudio", () =>
        {
            var choices = plan.ParticleControls().Select(p => new ParticleChoice(p.Id, !p.CanOriginal ? "off" : timed && p.CanDrain ? "drain" : "original", timed && p.CanDrain ? 2 : null, timed && p.CanDrain ? 8 : null));
            using var copy = PreparedScene.Create(plan, plan.Select(particles: choices), output);
            var bundle = new AssetBundle(copy.Project); var scene = Parse(copy.Project.Scene!.Value.GetRawText());
            int roots = 0, children = 0;
            foreach (var obj in Array(scene["objects"]).Where(o => Truth(o?["particle"])))
            {
                roots++; string root = Text(obj?["particle"]); var visited = new HashSet<string>();
                void Visit(string path, bool child)
                {
                    Require(visited.Add(path), "子粒子依赖重复或成环"); var definition = Parse(bundle.Read(path));
                    foreach (var emitter in Array(definition["emitter"]))
                    {
                        Require(Number(Value(emitter?["audioprocessingmode"]), 0) == 0, "仍依赖音频：" + Text(obj?["name"]) + " / " + path);
                        double rate = Number(emitter?["rate"], 0);
                        if (!child) Require(rate > 0, "根粒子没有固定发射率：" + path);
                        else { children++; Require(rate == 0 && Number(emitter?["instantaneous"]) == 1, "流星事件子粒子的瞬发行为被改变"); }
                        evidence.Add(new { timed, name = Text(obj?["name"]), path, child, rate, instantaneous = Number(emitter?["instantaneous"], 0) });
                    }
                    foreach (var link in Array(definition["children"])) Visit(Text(link?["name"]), true);
                }
                Visit(root, false);
            }
            Require(roots == 36 && children == 18, "未覆盖36个可用根粒子与18个事件子系统");
            return Task.CompletedTask;
        });
        await Check("fixedParticleCopiesPreserveSource", () => { Require(hashes.All(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value), "修改了原始壁纸"); return Task.CompletedTask; });
        File.WriteAllText(Path.Combine(output, "fixed-particle-emission.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }
}
