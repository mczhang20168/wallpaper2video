using System.Text.Json;
using System.Text.Json.Nodes;
using Wallpaper2Video.Core;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

public sealed class SceneRenderer(string? ffmpeg = null, string? engine = null)
{
    private readonly VideoRenderer video = new(ffmpeg);
    public async Task<VideoArtifact> ExportAsync(ScenePlan plan, SceneSelection selection, string output,
        IProgress<RenderProgress>? progress = null, ILiveMonitor? monitor = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        output = Path.GetFullPath(output);
        string reportPath = Path.ChangeExtension(output, ".report.json");
        if (!Path.GetExtension(output).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("输出文件必须为.mp4");
        if (SafeFiles.Contains(plan.Project.Root, output)) throw new ArgumentException("请将输出保存在壁纸素材目录之外");
        if (Path.Exists(output) || Path.Exists(reportPath)) throw new IOException("输出视频或报告已存在，请使用新文件名");
        // Re-read dependencies at the task boundary; never render a stale UI plan.
        var current = ScenePlanner.Build(ProjectLoader.Load(plan.Project.Entry), plan.Settings, engine, plan.Cycles);
        if (current.Id != plan.Id) throw new InvalidDataException("素材或依赖已变化，请重新载入并检查效果选择");
        current.Validate(selection);
        plan = current;
        string executable = EngineCapture.FindEngine(plan.Project, engine);
        string parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        // WPE still opens particle resources through legacy Windows path APIs. PreviewLease
        // already owns a cache directory; nesting render and prepared projects beneath it can
        // push a timed emitter's child file beyond MAX_PATH, crashing the native engine.
        // Keep engine inputs in a short, per-job temp directory, independent of output depth.
        string scratch = Path.Combine(Path.GetTempPath(), "w2v");
        string work = SafeFiles.NewWorkDirectory(scratch);
        try
        {
            progress?.Report(new("应用内容筛选与周期对齐…", 0));
            using var prepared = PreparedScene.Create(plan, selection, scratch, cancellation);
            string raw = Path.Combine(work, "capture.mp4");
            var capture = await EngineCapture.CaptureAsync(prepared, raw, plan.Settings, video.Ffmpeg, executable,
                new ForwardProgress(p => progress?.Report(p with { Fraction = p.Fraction * .6 })), monitor, cancellation);
            string staged = Path.Combine(work, "loop.mp4");
            var artifact = await video.ExportAsync(ProjectLoader.Load(raw), staged, plan.Settings,
                new ForwardProgress(p => progress?.Report(p with { Fraction = .6 + p.Fraction * .4 })), cancellation);
            var report = Parse(await File.ReadAllTextAsync(artifact.ReportPath, cancellation));
            report["schema_version"] = 2; report["implementation"] = "csharp-scene-v1";
            report["implementation_sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(typeof(SceneRenderer).Assembly.Location, cancellation)));
            report["project"] = new JsonObject { ["title"] = plan.Project.Title, ["kind"] = plan.Project.Kind, ["source"] = plan.Project.Entry };
            report["output"] = output; report["content_policy"] = JsonSerializer.SerializeToNode(plan.Summary(selection), Output);
            var alignment = JsonSerializer.SerializeToNode(plan.Alignment.Summary(plan.Duration, plan.Cycles), Output)!.AsObject();
            var phases = Array(alignment["phases"]);
            foreach (var phase in phases)
                phase!["applied"] = prepared.AppliedPhaseIds.Contains(Text(phase["id"]));
            report["period_alignment"] = alignment;
            report["particle_validation"] = capture.ParticleValidation?.DeepClone();
            report["particle_emission"] = prepared.ParticleEmission.DeepClone();
            report["recording_display"] = monitor == null ? "independent" : "workspace";
            report["source_client_width"] = capture.Width; report["source_client_height"] = capture.Height;
            report["source_window_closed"] = !NativeWindows.IsWindow(capture.SourceWindow);
            report["engine"] = executable; report["ffmpeg"] = video.Ffmpeg;
            report["engine_project_max_path_length"] = Directory.EnumerateFiles(prepared.Project.Root, "*", SearchOption.AllDirectories).Max(p => p.Length);
            report["engine_version"] = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).FileVersion;
            report["ffmpeg_version"] = (await ToolProcess.RunAsync(video.Ffmpeg, ["-version"], cancellation)).Split('\n')[0].Trim();
            using (var rules = typeof(SceneRenderer).Assembly.GetManifestResourceStream("Wallpaper2Video.reviewed_effects.json")!)
                report["rules_sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(rules));
            await File.WriteAllTextAsync(artifact.ReportPath, report.ToJsonString(Output), cancellation);
            await PublishAsync(staged, artifact.ReportPath, output, reportPath, cancellation);
            progress?.Report(new("导出完成", 1, RenderActivity.Completed, 1));
            return new(output, reportPath, artifact.Validation);
        }
        finally { SafeFiles.DeleteWorkDirectory(work, scratch); }
    }
    internal static async Task PublishAsync(string staged, string stagedReport, string output, string report, CancellationToken cancellation)
    {
        var created = new List<string>();
        try
        {
            foreach (var (source, destination) in new[] { (staged, output), (stagedReport, report) })
            {
                await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created.Add(destination);
                await using var input = File.OpenRead(source);
                await input.CopyToAsync(target, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
        }
        catch { foreach (string path in created) File.Delete(path); throw; }
    }
    private sealed class ForwardProgress(Action<RenderProgress> report) : IProgress<RenderProgress> { public void Report(RenderProgress value) => report(value); }
}
