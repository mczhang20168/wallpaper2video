using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;

internal static class RenderTargetChecks
{
    public static void Run()
    {
        string parent = Path.GetTempPath(), root = SafeFiles.NewWorkDirectory(parent);
        try
        {
            void Write(string name, string data)
            {
                string path = Path.Combine(root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, data);
            }
            Write("project.json", """{"type":"Scene","file":"scene.json"}""");
            Write("scene.json", """
                {"objects":[
                  {"id":1,"name":"foreground","dependencies":[2,4],"effects":[
                    {"file":"effects/motion/effect.json","passes":[{"textures":[null,"_rt_imageLayerComposite_2_a"]}]},
                    {"file":"effects/motion/effect.json","visible":{"value":false},"passes":[{"textures":[null,"_rt_imageLayerComposite_4_a"]}]}]},
                  {"id":2,"name":"hidden input","image":"source","visible":false,"dependencies":[3],"effects":[
                    {"file":"effects/motion/effect.json","passes":[{"textures":[null,"_rt_imageLayerComposite_3_a"]}]},
                    {"file":"effects/motion/effect.json","visible":{"value":false}}]},
                  {"id":3,"name":"nested input","image":"source","visible":false,"effects":[{"file":"effects/motion/effect.json"}]},
                  {"id":4,"name":"unused hidden input","image":"source","visible":false,"effects":[{"file":"effects/motion/effect.json"}]}
                ]}
                """);
            Write("effects/motion/effect.json", """{"passes":[{"material":"materials/motion.json"}]}""");
            Write("materials/motion.json", """{"passes":[{"shader":"motion"}]}""");
            Write("shaders/motion.vert", "void main(){gl_Position=vec4(0.0);}");
            Write("shaders/motion.frag", "uniform float g_Time;\nvoid main(){gl_FragColor=vec4(sin(g_Time));}");

            var plan = ScenePlanner.Build(ProjectLoader.Load(root), new ExportSettings { Duration = 20, ParticleMode = "off" });
            var phases = plan.Alignment.Phases.Select(p => p.Id).ToHashSet();
            Require(phases.Contains("1:0:clock") && phases.Contains("2:0:clock"), "应提取直接和间接引用的隐藏输入时钟");
            Require(!phases.Contains("1:1:clock") && !phases.Contains("3:0:clock"), "默认关闭的效果和未采样的隐藏依赖不应引入时钟");
            Require(plan.Targets().Any(t => t.Id == "effect:1:0") && !plan.SourceLayers()[1].CanToggle, "可编辑渲染输入的效果，但图层仍按作者设置隐藏");

            void CheckPrepared(SceneSelection selection, bool aligned)
            {
                using var prepared = PreparedScene.Create(plan, selection, parent);
                var objects = Array(Parse(prepared.Project.Scene!.Value.GetRawText())["objects"]);
                for (int i = 1; i <= 2; i++)
                {
                    var layer = objects[i]!;
                    Require(False(layer["visible"]), "改写周期不能把渲染输入显示到画面上");
                    string file = Text(Array(layer["effects"])[0]?["file"]);
                    Require(file.StartsWith("effects/w2v_", StringComparison.Ordinal) == aligned, "只有仍被采样的隐藏输入需要改写时钟");
                }
                if (aligned)
                    Require(Text(Array(Array(objects[0]!["effects"])[0]?["passes"])[0]?["textures"]?[1]) == "_rt_imageLayerComposite_2_a",
                        "导出副本需要保留合成输入引用");
            }
            CheckPrepared(plan.Select(), true);
            CheckPrepared(plan.Select(disabledEffects: ["effect:0:0"]), false);
        }
        finally { SafeFiles.DeleteWorkDirectory(root, parent); }
        Console.WriteLine("PASS render-target shader clocks: hidden inputs, transitive references, and export selection");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
