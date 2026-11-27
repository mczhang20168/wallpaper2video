using System.Globalization;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;

internal static class AcceptanceCapture
{
    public static async Task<object> RunAsync(ProjectSnapshot project, string output, string? ffmpeg, string? engine,
        CancellationToken cancellation)
    {
        if (project.Kind is not ("scene" or "video")) throw new NotSupportedException("端到端录制支持场景和视频壁纸");
        output = SafeFiles.Resolve(output);
        if (!Path.GetExtension(output).Equals(".mp4", StringComparison.OrdinalIgnoreCase) || SafeFiles.Contains(project.Root, output))
            throw new ArgumentException("原始录制须保存为素材目录外的 MP4 文件");
        if (Path.Exists(output)) throw new IOException("原始录制已存在：" + output);

        var renderer = new VideoRenderer(ffmpeg);
        var sourceVideo = project.Kind == "video" ? await renderer.ProbeAsync(project.Entry, cancellation) : null;
        var settings = new ExportSettings();
        if ((sourceVideo?.Width ?? project.Width) is int w && (sourceVideo?.Height ?? project.Height) is int h && w > 0 && h > 0)
        {
            int width = Math.Clamp(Math.Min(1920, w) / 2 * 2, 16, 8192);
            int height = Math.Clamp((int)Math.Round((double)width * h / w / 2) * 2, 16, 8192);
            settings = settings with { Width = width, Height = height };
        }

        ScenePlan? plan = null;
        SceneSelection? selection = null;
        double recommended = settings.Duration;
        if (project.Kind == "scene")
        {
            var bundle = new AssetBundle(project, engine);
            var alignment = AlignmentPlanner.Analyze(project, bundle);
            var initial = ScenePlanner.Build(project, settings, engine, bundle: bundle, alignment: alignment);
            recommended = initial.RecommendedDuration(Select(initial));
            double duration = Math.Max(recommended, 4d / settings.Fps);
            long frames = FrameMath.Quantize(duration, settings.Fps);
            settings = settings with { Duration = duration, Overlap = Math.Min(settings.FadeFrames, frames / 2) / (double)settings.Fps };
            plan = ScenePlanner.Build(project, settings, engine, bundle: bundle, alignment: alignment);
            selection = Select(plan);
        }

        long rawFrames = checked(settings.Frames + settings.FadeFrames);
        double expected = rawFrames / (double)settings.Fps;
        string parent = Path.GetDirectoryName(output)!;
        string work = SafeFiles.NewWorkDirectory(parent);
        try
        {
            string staged = Path.Combine(work, "capture.mp4");
            if (plan != null && selection != null)
            {
                string executable = EngineCapture.FindEngine(project, engine);
                using var prepared = PreparedScene.Create(plan, selection, Path.Combine(Path.GetTempPath(), "w2v"), cancellation);
                Console.Error.WriteLine($"已加载 {project.Title} · 推荐 {recommended:g} 秒 · 开始录制 {expected:g} 秒");
                var progress = new CaptureProgress();
                try
                {
                    await EngineCapture.CaptureAsync(prepared, staged, settings, renderer.Ffmpeg, executable, progress, cancellation: cancellation);
                }
                catch (TimeoutException error)
                {
                    throw new TimeoutException($"原生录制超时 · {progress.Stage}", error);
                }
            }
            else
            {
                if (sourceVideo!.Duration + .5 / settings.Fps < expected)
                    throw new InvalidDataException($"输入视频至少需要 {expected:g} 秒，目前只有 {sourceVideo.Duration:g} 秒");
                Console.Error.WriteLine($"已加载 {project.Title} · 读取 {expected:g} 秒原始画面");
                string graph = $"fps={settings.Fps},scale={settings.Width}:{settings.Height}:force_original_aspect_ratio=decrease," +
                    $"pad={settings.Width}:{settings.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p";
                await ToolProcess.RunAsync(renderer.Ffmpeg, ["-v", "error", "-nostdin", "-xerror", "-i", project.Entry,
                    "-map", "0:v:0", "-vf", graph, "-an", "-frames:v", rawFrames.ToString(CultureInfo.InvariantCulture),
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "0", staged], cancellation, Math.Max(90, expected * 5));
            }

            var measured = await renderer.ProbeAsync(staged, cancellation);
            if (!double.IsFinite(measured.Duration) || measured.Duration <= 0) throw new InvalidDataException("无法读取实际录制时长");
            cancellation.ThrowIfCancellationRequested();
            File.Move(staged, output);
            return new
            {
                project = new { project.Root, project.Title, project.Kind },
                raw_video = output,
                recommended_seconds = recommended,
                period_seconds = settings.Frames / (double)settings.Fps,
                overlap_frames = settings.FadeFrames,
                fps = settings.Fps,
                expected_raw_seconds = expected,
                actual_raw_seconds = measured.Duration,
                ffmpeg = renderer.Ffmpeg,
                selection = plan == null || selection == null ? null : DescribeSelection(plan, selection)
            };
        }
        finally { SafeFiles.DeleteWorkDirectory(work, parent); }
    }

    private sealed class CaptureProgress : IProgress<RenderProgress>
    {
        public string Stage { get; private set; } = "准备录制";

        public void Report(RenderProgress update)
        {
            if (Stage == update.Stage) return;
            Stage = update.Stage;
            Console.Error.WriteLine(Stage);
        }
    }

    private static SceneSelection Select(ScenePlan plan)
    {
        var particles = plan.ParticleControls().Where(p => p.CanOriginal).ToArray();
        var phases = plan.Alignment.Phases;
        var layers = phases.Where(p => p.EffectPath.Length > 1 && p.EffectPath[0] == "objects")
            .Select(p => int.Parse(p.EffectPath[1], CultureInfo.InvariantCulture)).Concat(particles.Select(p => p.Index)).ToHashSet();
        var objects = Array(plan.Scene?["objects"]);
        bool ContainsTarget(ReviewItem item)
        {
            if (item.Action == "remove_layer")
            {
                var descendants = new HashSet<int> { int.Parse(item.Path[1], CultureInfo.InvariantCulture) };
                AlignmentPlanner.ExpandChildren(objects, descendants);
                return descendants.Overlaps(layers);
            }
            return item.Action is "remove_effect" or "strip_animation" && phases.Any(p => Prefix(item.Path, p.EffectPath));
        }
        var enabled = plan.Optional.Where(i => i.CanEnable && ContainsTarget(i)).Select(i => i.Id);
        return plan.Select(enabled, particles.Select(p => new ParticleChoice(p.Id, "original")));
    }

    private static object DescribeSelection(ScenePlan plan, SceneSelection selection)
    {
        var removed = plan.RemovedLayers(selection);
        var applied = plan.AppliedPhaseIds(selection);
        return new
        {
            phases = plan.Alignment.Phases.Select(p => new
            {
                p.Id,
                p.Label,
                cycles = plan.Cycles[p.Id],
                retained = applied.Contains(p.Id)
            }),
            particles = plan.ParticleControls().Zip(selection.Particles, (control, choice) => new
            { control.Id, control.Label, choice.Mode, retained = !removed.Contains(control.Index), control.Reason }),
            required_exclusions = plan.Required.Select(i => new { i.Id, i.Label, i.Reason }),
        };
    }
}
