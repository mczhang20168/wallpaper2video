using System.Text;
using System.Text.Json.Nodes;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;

internal static class SkeletalChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static byte[] Model(int version, bool extended = false, bool helperTransform = false, bool single = false, bool scalarBlock = false)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        void Text0(string s) { writer.Write(Encoding.UTF8.GetBytes(s)); writer.Write((byte)0); }
        Text0("MDLV0021"); Text0($"MDLA{version:0000}"); long pointer = stream.Position; writer.Write(0); writer.Write(3);
        foreach (var (id, frames) in new[] { (463, 150), (1054, 270), (377, 360) })
        {
            writer.Write(id); writer.Write(0); Text0("动画" + id); Text0(single && id == 377 ? "single" : "loop"); writer.Write(60f); writer.Write(frames); writer.Write(0); writer.Write(scalarBlock ? 2 : 1);
            writer.Write(0); writer.Write((frames + 1) * 36); writer.Write(new byte[(frames + 1) * 36]);
            if (helperTransform || scalarBlock) { writer.Write(0); writer.Write((frames + 1) * 36); writer.Write(new byte[(frames + 1) * 36]); }
            if (scalarBlock)
            {
                writer.Write(0); writer.Write((byte)1);
                for (int track = 0; track < 2; track++)
                { writer.Write(0); writer.Write((frames + 1) * 4); writer.Write(new byte[(frames + 1) * 4]); }
                writer.Write(new byte[30]);
            }
            else if (version == 6 && extended)
            {
                writer.Write(0); writer.Write((byte)0); writer.Write((byte)1);
                writer.Write(1); writer.Write(.5f); writer.Write(1); writer.Write((frames + 1) * 4); writer.Write(new byte[(frames + 1) * 4]);
                writer.Write(new byte[24]); writer.Write((byte)1);
                writer.Write(0); writer.Write((frames + 1) * 4);
                for (int f = 0; f <= frames; f++) writer.Write((float)Math.Sin(2 * Math.PI * f / frames));
                writer.Write(0);
            }
            else writer.Write(new byte[version switch { 1 or 2 => 4, 3 => 9, 4 => 10, 5 => 34, 6 => 35, _ => 0 }]);
        }
        int end = (int)stream.Position; stream.Position = pointer; writer.Write(end); return stream.ToArray();
    }
    public static void Run()
    {
        using (var bytes = new MemoryStream())
        {
            using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
            writer.Write("TEXS0003\0"u8); writer.Write(3); writer.Write(32); writer.Write(32);
            foreach (float delay in new[] { .1f, .2f, .3f }) { writer.Write(0); writer.Write(delay); writer.Write(new byte[24]); }
            byte[] sequence = bytes.ToArray(); var timing = TextureTiming.ReadTail(sequence)!;
            Require(timing.Frames == 3 && Math.Abs(timing.Duration - .6) < 1e-7, "序列贴图应累加逐帧时长");
            Require(Math.Abs(timing.FrameEnds[1] - .3) < 1e-7, "逐帧定位必须保留不均匀延迟");
            bool truncated = false; try { TextureTiming.ReadTail(sequence[..^1]); } catch (InvalidDataException) { truncated = true; }
            Require(truncated, "截断序列贴图不能猜测时长");
            using var package = new PackageReader(new MemoryStream(RuntimeChecks.Package(("a.tex", new byte[100].Concat(sequence).ToArray()))));
            Require(package.ReadTail("A.TEX", sequence.Length).SequenceEqual(sequence), "PKG尾部读取应保持casefold且不越过素材边界");
        }
        foreach (int version in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var clips = SkeletalTiming.Read(Model(version));
            Require(clips.Select(c => c.Duration).SequenceEqual(new[] { 2.5, 4.5, 6 }), "骨骼帧区间应除以FPS，不能把N+1采样数当作时长");
            Require(clips.All(c => c.Simple), "已核对动画轨道不能被误判");
            foreach (int trim in new[] { 1, 4, 100 })
            {
                bool rejected = false; try { SkeletalTiming.Read(Model(version)[..^trim]); } catch (InvalidDataException) { rejected = true; }
                Require(rejected, "截断模型必须拒绝");
            }
        }
        var unknown = Model(7); bool blocked = false;
        try { SkeletalTiming.Read(unknown); } catch (InvalidDataException) { blocked = true; }
        Require(blocked, "未核对模型版本必须保留未知状态");
        var extended = SkeletalTiming.Read(Model(6, true));
        Require(extended.Count == 3 && extended.All(c => c.Simple) && extended.Select(c => c.Duration).SequenceEqual(new[] { 2.5, 4.5, 6 }), "MDLA6附加采样不能使下一条动画的名称/时长错位");
        blocked = false; try { SkeletalTiming.Read(Model(6, true)[..^1]); } catch (InvalidDataException) { blocked = true; }
        Require(blocked, "带附加轨道的MDLA6截断也必须拒绝");
        foreach (int version in new[] { 4, 6 })
            Require(SkeletalTiming.Read(Model(version, helperTransform: true)).Select(c => c.Duration).SequenceEqual(new[] { 2.5, 4.5, 6 }),
                "额外变换采样不能使后续片段偏移");
        Require(SkeletalTiming.Read(Model(6, scalarBlock: true)).All(c => c.Simple), "逐骨骼标量块应在读取附加通道之前完整消费");
        string parent = Path.GetTempPath(), root = SafeFiles.NewWorkDirectory(parent);
        try
        {
            File.WriteAllText(Path.Combine(root, "project.json"), """{"type":"Scene","file":"scene.json"}""");
            File.WriteAllText(Path.Combine(root, "model.json"), """{"puppet":"model.mdl"}""");
            File.WriteAllBytes(Path.Combine(root, "model.mdl"), Model(6));
            File.WriteAllText(Path.Combine(root, "scene.json"), """{"objects":[{"name":"Miku","image":"model.json","animationlayers":[{"animation":463,"name":"呼吸","rate":{"value":-0.78}},{"animation":1054,"name":"眨眼","rate":0.81},{"animation":377,"name":"摇手","rate":0.74}]}]}""");
            var project = ProjectLoader.Load(root); var bundle = new AssetBundle(project); var alignment = AlignmentPlanner.Analyze(project, bundle);
            Require(alignment.Phases.Count == 3 && alignment.Durations.Count == 3, "应分别提取三个动画层");
            Require(bundle.BinaryFiles.ContainsKey("model.mdl") && !bundle.Files.ContainsKey("model.mdl"), "模型二进制不应进入文本缓存");
            Require(Math.Abs(alignment.Phases[0].Origin - 2.5 / .78) < 1e-9, "原始长度与变速周期不可混淆");
            var counts = new Dictionary<string, int> { ["skeletal:0:0"] = 36, ["skeletal:0:1"] = 20, ["skeletal:0:2"] = 15 };
            var scene = Parse(project.Scene!.Value.GetRawText()); AlignmentPatcher.Apply(scene, root, alignment, counts, 90);
            var layers = Array(Array(scene["objects"])[0]?["animationlayers"]);
            Require(Number(layers[0]?["rate"]) == -1 && Number(layers[1]?["rate"]) == 1 && Number(layers[2]?["rate"]) == 1, "90秒对应36/20/15次；应保留反向速度及原绑定结构");
            Require(layers.All(l => l?["passes"] == null), "骨骼速度不能写入图像效果passes");
            layers[1]!["rate"] = new JsonObject { ["value"] = .81, ["script"] = "return value;" };
            var dynamic = AlignmentPlanner.Analyze(project with { Scene = System.Text.Json.JsonSerializer.SerializeToElement(scene) }, bundle);
            Require(dynamic.Durations.Count == 3 && dynamic.Phases.Count == 2 && dynamic.Durations[1].RawSeconds == 4.5, "动态速度应提取时长但不能假定固定周期");
            CheckUserBoundRates(root, parent);
            File.WriteAllText(Path.Combine(root, "scene.json"), """{"objects":[{"alpha":{"value":0,"animation":{"options":{"fps":30,"length":60,"mode":"loop"},"c0":[{"frame":0,"value":0},{"frame":60,"value":1}]}}}]}""");
            var timeline = AlignmentPlanner.Analyze(ProjectLoader.Load(root), new AssetBundle(ProjectLoader.Load(root)));
            Require(timeline.Phases.Count == 1 && timeline.Durations.Single().RawSeconds == 2 && timeline.Phases.Single().Note.Contains("回跳"), "作者Loop应保留回场语义并提示接缝");
            var late = Parse("""{"value":1,"animation":{"previewvalue":1,"options":{"fps":10,"length":20,"mode":"mirror","events":[]},"c0":[{"frame":5,"value":0},{"frame":18,"value":1}]}}""");
            Require(AlignmentPlanner.TimelinePeriod(late).Origin == 4, "预览值、空事件和前置区间应保留原往返周期");
            late["animation"]!["options"]!["events"] = new JsonArray(JsonValue.Create("start"));
            blocked = false; try { AlignmentPlanner.TimelinePeriod(late); } catch (InvalidDataException) { blocked = true; }
            Require(blocked, "真实事件必须经过单独验证");

            File.WriteAllBytes(Path.Combine(root, "model.mdl"), Model(6, single: true));
            var controlled = Parse("""
                {"objects":[{"id":1,"image":"model.json","animationlayers":[{"animation":463,"name":"idle"},{"animation":1054,"name":"event"},
                {"animation":377,"visible":{"value":true,"script":"export function init(value) { thisLayer.getAnimationLayer(scriptProperties.target).stop(); return value; } export function animationEvent(event) { thisLayer.getAnimationLayer(scriptProperties.target).play(); }","scriptproperties":{"target":"event"}}}]}]}
                """);
            File.WriteAllText(Path.Combine(root, "scene.json"), controlled.ToJsonString());
            var controlledPlan = ScenePlanner.Build(ProjectLoader.Load(root), new ExportSettings());
            Require(controlledPlan.Alignment.Phases.Count == 1 && controlledPlan.Alignment.Phases[0].Id == "skeletal:0:0", "事件与Single不参与对齐");
            using var prepared = PreparedScene.Create(controlledPlan, controlledPlan.Select(), parent);
            var preparedLayers = Array(At(Parse(prepared.Project.Scene!.Value.GetRawText()), ["objects", "0", "animationlayers"]));
            Require(preparedLayers.Count == 3 && False(preparedLayers[1]?["visible"]) && False(preparedLayers[2]?["visible"]),
                "禁止事件与Single须保留层索引与常驻层");
        }
        finally { SafeFiles.DeleteWorkDirectory(root, parent); }
        Console.WriteLine("PASS skeletal and texture timing: MDLA 1–6 / helper transforms / per-bone scalars / TEXS3 / independent rates / late timeline / event and single suppression");
    }

    private static void CheckUserBoundRates(string root, string parent)
    {
        const string metadata = """
            {"type":"Scene","file":"scene.json","general":{"properties":{
              "body_speed":{"type":"slider","value":1.25},"reverse":{"type":"slider","value":-1.5},
              "weight":{"type":"slider","value":1.3},"stopped":{"type":"slider","value":0},
              "mode":{"type":"combo","value":1},"invalid":{"type":"slider","value":"unknown"}
            }}}
            """;
        const string scene = """
            {"objects":[
              {"id":36,"image":"model.json","animationlayers":[
                {"animation":463,"rate":{"user":"body_speed","value":1},"blend":{"user":"weight","value":1.3}},
                {"animation":1054,"rate":{"user":"body_speed"},"visible":{"value":true,"scriptproperties":{"percentage":0.93},
                 "script":"export function init(value) { const ani = 'addEndedCallback' in thisObject ? thisObject : thisObject.getAnimation(); ani.play(); ani.setFrame(ani.frameCount * scriptProperties.percentage); return value; }"}},
                {"animation":377,"rate":{"user":"reverse","value":1}}]},
              {"id":99,"image":"model.json","animationlayers":[
                {"animation":463,"rate":{"user":"missing","value":1}},
                {"animation":463,"rate":{"user":"mode","value":1}},
                {"animation":463,"rate":{"user":"body_speed","value":1,"animation":{"options":{"mode":"loop"}}}},
                {"animation":463,"rate":{"user":"body_speed","value":1,"script":"export function update(value) { return value; }"}},
                {"animation":463,"rate":{"user":"invalid","value":1}},
                {"animation":463,"rate":{"user":"stopped","value":1}}]}
            ]}
            """;
        string metadataPath = Path.Combine(root, "project.json"), scenePath = Path.Combine(root, "scene.json");
        File.WriteAllText(metadataPath, metadata);
        File.WriteAllText(scenePath, scene);
        var project = ProjectLoader.Load(root);
        var bundle = new AssetBundle(project);
        var alignment = AlignmentPlanner.Analyze(project, bundle);
        var phases = alignment.Phases;
        Require(phases.Select(p => Number(p.Raw)).SequenceEqual(new[] { 1.25, 1.25, -1.5 }), "骨骼原始速度应来自作者滑块的保存值");
        Require(phases.Select(p => p.Origin).SequenceEqual(new[] { 2d, 3.6, 4 }), "应按实际滑块速度计算原始周期");
        Require(Number(phases[1].Settings?["start_phase"]) == 0.93, "对齐时应保留第二段动画的初始相位");
        var unsupportedRates = alignment.Durations.Where(d => d.Kind == "skeletal").Skip(3).ToArray();
        Require(unsupportedRates.Length == 6 && unsupportedRates.All(d => !d.Aligned) && unsupportedRates[^1].Reason == "静止骨骼保持原姿态",
            "未解析的绑定、混合动态绑定或零速动画不能生成对齐时钟");

        var counts = new Dictionary<string, int> { ["skeletal:0:0"] = 3, ["skeletal:0:1"] = 4, ["skeletal:0:2"] = 1 };
        var plan = ScenePlanner.Build(project, new ExportSettings { Duration = 8 }, cycles: counts, bundle: bundle, alignment: alignment);
        var selection = plan.Select();
        Require(selection.Enabled.Contains("model:0"), "全部骨骼周期可对齐后应默认保留此模型");
        using var prepared = PreparedScene.Create(plan, selection, parent);
        var layers = Array(At(Parse(prepared.Project.Scene!.Value.GetRawText()), ["objects", "0", "animationlayers"]));
        Require(layers.Select(l => Number(l?["rate"])).SequenceEqual(new[] { 0.9375, 2.25, -0.75 }), "绑定同一作者滑块的动画应各自使用导出转速");
        Require(layers.All(l => l?["rate"] is not JsonObject rate || rate["user"] == null), "导出速度仍绑定作者滑块");
        Require(Text(layers[0]?["blend"]?["user"]) == "weight", "混合权重应保留作者绑定");
        Require(Number(Array(prepared.ParticleManifest?["clocks"])[1]?["start_phase"]) == 0.93, "录制时钟应收到作者初始相位");
        Require(Number(Parse(prepared.Project.Metadata.GetRawText())["general"]?["properties"]?["body_speed"]?["value"]) == 1.25,
            "导出时不能改写共享的作者滑块值");
        Require(File.ReadAllText(metadataPath) == metadata && File.ReadAllText(scenePath) == scene, "原始素材应保持不变");
    }
}
