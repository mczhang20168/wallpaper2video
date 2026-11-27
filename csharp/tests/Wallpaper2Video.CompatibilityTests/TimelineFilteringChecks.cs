using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;

internal static class TimelineFilteringChecks
{
    public static void Run()
    {
        string parent = Path.GetTempPath(), root = SafeFiles.NewWorkDirectory(parent);
        try
        {
            void Write(string name, string data) { string path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, data); }
            Write("project.json", """{"type":"Scene","file":"scene.json"}""");
            Write("scene.json", """
                {"objects":[{"id":1,"name":"petals","image":"x","effects":[
                  {"file":"effects/xray/effect.json"},
                  {"file":"effects/test/effect.json","passes":[{"constantshadervalues":{"alpha":{"value":0,"animation":{"options":{"fps":30,"length":60,"mode":"loop"},"c0":[{"frame":0,"value":0},{"frame":60,"value":1}]}}}}]}
                ]}]}
                """);
            Write("effects/test/effect.json", """{"passes":[{"material":"materials/test.json"}]}""");
            Write("materials/test.json", """{"passes":[{"shader":"test"}]}""");
            Write("shaders/test.vert", "void main(){}\n"); Write("shaders/test.frag", "void main(){}\n");
            var plan = ScenePlanner.Build(ProjectLoader.Load(root), new ExportSettings { Duration = 6, Overlap = 1 });
            using (var prepared = PreparedScene.Create(plan, plan.Select(["effect:0:1"]), parent))
            {
                var obj = Array(Parse(prepared.Project.Scene!.Value.GetRawText())["objects"])[0];
                if (Array(obj?["effects"]).Count != 1) throw new InvalidOperationException("鼠标效果没有被移除");
                var clock = Array(prepared.ParticleManifest?["clocks"]).Single();
                var binding = obj?["effects"]?[0]?["passes"]?[0]?["constantshadervalues"]?["alpha"];
                if (Text(clock?["name"]) != Text(binding?["animation"]?["options"]?["name"])) throw new InvalidOperationException("前序效果删除后时间线定位到了其它绑定");
                if (Number(clock?["effect_index"], -1) != 0 || Number(clock?["material_index"], -1) != 0) throw new InvalidOperationException("效果参数时间线未使用过滤后的材质所有者");
            }
            using (var prepared = PreparedScene.Create(plan, plan.Select(disabledEffects: ["effect:0:1"]), parent))
                if (Array(prepared.ParticleManifest?["clocks"]).Count != 0) throw new InvalidOperationException("被移除的效果仍生成播放时钟");
        }
        finally { SafeFiles.DeleteWorkDirectory(root, parent); }
        Console.WriteLine("PASS effect timeline identity survives preceding-effect removal; removed timelines skipped");
    }
}
