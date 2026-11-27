using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;

internal static class GenericShaderChecks
{
    public static void Run()
    {
        string parent = Path.GetTempPath(), root = SafeFiles.NewWorkDirectory(parent);
        try
        {
            void Write(string name, string value)
            {
                string file = Path.Combine(root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, value);
            }
            Write("project.json", """{"type":"Scene","file":"scene.json"}""");
            Write("scene.json", """{"objects":[{"id":1,"effects":[{"file":"effects/local/motion.json"}]}]}""");
            Write("effects/local/motion.json", """{"passes":[{"material":"materials/motion.json"}]}""");
            Write("materials/motion.json", """{"passes":[{"shader":"local/motion"}]}""");
            Write("shaders/local/motion.vert", "void main(){gl_Position=vec4(0.0);}");
            PeriodPhase? Phase(string body)
            {
                Write("shaders/local/motion.frag", $"uniform float g_Time;\n{body}");
                var project = ProjectLoader.Load(root);
                return AlignmentPlanner.Analyze(project, new AssetBundle(project)).Phases.SingleOrDefault();
            }
            void Require(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); }
            Require(Math.Abs(Phase("float evaluate(float x){float angle=x*2.0; return sin(angle);}\nvoid main(){gl_FragColor=vec4(evaluate(g_Time));}")!.Origin - Math.PI) < 1e-12,
                "应从无登记签名的效果及普通辅助函数中提取周期");
            Require(Phase("void main(){gl_FragColor=vec4(fmod(g_Time,5.0));}")?.Origin == 5, "应识别显式时间回绕");
            Require(Phase("void main(){gl_FragColor=vec4(sin(g_Time)+g_Time);}") == null, "单个正弦分量不能证明整个输出已经循环");
            Require(Phase("void main(){int step=g_Time;gl_FragColor=vec4(sin(step));}") == null, "时间整数量化的周期需要独立证明");
            Require(Phase("void main(){gl_FragColor=vec4(sin(g_Time)+frac(g_Time));}") == null, "不同相位单位的分量不能强行合成共同周期");
        }
        finally { SafeFiles.DeleteWorkDirectory(root, parent); }
        Console.WriteLine("PASS generic shader clock: unregistered source / affine helper / time wrap / unresolved inputs and common periods");
    }
}
