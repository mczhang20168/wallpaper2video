using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;

internal static class RuntimeChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static byte[] Package(params (string Name, byte[] Bytes)[] files)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(8u); writer.Write("PKGV0016"u8); writer.Write((uint)files.Length);
        uint offset = 0;
        foreach (var file in files)
        {
            byte[] name = Encoding.UTF8.GetBytes(file.Name);
            writer.Write((uint)name.Length); writer.Write(name); writer.Write(offset); writer.Write((uint)file.Bytes.Length);
            offset += (uint)file.Bytes.Length;
        }
        foreach (var file in files) writer.Write(file.Bytes);
        return output.ToArray();
    }

    public static async Task RunAsync(string fixtures)
    {
        string parent = Path.GetTempPath();
        string root = SafeFiles.NewWorkDirectory(parent);
        try
        {
            string input = Path.Combine(fixtures, "packages", "unicode.pkg");
            string sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));
            string unpacked = Path.Combine(root, "unpacked");
            Require(PackageExtractor.Extract(input, unpacked) == 3, "安全解包数量错误");
            Require(File.ReadAllBytes(Path.Combine(unpacked, "textures", "星.tex")).SequenceEqual(Enumerable.Range(0, 256).Select(i => (byte)i)), "解包二进制内容错误");
            bool rejected = false;
            try { PackageExtractor.Extract(input, unpacked); } catch (IOException) { rejected = true; }
            Require(rejected, "不能覆盖已有解包目录");
            foreach (var names in new[] { new[] { "a//b", "a/b" }, new[] { "a", "a/b" } })
            {
                string bad = Path.Combine(root, Guid.NewGuid() + ".pkg");
                File.WriteAllBytes(bad, Package((names[0], new byte[] { 1 }), (names[1], new byte[] { 2 })));
                string target = Path.Combine(root, Guid.NewGuid().ToString());
                rejected = false;
                try { PackageExtractor.Extract(bad, target); } catch (InvalidDataException) { rejected = true; }
                Require(rejected && !Directory.Exists(target), "路径冲突未在写入前拒绝");
            }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { PackageExtractor.Extract(input, Path.Combine(root, "cancelled"), cancelled.Token); throw new InvalidOperationException("取消无效"); }
            catch (OperationCanceledException) { }
            Require(!Directory.Exists(Path.Combine(root, "cancelled")), "取消留下解包目录");
            string projectRoot = Path.Combine(root, "scene"); Directory.CreateDirectory(projectRoot);
            File.Copy(input, Path.Combine(projectRoot, "scene.pkg"));
            File.WriteAllText(Path.Combine(projectRoot, "project.json"), "{\"title\":\"星空🙂\",\"type\":\"Scene\",\"file\":\"scene.json\"}");
            var project = ProjectLoader.Load(projectRoot);
            Require(project.Title == "星空🙂" && project.Kind == "scene" && project.PackageFiles == 3 && project.Objects == 0, "打包素材加载错误");
            Require(ProjectLoader.Load(Path.Combine(projectRoot, "project.json")).PackageVersion == "PKGV0016", "项目文件入口错误");
            string loose = Path.Combine(root, "loose"); Directory.CreateDirectory(loose);
            File.WriteAllText(Path.Combine(loose, "scene.json"), "{\"general\":{\"orthogonalprojection\":{\"width\":4096,\"height\":2044}},\"objects\":[{\"name\":\"层\",\"image\":\"x\",\"effects\":[{\"file\":\"effects/shine/effect.json\"}]}]}");
            var scene = ProjectLoader.Load(Path.Combine(loose, "scene.json"));
            Require(scene.Width == 4096 && scene.Images == 1 && scene.Effects[0].Key == "effect:0:0", "松散场景加载错误");
            File.WriteAllText(Path.Combine(loose, "scene.json"), """
                {"objects":[{"image":false,"particle":null},{"image":"","particle":0},
                            {"image":{},"particle":[]},{"image":"x","particle":{"file":"p.json"}}]}
                """);
            scene = ProjectLoader.Load(Path.Combine(loose, "scene.json"));
            Require(scene.Images == 1 && scene.Particles == 1, "图像/粒子计数与Python真值规则不一致");
            File.WriteAllText(Path.Combine(loose, "scene.json"), """
                {"objects":[
                  {"name":"粒子层","particle":"particles/p.json","effects":[
                    {"file":"effects/shine/effect.json","visible":false},
                    {"file":"effects/shake/effect.json"}]},
                  {"visible":{"value":false},"particle":"particles/hidden.json","effects":[
                    {"file":"effects/blur/effect.json"}]},
                  {"name":"图像层","image":"x","effects":[
                    {"file":"effects/spin/effect.json","visible":{"value":false}},
                    {"file":"effects/tint/effect.json","visible":{"value":true}}]}
                ]}
                """);
            scene = ProjectLoader.Load(Path.Combine(loose, "scene.json"));
            Require(scene.Effects.Select(e => e.Key).SequenceEqual(new[] { "particle:0", "effect:0:1", "effect:2:1" }),
                "场景列表应包括粒子并过滤隐藏层/隐藏效果，同时保留原始索引");
            Require(scene.Particles == 2 && scene.Images == 1, "列表筛选不能改变原始图层统计");
            Console.WriteLine("PASS scene inventory: particles / visibility wrappers / stable source indices");
            foreach (string malformed in new[] { "{\"general\":null}", "{\"general\":{\"orthogonalprojection\":[]}}",
                "{\"objects\":{}}", "{\"objects\":[false]}", "{\"objects\":[{\"effects\":null}]}", "{\"objects\":[{\"effects\":[1]}]}" })
            {
                File.WriteAllText(Path.Combine(loose, "scene.json"), malformed);
                rejected = false;
                try { ProjectLoader.Load(Path.Combine(loose, "scene.json")); } catch (InvalidDataException) { rejected = true; }
                Require(rejected, "畸形场景应返回可报告的素材错误：" + malformed);
            }
            File.WriteAllText(Path.Combine(loose, "project.json"), "{\"type\":\"Scene\",\"file\":\"../escape.json\"}");
            rejected = false;
            try { ProjectLoader.Load(loose); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "素材引用不能越界");
            Require(sourceHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))), "读取或解包修改了输入包");
            Console.WriteLine("PASS project loading / malformed scenes / Python counts / safe extraction / collisions / cancellation");
            if (Environment.GetEnvironmentVariable("W2V_FFMPEG_TESTS") == "1") await VideoTests(root);
        }
        finally { SafeFiles.DeleteWorkDirectory(root, parent); }
    }

    private sealed class InlineProgress(Action<RenderProgress> action) : IProgress<RenderProgress>
    { public void Report(RenderProgress value) => action(value); }

    private static async Task VideoTests(string root)
    {
        var renderer = new VideoRenderer();
        string input = Path.Combine(root, "input with spaces.mp4");
        await ToolProcess.RunAsync(renderer.Ffmpeg, ["-v", "error", "-nostdin", "-f", "lavfi", "-i",
            "nullsrc=size=160x90:rate=12:duration=5,geq=lum='40+30*T':cb=128:cr=128", "-c:v", "libx264", "-pix_fmt", "yuv420p", input], CancellationToken.None);
        var source = ProjectLoader.Load(input);
        string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));
        var settings = new ExportSettings { Duration = 4, Overlap = 1, Fps = 12, Width = 160, Height = 90, Crf = 14 };
        var artifact = await renderer.ExportAsync(source, Path.Combine(root, "loop.mp4"), settings);
        Require(artifact.Validation.Frames == 48 && artifact.Validation.Width == 160 && artifact.Validation.SeamMae0255 < 5, "循环视频验收失败");
        Require(File.Exists(artifact.ReportPath), "未生成报告");
        bool rejected = false;
        try { await renderer.ExportAsync(source, artifact.Path, settings); } catch (IOException) { rejected = true; }
        Require(rejected, "已有文件不能覆盖");
        string shortOutput = Path.Combine(root, "short.mp4");
        try { await renderer.ExportAsync(source, shortOutput, settings with { Duration = 5 }); throw new InvalidOperationException("短输入未拒绝"); }
        catch (InvalidDataException) { }
        Require(!File.Exists(shortOutput), "短输入留下输出");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Stage.Contains("编码")) cancellation.Cancel(); });
        string cancelled = Path.Combine(root, "cancel.mp4");
        try { await renderer.ExportAsync(source, cancelled, settings, progress, cancellation.Token); throw new InvalidOperationException("取消无效"); }
        catch (OperationCanceledException) { }
        Require(!File.Exists(cancelled) && !File.Exists(Path.ChangeExtension(cancelled, ".report.json")), "取消留下半成品");
        string cache = Path.Combine(root, "cache");
        var preview = await PreviewLease.RenderAsync(renderer, source, settings, cache, 640, null, CancellationToken.None);
        Require(preview.Artifact.Validation.Width == 640 && preview.Artifact.Validation.Frames == 48, "MP4预览尺寸错误");
        await preview.DisposeAsync();
        Require(!Directory.EnumerateFileSystemEntries(cache).Any(), "预览缓存未释放");
        Require(!Directory.EnumerateDirectories(root, ".w2v-cs-*").Any(), "临时目录未清理");
        Require(digest == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))), "输入视频被修改");
        Console.WriteLine("PASS FFmpeg: 48 frames / seam / MP4 preview / overwrite / short input / cancellation / cleanup");
    }
}
