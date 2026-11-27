using System.Globalization;
using System.Text.Json.Nodes;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;

internal static class SpinChecks
{
    public static void Run()
    {
        const string effect = "effects/local/spin/effect.json";
        const string include = "shaders/rotation.h";
        const string signature = "6d098083a325de0245674952819c93f4ca76683b580923d3192ed5dc0d63c068";
        string parent = Path.GetTempPath();
        string root = SafeFiles.NewWorkDirectory(parent);
        try
        {
            void Write(string name, string value)
            {
                string file = Path.Combine(root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, value);
            }

            Write("project.json", """{"type":"Scene","file":"scene.json"}""");
            Write(effect, """{"passes":[{"material":"materials/local-spin.json"}]}""");
            Write("materials/local-spin.json", """{"passes":[{"shader":"local-spin"}]}""");
            Write("shaders/local-spin.vert", """
                #include "rotation.h"
                uniform float g_Time;
                uniform float g_Speed; // {"material":"speed","default":1}
                void main() {
                    gl_Position = vec4(spinPoint(g_Time * g_Speed), 0.0, 1.0);
                }
                """);
            Write("shaders/local-spin.frag", "void main() { gl_FragColor = vec4(1.0); }");
            Write(include, "vec2 spinPoint(float angle) { return vec2(cos(angle), sin(angle)); }");
            var reviews = new JsonObject { [effect] = new JsonObject { [signature] = "PARAMETER" } };

            foreach (double speed in new[] { -0.16, 0.26 })
            {
                Write("scene.json", $$$"""
                    {"objects":[{"id":1,"effects":[{"file":"{{{effect}}}","passes":[{"constantshadervalues":{"speed":{{{speed.ToString("R", CultureInfo.InvariantCulture)}}}}}]}]}]}
                    """);
                var project = ProjectLoader.Load(root);
                var unreviewed = new AssetBundle(project).Effect(effect);
                if (unreviewed.Signature != signature || unreviewed.Category != null)
                    throw new InvalidOperationException("自制效果的依赖签名应匹配预期，默认规则中没有登记它");

                var bundle = new AssetBundle(project, null, reviews);
                var alignment = AlignmentPlanner.Analyze(project, bundle);
                var phase = alignment.Phases.Single();
                if (Math.Abs(phase.Origin - 2 * Math.PI / Math.Abs(speed)) > 1e-9 || phase.Parameter != "speed")
                    throw new InvalidOperationException("旋转周期或速度参数错误");

                var scene = Parse(project.Scene!.Value.GetRawText());
                AlignmentPatcher.Apply(scene, project.Root, alignment,
                    new Dictionary<string, int> { [phase.Id] = 3 }, 12);
                double retimedSpeed = Number(scene["objects"]?[0]?["effects"]?[0]?["passes"]?[0]?["constantshadervalues"]?["speed"]);
                if (Math.Abs(retimedSpeed - Math.CopySign(Math.PI / 2, speed)) > 1e-9)
                    throw new InvalidOperationException("调整到四秒一圈后应保留原旋转方向");
            }

            File.AppendAllText(Path.Combine(root, include), "\n// revised dependency");
            var changed = new AssetBundle(ProjectLoader.Load(root), null, reviews).Effect(effect);
            if (changed.Signature == signature || changed.Category != null)
                throw new InvalidOperationException("依赖文件变更后应重新核对效果");
        }
        finally
        {
            SafeFiles.DeleteWorkDirectory(root, parent);
        }
        Console.WriteLine("PASS synthetic spin: exact dependency signature, period, direction and unreviewed dependency");
    }
}
