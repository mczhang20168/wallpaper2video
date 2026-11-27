using System.Text.Json.Nodes;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;

internal static class SceneChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run()
    {
        Require(Text(Parse("{\"group\":1,\"group\":2}")["group"]) == "2", "重复JSON键必须采用Python的最后值语义");
        Require(Restricted("const note='Date mouse'; // new Date()\nreturn value;") == null, "普通字符串和注释不能误判交互脚本");
        Require(Restricted("return `${new Date()}`;") != null, "模板字符串中的可执行Date应排除");
        Require(Restricted("import x from 'unknown';return x;") != null, "未知导入必须排除");
        Require(Restricted("function onKeyDown(event) { return event.keyCode; }") != null && Restricted("const note='onKeyDown keyCode'; return value;") == null, "键盘输入需要排除，普通说明文字不能误判");
        string clock = "uniform float g_Time; // g_Time\n#include \"g_Time.h\"\nfloat t=g_Time; float g_Time2=0;";
        string rewritten = AlignmentPatcher.ReplaceClock(clock, "CLOCK");
        Require(rewritten.Contains("uniform float g_Time;") && rewritten.Contains("// g_Time") && rewritten.Contains("\"g_Time.h\"") && rewritten.Contains("t=CLOCK;") && rewritten.Contains("g_Time2=0"), "改写时钟必须保留声明、注释和引用字符串");
        string neutral = PreparedScene.Neutralize("uniform vec2 g_PointerPosition; uniform float g_MouseX;");
        Require(neutral.Contains("vec2(0.5, 0.5)") && neutral.Contains("g_MouseX = 0"), "鼠标输入必须中性化");
        bool rejected = false;
        try { PreparedScene.Neutralize("uniform sampler2D g_PointerTexture;"); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "未知鼠标uniform类型应阻断准备");
        string parent = Path.GetTempPath(), root = SafeFiles.NewWorkDirectory(parent);
        try
        {
            string source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            void Write(string name, string data)
            {
                string path = Path.Combine(source, name.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, data);
            }
            Write("project.json", """{"type":"Scene","file":"scene.json","title":"迁移回归","unknown":{"keep":true}}""");
            Write("scene.json", """
                {"general":{"cameraparallax":true,"cameraparallaxmouseinfluence":0.3,"camerafade":true},"objects":[
                  {"id":1,"name":"clock","text":{"value":"clock","script":"return new Date();"}},
                  {"id":2,"name":"时钟子层","parent":1,"image":"x"},
                  {"id":3,"name":"背景","image":"x","alpha":{"value":1,"script":"const s='Date'; return value;"},
                   "color":{"value":"1 1 1","script":"return new Date();"},"effects":[{"file":"effects/custom/effect.json"}]},
                  {"id":22,"name":"测试粒子","particle":"particles/test.json"},
                  {"id":23,"name":"未知实例","instance":"unverified.json"}
                ]}
                """);
            Write("effects/custom/effect.json", """{"passes":[{"material":"materials/test.json"}]}""");
            Write("materials/test.json", """{"passes":[{"shader":"test"}]}""");
            Write("shaders/test.vert", "uniform vec2 g_PointerPosition;\nuniform float g_Speed; // {\"material\":\"speed\",\"default\":1,\"group\":1,\"group\":2}\n");
            Write("shaders/test.frag", "void main(){}\n");
            Write("particles/test.json", """
                {"starttime":9,"initializer":[{"name":"lifetimerandom","min":1,"max":2}],"operator":[{"name":"movement"}],
                 "renderer":[{"name":"sprite"}],"emitter":[{"name":"boxrandom","rate":8,"instantaneous":20}]}
                """);
            var project = ProjectLoader.Load(source);
            var plan = ScenePlanner.Build(project, new() { Duration = 20, Overlap = 2, Fps = 30 });
            Require(plan.Required.Any(i => i.Id == "required:layer:0") && plan.Required.Any(i => i.Id == "required:layer:1"), "日期层及子层必须排除");
            Require(plan.Optional.Any(i => i.Id == "effect:2:0" && i.CanEnable), "可读取的未知效果允许明确接受风险");
            Require(plan.Optional.Any(i => i.Id == "model:4" && !i.CanEnable), "未知实例不能绕过依赖筛选");
            Require(plan.Required.Any(i => i.Action == "strip_script") && plan.Optional.Any(i => i.Kind == "script"), "脚本的强制排除与可选项应区分");
            Require(plan.ParticleControls().Single().CanDrain, "有限粒子应支持首尾留空");
            var choice = plan.Select(["effect:2:0"], [new("particle:3", "drain", 2.01, 8.01)]);
            Require(choice.Particles[0].Start == 2 && choice.Particles[0].Stop == 8, "粒子时间必须量化到帧");
            var sceneSnapshot = plan.Scene!;
            Array(sceneSnapshot["objects"]).Clear();
            Require(plan.Targets().Any(target => target.Id == "effect:2:0"), "修改场景快照不能影响已核对的导出计划");
            string before = File.ReadAllText(Path.Combine(source, "scene.json"));
            string foldedPackage = Path.Combine(root, "folded"); Directory.CreateDirectory(foldedPackage);
            File.WriteAllText(Path.Combine(foldedPackage, "project.json"), """{"type":"Scene","file":"scene.json"}""");
            File.WriteAllBytes(Path.Combine(foldedPackage, "scene.pkg"), RuntimeChecks.Package(("scene.json", "{}"u8.ToArray()),
                ("scripts/Straße.js", System.Text.Encoding.UTF8.GetBytes("\uFEFF\uFEFFconst value=1;"))));
            Require(new AssetBundle(ProjectLoader.Load(foldedPackage)).Read("scripts/STRASSE.js") == "\uFEFFconst value=1;", "依赖读取必须保留完整casefold和只移除一个BOM的Python语义");
            foreach (bool ambiguous in new[] { false, true })
            {
                string engine = Path.Combine(root, ambiguous ? "ambiguous-engine" : "unique-engine");
                string path = Path.Combine(engine, "assets/effects/a/shaders/Straße.js"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "unique");
                if (ambiguous) { string other = Path.Combine(engine, "assets/effects/b/shaders/STRASSE.js"); Directory.CreateDirectory(Path.GetDirectoryName(other)!); File.WriteAllText(other, "other"); }
                bool blocked = false;
                try { Require(new AssetBundle(project, engine).Read("shaders/STRASSE.js") == "unique", "内置依赖casefold查找失败"); } catch (InvalidDataException) { blocked = true; }
                Require(blocked == ambiguous, "casefold重名的内置依赖必须按歧义拒绝");
            }
            using (var prepared = PreparedScene.Create(plan, choice, root))
            {
                var scene = Parse(prepared.Project.Scene!.Value.GetRawText());
                var objects = Array(scene["objects"]);
                Require(objects.Count == 2 && objects.All(o => Text(o?["id"]) is "3" or "22"), "实际副本应删除时钟、子层和默认关闭实例");
                Require(False(scene["general"]?["cameraparallax"]) && False(scene["general"]?["camerafade"]), "相机选择未应用到副本");
                Require(objects[0]?["alpha"] is JsonValue && objects[0]?["color"] is JsonValue, "脚本绑定应解包为原值");
                Require(File.ReadAllText(Path.Combine(prepared.Project.Root, "shaders/test.vert")).Contains("const vec2 g_PointerPosition"), "实际副本shader没有中性化");
                var particle = Parse(File.ReadAllText(SafeFiles.Member(prepared.Project.Root, Text(objects[1]?["particle"]))));
                Require(Number(particle["starttime"]) == 0 && Number(Array(particle["emitter"])[0]?["instantaneous"]) == 0, "预模拟与根系统初始瞬发应归零");
                Require(prepared.ParticleManifest != null && Text(objects[1]?["visible"]?["script"]).Contains("W2V_PARTICLE"), "没有生成可信粒子控制器");
                Require(Parse(prepared.Project.Metadata.GetRawText())["unknown"]?["keep"]?.GetValue<bool>() == true, "未知元数据应保留");
            }
            Require(before == File.ReadAllText(Path.Combine(source, "scene.json")), "不能修改源素材");
            var late = plan.Select(particles: [new("particle:3", "drain", 1, 20)]);
            Require(False(plan.ParticlePlan.Configure(late.Particles)[0]["boundary_wait_required"]), "晚停发应允许并报告边界风险");
            var reverse = plan.Select(particles: [new("particle:3", "drain", 20, 1)]);
            Require(Truth(plan.ParticlePlan.Configure(reverse.Particles)[0]["emission_disabled"]), "反向窗口应不发射");
            rejected = false;
            try { using var unsafeCopy = PreparedScene.Create(plan, plan.Select(["script:objects/2/alpha"], [new("particle:3", "drain", 2, 8)]), root); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "首尾留空与保留用户脚本冲突时必须拒绝");
            using (var natural = PreparedScene.Create(plan, plan.Select(["script:objects/2/alpha"], [new("particle:3", "original")]), root))
                Require(File.Exists(natural.Project.Entry), "自然播放不能被自定义粒子时段的脚本限制阻断");
            Require(!Directory.EnumerateDirectories(root, ".w2v-cs-*").Any(), "准备成功或失败后不能遗留临时副本");
            var graph = new AssetBundle(project).Effect("effects/custom/effect.json");
            var privatePlan = new AlignmentPlan();
            privatePlan.Graphs["0:0"] = graph; privatePlan.Graphs["0:1"] = graph;
            foreach (int index in new[] { 0, 1 }) privatePlan.Phases.Add(new($"p{index}", "私有时钟", 1, ["objects", "0", "effects", index.ToString()], Members: ["shaders/test.vert"]));
            graph.Files["shaders/test.vert"] = "uniform float g_Time; float t=g_Time;";
            var privateScene = Parse("""{"objects":[{"effects":[{"file":"effects/custom/effect.json"},{"file":"effects/custom/effect.json"}]}]}""");
            AlignmentPatcher.Apply(privateScene, root, privatePlan, new Dictionary<string, int> { ["p0"] = 1, ["p1"] = 2 }, 20);
            string firstPrivate = Text(Array(privateScene["objects"])[0]?["effects"]?[0]?["file"]);
            string secondPrivate = Text(Array(privateScene["objects"])[0]?["effects"]?[1]?["file"]);
            Require(firstPrivate != secondPrivate && firstPrivate.Contains("w2v_"), "共享shader实例必须拥有独立引用");
            var privateShaders = Directory.EnumerateFiles(Path.Combine(root, "shaders"), "*.vert", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
            Require(privateShaders.Distinct().Count() == 2 && privateShaders.All(s => s.Contains("uniform float g_Time;")), "每实例时钟必须独立改写且保留声明");
            string foliage = AlignmentPatcher.Rewrite("float4 t = g_Speed * g_Time * vec4(0.1, 0.2, -0.3, 0.4);", new("f", "风动", 20, [], Method: "foliage_harmonics", Settings: new() { ["speed"] = 5 }), 4);
            Require(!foliage.Contains("g_Speed *") && foliage.Contains("g_Time * vec4("), "风动应改写为非零整数谐波");
            string iris = AlignmentPatcher.Rewrite("float time=(g_Time * g_Speed); vec2 r=(lowDt + vec2(0, 1)); sin(time);", new("i", "注视", 20, [], Method: "iris_sequence", Settings: new() { ["speed"] = 1, ["steps"] = 20 }), 4);
            Require(iris.Contains("frac(") && !iris.Contains("g_Speed"), "注视序列必须闭合目标并重定时");
            var timeline = Parse("""{"value":0,"animation":{"options":{"mode":"loop","length":60,"fps":30},"c0":[{"frame":0,"value":0,"front":{"enabled":true,"y":0}},{"frame":60,"value":0,"back":{"enabled":true,"y":0}}]}}""");
            Require(AlignmentPlanner.TimelinePeriod(timeline).Origin == 2, "时间线自然周期错误");
            var p = new AlignmentPlan { };
            p.Phases.Add(new("timeline:value", "测试时间线", 2, ["value"], Method: "timeline_retime", Settings: new() { ["mode"] = "loop", ["length"] = 60 }));
            var container = new JsonObject { ["value"] = timeline };
            AlignmentPatcher.Apply(container, root, p, new Dictionary<string, int> { ["timeline:value"] = 3 }, 20);
            Require(Math.Abs(Number(container["value"]?["animation"]?["options"]?["fps"]) - 9) < 1e-10, "时间线应按整数次数重定时");
            var rect = NativeWindows.Fit(new(10, 20, 410, 320), 1920, 1080);
            Require(rect.Right - rect.Left == 400 && rect.Bottom - rect.Top == 225, "实时监看必须保持比例");
            Require(EngineCapture.QuoteWindows("a b\\") == "\"a b\\\\\"", "Windows命令行末尾反斜杠编码错误");
            var telemetryRow = Parse("""{"id":"22","emit_stop_after_arm":5,"drain_bound_seconds":2,"boundary_wait_required":true}""");
            var events = new List<JsonObject>();
            foreach (string stage in new[] { "ready", "armed", "emitting", "paused", "drain_wait_completed", "verify" })
                events.Add(new() { ["stage"] = stage, ["elapsed"] = 10, ["active"] = new JsonArray(JsonValue.Create(stage == "emitting")) });
            Require(Truth(ParticlePlanner.ValidateTelemetry(events, [telemetryRow], 2)["verified_wait_complete"]), "完整原生回执应验证成功");
            rejected = false;
            try { ParticlePlanner.ValidateTelemetry(events.Where(e => Text(e["stage"]) != "paused").ToList(), [telemetryRow], 2); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "缺少停发回执不能通过验证");
            telemetryRow["boundary_wait_required"] = false;
            events[^1]["elapsed"] = 3; events[^1]["active"] = new JsonArray(JsonValue.Create(true));
            Require(False(ParticlePlanner.ValidateTelemetry(events, [telemetryRow], 2)["verified_wait_complete"]), "用户接受晚停发风险不能冒充消亡验证通过");
        }
        finally { SafeFiles.DeleteWorkDirectory(root, parent); }
        Console.WriteLine("PASS scene migration: JSONC / duplicate keys / script policy / dependency graph / private copy / particle lifetime / windows / conflicts / timeline / clock rewriting / cleanup");
    }
}
