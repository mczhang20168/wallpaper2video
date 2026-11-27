using Wallpaper2Video.Core;

namespace Wallpaper2Video.Runtime.Windows;

public sealed class PreviewLease : IAsyncDisposable
{
    private readonly string directory;
    private readonly string parent;
    private bool disposed;
    public VideoArtifact Artifact { get; }

    private PreviewLease(string directory, string parent, VideoArtifact artifact)
    { this.directory = directory; this.parent = parent; Artifact = artifact; }

    public static async Task<PreviewLease> RenderAsync(VideoRenderer renderer, ProjectSnapshot project, ExportSettings settings,
        string cacheDirectory, int? width, IProgress<RenderProgress>? progress, CancellationToken cancellation)
    {
        int w = width ?? settings.Width;
        if (w is < 16 or > 8192) throw new ArgumentOutOfRangeException(nameof(width));
        int h = Math.Max(16, checked((int)Math.Round((double)w * settings.Height / settings.Width / 2)) * 2);
        if (h > 8192) { h = 8192; w = Math.Max(16, (int)Math.Round((double)h * settings.Width / settings.Height / 2) * 2); }
        string work = SafeFiles.NewWorkDirectory(cacheDirectory);
        try
        {
            var artifact = await renderer.ExportAsync(project, Path.Combine(work, "preview.mp4"), settings with { Width = w - w % 2, Height = h }, progress, cancellation);
            return new(work, cacheDirectory, artifact);
        }
        catch { SafeFiles.DeleteWorkDirectory(work, cacheDirectory); throw; }
    }

    public static async Task<PreviewLease> RenderSceneAsync(SceneRenderer renderer, ScenePlan plan, SceneSelection choices,
        string cacheDirectory, int? width, string? engine, IProgress<RenderProgress>? progress, ILiveMonitor? monitor, CancellationToken cancellation)
    {
        int w = width ?? plan.Settings.Width;
        int h = Math.Max(16, checked((int)Math.Round((double)w * plan.Settings.Height / plan.Settings.Width / 2)) * 2);
        if (h > 8192) { h = 8192; w = Math.Max(16, (int)Math.Round((double)h * plan.Settings.Width / plan.Settings.Height / 2) * 2); }
        var settings = plan.Settings with { Width = w - w % 2, Height = h };
        var local = ScenePlanner.Build(plan.Project, settings, engine, plan.Cycles);
        var selected = local.Select(choices.Enabled, choices.Particles, choices.DisabledEffects, choices.SourceVisibility);
        string work = SafeFiles.NewWorkDirectory(cacheDirectory);
        try
        {
            var artifact = await renderer.ExportAsync(local, selected, Path.Combine(work, "preview.mp4"), progress, monitor, cancellation);
            return new(work, cacheDirectory, artifact);
        }
        catch { SafeFiles.DeleteWorkDirectory(work, cacheDirectory); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        for (int attempt = 0; ; attempt++)
        {
            try { SafeFiles.DeleteWorkDirectory(directory, parent); disposed = true; return; }
            catch (IOException) when (attempt < 8) { await Task.Delay(150); }
        }
    }
}
