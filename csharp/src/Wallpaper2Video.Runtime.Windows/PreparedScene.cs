using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wallpaper2Video.Core;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

public sealed class PreparedScene : IDisposable
{
    private readonly string work, parent;
    public ProjectSnapshot Project { get; }
    public JsonObject? ParticleManifest { get; }
    public JsonObject ParticleEmission { get; }
    public IReadOnlySet<string> AppliedPhaseIds { get; }
    private PreparedScene(string work, string parent, ProjectSnapshot project, JsonObject? manifest, JsonObject emission, IReadOnlySet<string> appliedPhaseIds)
    {
        this.work = work;
        this.parent = parent;
        Project = project;
        ParticleManifest = manifest;
        ParticleEmission = emission;
        AppliedPhaseIds = appliedPhaseIds;
    }
    public static PreparedScene Create(ScenePlan plan, SceneSelection selection, string parent, CancellationToken cancellation = default)
    {
        plan.Validate(selection);
        if (SafeFiles.Contains(plan.Project.Root, parent) || string.Equals(SafeFiles.Resolve(parent), plan.Project.Root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("导出副本必须位于原始素材目录之外");
        string work = SafeFiles.NewWorkDirectory(parent), root = Path.Combine(work, "project");
        try
        {
            if (plan.Project.PackagePath != null) PackageExtractor.Extract(plan.Project.PackagePath, root, cancellation);
            else Directory.CreateDirectory(root);
            void Copy(string directory)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    string name = Path.GetFileName(child);
                    if (name.StartsWith("blobs", StringComparison.OrdinalIgnoreCase) || name is ".git" or "__pycache__" || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    Copy(child);
                }
                foreach (string source in Directory.EnumerateFiles(directory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (source.Equals(plan.Project.Entry, StringComparison.OrdinalIgnoreCase) || source.Equals(plan.Project.PackagePath, StringComparison.OrdinalIgnoreCase)) continue;
                    string resolved = SafeFiles.Resolve(source);
                    if (!SafeFiles.Contains(plan.Project.Root, resolved)) throw new InvalidDataException("素材文件越过原始目录：" + source);
                    string destination = SafeFiles.Member(root, Path.GetRelativePath(plan.Project.Root, source).Replace('\\', '/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(resolved, destination, true);
                }
            }
            Copy(plan.Project.Root);
            foreach (var (name, data) in plan.Bundle.BinaryFiles)
            {
                string destination = SafeFiles.Member(root, name); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.WriteAllBytes(destination, data);
            }
            foreach (var (name, source) in plan.Bundle.Files)
            {
                string destination = SafeFiles.Member(root, name); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.WriteAllText(destination, source);
            }
            var scene = plan.Scene ?? throw new ArgumentException("需要场景素材");
            var content = plan.ResolveContent(selection);
            AlignmentPatcher.Apply(scene, root, plan.Alignment, plan.Cycles, plan.Duration, content.AppliedPhaseIds);
            foreach (var path in content.StoppedBones)
                if (At(scene, path) is JsonObject bone) { bone["visible"] = false; bone["blend"] = 0; }
            foreach (var item in content.PropertyEdits)
            {
                cancellation.ThrowIfCancellationRequested();
                switch (item.Action)
                {
                    case "strip_script":
                    case "strip_animation":
                        if (At(scene, item.Path) is JsonObject binding)
                        {
                            var replacement = (JsonObject)binding.DeepClone();
                            if (item.Action == "strip_script") { replacement.Remove("script"); replacement.Remove("scriptproperties"); } else replacement.Remove("animation");
                            if (item.Path.Length == 0) { foreach (string key in binding.Select(p => p.Key).ToArray()) binding.Remove(key); foreach (var (key, value) in replacement) binding[key] = value?.DeepClone(); }
                            else Set(scene, item.Path, replacement.Count == 1 && replacement.ContainsKey("value") ? replacement["value"]?.DeepClone() : replacement);
                        }
                        break;
                    case "false": Set(scene, item.Path, JsonValue.Create(false)); break;
                }
            }
            if (scene["general"] is not JsonObject general) scene["general"] = general = new();
            general["cameraparallax"] = false; general["cameraparallaxmouseinfluence"] = 0;
            var objects = Array(scene["objects"]);
            foreach (var particle in selection.Particles.Where(p => p.Mode != "off"))
                objects[int.Parse(particle.Id.Split(':')[1], CultureInfo.InvariantCulture)]!["visible"] = true;
            foreach (var (index, visible) in selection.SourceVisibility) if (visible) objects[index]!["visible"] = true;
            for (int oi = 0; oi < objects.Count; oi++)
                if (objects[oi]?["effects"] is JsonArray list)
                    for (int ei = list.Count - 1; ei >= 0; ei--) if (content.RemovedEffects.Contains((oi, ei))) list.RemoveAt(ei);
            for (int oi = objects.Count - 1; oi >= 0; oi--) if (content.RemovedLayers.Contains(oi)) objects.RemoveAt(oi);
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                cancellation.ThrowIfCancellationRequested();
                if (Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(n => n.StartsWith("blobs", StringComparison.OrdinalIgnoreCase))) { File.Delete(path); continue; }
                if (Path.GetExtension(path).ToLowerInvariant() is ".vert" or ".frag" or ".h" or ".geom" or ".comp") File.WriteAllText(path, Neutralize(File.ReadAllText(path)));
            }
            var metadata = Parse(plan.Project.Metadata.GetRawText());
            metadata["type"] = "Scene"; metadata["file"] = "scene.json"; metadata["title"] = plan.Project.Title + " [导出副本]"; metadata.Remove("workshopid");
            EntrancePolicy.Apply(plan, scene, metadata);
            var videos = EmbeddedVideoPlayback.Plan(plan, scene, root);
            JsonObject? manifest = ParticlePlanner.Prepare(scene, metadata, root, plan.ParticlePlan, selection.Particles, plan);
            if (manifest != null) manifest["video_transcodes"] = videos;
            var emission = ParticleEmissionPolicy.Apply(scene, root, plan.Bundle);
            File.WriteAllText(Path.Combine(root, "scene.json"), scene.ToJsonString(Output));
            File.WriteAllText(Path.Combine(root, "project.json"), metadata.ToJsonString(Output));
            cancellation.ThrowIfCancellationRequested();
            return new(work, parent, ProjectLoader.Load(root), manifest, emission, content.AppliedPhaseIds);
        }
        catch { SafeFiles.DeleteWorkDirectory(work, parent); throw; }
    }
    public static string Neutralize(string source)
    {
        string result = Regex.Replace(source, @"\buniform\s+(\w+)\s+(g_(?:Pointer|Parallax|Mouse)\w*)\s*;", m =>
        {
            string type = m.Groups[1].Value;
            string value = type switch
            {
                "bool" => "false",
                "float" or "int" => "0",
                "vec2" => "vec2(0.5, 0.5)",
                "vec3" => "vec3(0.5, 0.5, 0.0)",
                "vec4" => "vec4(0.5, 0.5, 0.0, 0.0)",
                _ => throw new InvalidDataException("无法安全固定输入 " + m.Groups[2].Value + " 的类型：" + type)
            };
            return "const " + type + " " + m.Groups[2].Value + " = " + value + ";";
        });
        if (Regex.IsMatch(CodeOnly(result), @"\buniform\s+\w+\s+g_(?:Pointer|Parallax|Mouse)\w*")) throw new InvalidDataException("检测到未支持的鼠标输入声明，无法保证排除");
        return result;
    }
    public void Dispose() => SafeFiles.DeleteWorkDirectory(work, parent);
}
