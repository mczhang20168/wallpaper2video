using System.Globalization;
using System.IO;
using Wallpaper2Video.Core;
using Wallpaper2Video.Desktop;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;
using static Program;

internal static class ApprovedTimingChecks
{
    private const string Workshop = @"E:\SteamLibrary\steamapps\workshop\content\431960\";
    public static async Task RunAsync(string output)
    {
        await Check("realModelAllTwentyTwoLoopsAndFishMirror", () =>
        {
            var model = ProjectLoader.Load(Workshop + "3253330537"); var a = AlignmentPlanner.Analyze(model, new(model));
            Require(a.Durations.Count(d => d.Kind == "skeletal" && d.Aligned) == 22, "主要模型时长仍缺失");
            var fish = ProjectLoader.Load(Workshop + "2468489223"); var plan = ScenePlanner.Build(fish, new ExportSettings { Duration = 27 });
            Require(plan.Alignment.Phases.Count(p => p.Method == "skeletal_rate") == 3, "Mirror骨骼未全部对齐");
            Require(Math.Abs(plan.Alignment.Phases.Single(p => p.Id == "skeletal:1:0").Origin - 4d / 3) < 1e-8, "Mirror缺少返回段");
            var main = plan.Alignment.Phases.Single(p => p.Id == "timeline:objects:4:origin");
            Require(!plan.Alignment.Phases.Any(p => p.Id == "timeline:objects:4:angles") && Array(main.Settings?["combined_paths"]).Count == 1, "组合时间线未共用次数");
            using var copy = PreparedScene.Create(plan, plan.Select(), output);
            var scene = Parse(copy.Project.Scene!.Value.GetRawText()); var obj = Array(scene["objects"]).Single(o => Text(o?["id"]) == Text(plan.Scene!["objects"]![4]!["id"]));
            Require(Number(obj?["origin"]?["animation"]?["options"]?["fps"]) == Number(obj?["angles"]?["animation"]?["options"]?["fps"]), "位置和角度失去同步");
            return Task.CompletedTask;
        });
        await Check("particleManualPrewarmAndModePersistence", async () =>
        {
            var m = new WorkspaceModel(); await m.LoadAsync(Workshop + "3262794021"); m.SetEffectSelection(["particle:1"]);
            m.ParticlePrewarmText = "12"; Require(m.CurrentSceneSelection!.Particles.Single().Prewarm == 12, "自然播放预热未保存");
            m.SetParticle("particle:1", "drain", 0, 4); Require(m.CurrentSceneSelection.Particles.Single().Prewarm == 12, "切换定时丢失预热");
            using var copy = PreparedScene.Create(m.CurrentScenePlan!, m.CurrentSceneSelection, output);
            Require(Number(copy.ParticleManifest?["prewarm_seconds"]) == 12 && False(Array(copy.ParticleManifest?["managed_layers"])[0]?["head_empty_expected"]), "预热清单误报首帧为空");
            m.ToggleEffect("particle:1", false); m.ToggleEffect("particle:1", true); Require(m.CurrentSceneSelection.Particles.Single().Prewarm == 12, "关闭恢复丢失预热");
            m.ParticlePrewarmText = "-1"; Require(!m.CanRender, "无效预热仍可渲染"); m.ParticlePrewarmText = "0"; Require(m.CanRender, "修正预热后无法生成");
            m.StopPlanning(); await m.ReleasePreviewAsync();
        });
        await Check("recommendationsPreserveManualDuration", async () =>
        {
            var m = new WorkspaceModel(); await m.LoadAsync(Workshop + "2468489223"); Require(Math.Abs(double.Parse(m.DurationText, CultureInfo.InvariantCulture) - 40) < .001, "相对时间线未纳入推荐");
            m.DurationText = "23"; while (m.Planning) await Task.Delay(50); Require(m.DurationText == "23", "重新分析覆盖用户总长");
            await m.LoadAsync(Workshop + "3253330537"); Require(m.DurationText == "15", "可信主骨骼未纳入推荐总长");
            m.StopPlanning(); await m.ReleasePreviewAsync();
        });
        await Check("entranceModesNeverSynthesizeMirror", () =>
        {
            var source = ProjectLoader.Load(Workshop + "3629379075");
            foreach (bool keep in new[] { false, true })
            {
                var plan = ScenePlanner.Build(source, new ExportSettings { PreserveEntrance = keep });
                Require(!plan.Alignment.Phases.Any(p => Text(p.Settings?["mode"]) == "single"), "Single被合成循环");
                using var copy = PreparedScene.Create(plan, plan.Select(), output); var scene = Parse(copy.Project.Scene!.Value.GetRawText());
                foreach (var obj in Array(plan.Scene?["objects"]).Where(o => o != null && EntrancePolicy.Identified(o)))
                {
                    var camera = Array(scene["objects"]).Single(o => Text(o?["id"]) == Text(obj?["id"]));
                    if (Truth(obj?["camera"]))
                    {
                        Require(Truth(Value(camera?["visible"])) == keep, "作者入场开关未应用");
                        if (keep) Require(Text(camera?["zoom"]?["animation"]?["options"]?["mode"]) == "single", "保留入场改变了原模式");
                    }
                }
            }
            return Task.CompletedTask;
        });
    }
}
