using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;

namespace Wallpaper2Video.Desktop;

public sealed class WorkspaceCommand(Action execute, Func<bool>? enabled = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => enabled?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}
public sealed record PreviewQuality(string Label, int? Width)
{
    public override string ToString() => Label;
}

public sealed partial class WorkspaceModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? CloseMedia;
    public event Action<bool>? PlayRequested;
    public event Action<double>? SeekRequested;
    private ProjectSnapshot? project;
    private PreviewLease? preview;
    private CancellationTokenSource? cancellation;
    private string loadedSource = "";
    private double? sourceVideoDuration;
    private string sourcePath = "", outputPath = "", title = "打开壁纸项目或视频", projectInfo = "Scene / Video", status = "选择视频文件，开始制作循环成片。";
    private string durationText = "20", fpsText = "30", widthText = "1920", heightText = "1080", overlapText = "2", crfText = "18", ffmpegPath = "";
    private bool busy, isPlaying, mediaReady, generatedPreview, stale;
    private double progress, position, mediaDuration;
    private double recordingProgress, exportProgress;
    private bool isRecording;
    public double RecordingProgress { get => recordingProgress; private set { Set(ref recordingProgress, value); Notify(nameof(RecordingProgressText)); } }
    public double ExportProgress { get => exportProgress; private set { Set(ref exportProgress, value); Notify(nameof(ExportProgressText)); } }
    public bool IsRecording { get => isRecording; private set => Set(ref isRecording, value); }
    public bool RecordingApplicable => project?.Kind == "scene";
    public string RecordingProgressText => RecordingApplicable ? $"录制 {RecordingProgress:0}%" : "录制 · 视频素材无需录制";
    public string ExportProgressText => $"合成 / 导出 {ExportProgress:0}%";
    private double expectedMediaDuration;
    private bool scrubbing, resumeAfterScrub;
    private DateTime ignorePlaybackUntil;
    private double? awaitingSeek;
    private long lastScrubSeek;
    private readonly System.Windows.Threading.DispatcherTimer scrubTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private Uri? mediaSource;
    private ImageSource? posterSource;
    private string viewerInfo = "画面监视器", detail = "选择右侧条目查看详细信息。", validationText = "";
    private EffectRow? selectedEffect;
    private PreviewQuality selectedQuality;
    public ObservableCollection<EffectRow> Effects { get; } = [];
    public IReadOnlyList<PreviewQuality> Qualities { get; } = [new("流畅 · 640px", 640), new("高清 · 1280px", 1280), new("超清 · 1920px", 1920), new("导出分辨率", null)];
    public ICommand OpenCommand { get; }
    public ICommand FolderCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand OpenOutputCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }

    public WorkspaceModel()
    {
        selectedQuality = Qualities[1];
        InitializeSceneWorkspace();
        scrubTimer.Tick += (_, _) => { scrubTimer.Stop(); if (scrubbing) SendScrubSeek(); };
        OpenCommand = new WorkspaceCommand(async () => { var picker = new OpenFileDialog { Filter = "壁纸与视频|*.json;*.pkg;*.mp4;*.webm;*.mkv;*.mov;*.avi;*.m4v;*.gif|所有文件|*.*" }; if (picker.ShowDialog() == true) await LoadAsync(picker.FileName); }, () => CanEdit);
        FolderCommand = new WorkspaceCommand(async () => { var picker = new OpenFolderDialog { Title = "打开壁纸文件夹" }; if (picker.ShowDialog() == true) await LoadAsync(picker.FolderName); }, () => CanEdit);
        LoadCommand = new WorkspaceCommand(async () => await LoadAsync(SourcePath), () => CanEdit && SourcePath.Length > 0);
        SaveCommand = new WorkspaceCommand(ChooseOutput, () => CanEdit);
        PreviewCommand = new WorkspaceCommand(async () => await RenderAsync(true), () => CanRender);
        ExportCommand = new WorkspaceCommand(async () => await RenderAsync(false), () => CanRender);
        CancelCommand = new WorkspaceCommand(() => cancellation?.Cancel(), () => Busy && cancellation != null);
        PlayCommand = new WorkspaceCommand(() => { IsPlaying = !IsPlaying; PlayRequested?.Invoke(IsPlaying); }, () => mediaReady && !Busy && !(generatedPreview && stale));
        OpenOutputCommand = new WorkspaceCommand(() => OpenPath(OutputPath), () => !Busy && File.Exists(OutputPath));
        OpenOutputFolderCommand = new WorkspaceCommand(() => OpenPath(Path.GetDirectoryName(OutputPath) ?? OutputDirectory), () => !Busy);
        UpdateValidation();
    }
    private void Notify([CallerMemberName] string? property = null) { PropertyChanged?.Invoke(this, new(property)); if (property is not (nameof(Position) or nameof(TimeText) or nameof(Status) or nameof(Progress) or nameof(RecordingProgress) or nameof(ExportProgress) or nameof(RecordingProgressText) or nameof(ExportProgressText))) CommandManager.InvalidateRequerySuggested(); }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(name); return true; }
    private void Edit(ref string field, string value, string name) { if (Set(ref field, value, name)) { MarkStale(); UpdateValidation(); ScheduleScenePlan(); } }
    public string SourcePath { get => sourcePath; set { if (Set(ref sourcePath, value)) { Notify(nameof(CanRender)); if (value != loadedSource) Status = "素材路径已更改，请载入素材。"; } } }
    public string OutputPath { get => outputPath; set => Set(ref outputPath, value); }
    public string Title { get => title; private set => Set(ref title, value); }
    public string ProjectInfo { get => projectInfo; private set => Set(ref projectInfo, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string DurationText { get => durationText; set => Edit(ref durationText, value, nameof(DurationText)); }
    public string FpsText { get => fpsText; set => Edit(ref fpsText, value, nameof(FpsText)); }
    public string WidthText { get => widthText; set => Edit(ref widthText, value, nameof(WidthText)); }
    public string HeightText { get => heightText; set => Edit(ref heightText, value, nameof(HeightText)); }
    public string OverlapText { get => overlapText; set => Edit(ref overlapText, value, nameof(OverlapText)); }
    public string CrfText { get => crfText; set => Edit(ref crfText, value, nameof(CrfText)); }
    public string FfmpegPath { get => ffmpegPath; set => Edit(ref ffmpegPath, value, nameof(FfmpegPath)); }
    public string ValidationText { get => validationText; private set => Set(ref validationText, value); }
    public string ViewerInfo { get => viewerInfo; private set => Set(ref viewerInfo, value); }
    public string Detail { get => detail; private set => Set(ref detail, value); }
    public string ContentHint => project?.Kind == "video" ? "已合成视频中的效果无法拆分，可直接生成循环预览与成片。" : project == null ? "打开素材后显示效果信息。" : SceneSummary;
    public string EffectCountText => $"{Effects.Count} 项 · {Effects.Count(e => e.Kind == "effect")} 个图像效果 / {Effects.Count(e => e.Kind == "particle")} 个粒子层";
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) { Notify(nameof(CanEdit)); Notify(nameof(CanRender)); Notify(nameof(CanSeek)); } } }
    public bool CanEdit => !Busy;
    public bool CanSeek => CanEdit && mediaReady && MediaDuration > 0;
    public bool CanRender => CanEdit && project != null && (project.Kind == "video" || project.Kind == "scene" && SceneReady) && SourcePath == loadedSource && TrySettings(out _, out _);
    public double Progress { get => progress; private set => Set(ref progress, value); }
    private double BoundPosition(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, MediaDuration) : 0;
    public double Position { get => position; set { double target = Math.Min(BoundPosition(Math.Round(value / FrameStep) * FrameStep), Math.Max(0, MediaDuration - FrameStep)); if (Set(ref position, target)) { Notify(nameof(TimeText)); if (!scrubbing) CommitSeek(); else if (Environment.TickCount64 - lastScrubSeek >= 70) SendScrubSeek(); else scrubTimer.Start(); } } }
    private void SendScrubSeek() { lastScrubSeek = Environment.TickCount64; CommitSeek(); }
    private void CommitSeek()
    {
        awaitingSeek = Position; ignorePlaybackUntil = DateTime.UtcNow.AddSeconds(1);
        // Seeking to the container end can raise MediaEnded and restart a looping player.
        SeekRequested?.Invoke(Math.Min(Position, Math.Max(0, MediaDuration - FrameStep)));
    }
    public void BeginScrub()
    {
        if (scrubbing) return;
        scrubbing = true; lastScrubSeek = 0; resumeAfterScrub = IsPlaying; IsPlaying = false; PlayRequested?.Invoke(false);
    }
    public void EndScrub()
    {
        if (!scrubbing) return;
        scrubTimer.Stop(); scrubbing = false; CommitSeek();
        IsPlaying = resumeAfterScrub && mediaReady && !Busy && !(generatedPreview && stale);
        PlayRequested?.Invoke(IsPlaying);
    }
    public double MediaDuration { get => mediaDuration; private set { Set(ref mediaDuration, value); Notify(nameof(TimeText)); } }
    public string TimeText => $"{TimeSpan.FromSeconds(Math.Max(0, Position)):mm\\:ss\\.fff} / {TimeSpan.FromSeconds(Math.Max(0, MediaDuration)):mm\\:ss\\.fff}";
    public bool IsPlaying { get => isPlaying; private set { Set(ref isPlaying, value); Notify(nameof(PlayText)); } }
    public string PlayText => IsPlaying ? "Ⅱ 暂停" : "▶ 播放";
    public Uri? MediaSource { get => mediaSource; private set => Set(ref mediaSource, value); }
    public ImageSource? PosterSource { get => posterSource; private set => Set(ref posterSource, value); }
    public PreviewQuality SelectedQuality { get => selectedQuality; set { if (Set(ref selectedQuality, value)) MarkStale(); } }
    public EffectRow? SelectedEffect { get => selectedEffect; set { Set(ref selectedEffect, value); if (value != null && (selectedKeys.Count != 1 || !selectedKeys.Contains(value.Key))) { selectedKeys.Clear(); selectedKeys.Add(value.Key); NotifySelection(); BrowserChanged?.Invoke(); } UpdateSceneDetail(); } }
    public string PreviewNotice => generatedPreview && stale ? $"旧预览 {MediaDuration:0.###} 秒 · 设置已更改，请重新生成" : generatedPreview ? $"{(PreserveEntrance ? "普通视频预览 · 保留入场" : "循环合成预览")} · {MediaDuration:0.###} 秒" :
        project == null ? "打开素材后查看画面" : project.Kind == "video" ? "源视频 · 生成预览可查看循环处理结果" :
        PosterSource != null ? "素材封面 · 生成预览查看当前合成效果" : "尚未生成合成预览";

    private static string OutputDirectory
    {
        get
        {
            DirectoryInfo? path = new(AppContext.BaseDirectory);
            while (path != null)
            {
                if (File.Exists(Path.Combine(path.FullName, "pyproject.toml"))) return Path.Combine(path.FullName, "output");
                path = path.Parent;
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Wallpaper2Video");
        }
    }
    private bool TrySettings(out ExportSettings settings, out string error)
    {
        try
        {
            settings = new()
            {
                Duration = double.Parse(DurationText, CultureInfo.InvariantCulture),
                Fps = int.Parse(FpsText, CultureInfo.InvariantCulture),
                Width = int.Parse(WidthText, CultureInfo.InvariantCulture),
                Height = int.Parse(HeightText, CultureInfo.InvariantCulture),
                Overlap = double.Parse(OverlapText, CultureInfo.InvariantCulture),
                Crf = int.Parse(CrfText, CultureInfo.InvariantCulture),
                Warmup = AutomaticStartupWait ? 0 : double.Parse(WarmupText, CultureInfo.InvariantCulture),
                AutomaticStartupWait = AutomaticStartupWait,
                ParticleMode = "original",
                PreserveEntrance = PreserveEntrance
            };
            settings.Validate();
            if (project?.Kind == "video" && sourceVideoDuration is double available && available + 1e-6 < (double)(settings.Frames + settings.FadeFrames) / settings.Fps)
                throw new ArgumentException($"源视频仅 {available:0.###} 秒，需要至少 {(double)(settings.Frames + settings.FadeFrames) / settings.Fps:0.###} 秒（总长＋交叠），请缩短总长或交叠。");
            error = ""; return true;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
        { settings = new(); error = "请检查视频参数：" + e.Message; return false; }
    }
    private void UpdateValidation()
    {
        ValidationText = TrySettings(out var settings, out string error) ? $"{settings.Frames} 帧 · 实际总长 {(double)settings.Frames / settings.Fps:0.###} 秒" : error;
        Notify(nameof(CanRender));
    }
    private void MarkStale()
    {
        stale = true; IsPlaying = false; PlayRequested?.Invoke(false); Notify(nameof(PreviewNotice));
    }
    private VideoRenderer Renderer() => new(string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath);

    public async Task LoadAsync(string source)
    {
        if (!CanEdit || string.IsNullOrWhiteSpace(source)) return;
        cancellation = new(); Busy = true; Status = "正在读取素材…";
        try
        {
            await ReleasePreviewAsync();
            project = null; sourceVideoDuration = null; PosterSource = null; Effects.Clear(); SelectedEffect = null;
            ResetSceneWorkspace();
            DurationText = "20"; OverlapText = "2";
            generatedPreview = false; stale = false;
            project = await Task.Run(() => ProjectLoader.Load(source), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            loadedSource = source; SourcePath = source; Title = project.Title;
            ProjectInfo = $"{project.Kind.ToUpperInvariant()} · {project.Width?.ToString() ?? "—"} × {project.Height?.ToString() ?? "—"}\n{project.Images} 个图像层 / {project.Particles} 个粒子层";
            Effects.Clear();
            generatedPreview = false; stale = false;
            if (project.Kind == "video")
            {
                var probe = await Renderer().ProbeAsync(project.Entry, cancellation.Token);
                sourceVideoDuration = probe.Duration;
                ProjectInfo = $"VIDEO · {probe.Width} × {probe.Height}\n源视频 {probe.Duration:0.###} 秒";
                WidthText = Math.Min(1920, Math.Max(16, probe.Width - probe.Width % 2)).ToString(CultureInfo.InvariantCulture);
                HeightText = Math.Clamp((int)Math.Round(double.Parse(WidthText, CultureInfo.InvariantCulture) * probe.Height / probe.Width / 2) * 2, 16, 8192).ToString(CultureInfo.InvariantCulture);
                SetMedia(project.Entry, false, $"源视频 {probe.Duration:0.###} 秒 · {probe.Width} × {probe.Height}", probe.Duration);
                Status = "素材已载入，可生成循环预览或导出。";
            }
            else
            {
                await LoadSceneAsync(project, cancellation.Token);
                LoadPoster(project.PreviewPath);
                ViewerInfo = PosterSource != null ? "素材封面 / 非合成预览" : "素材无可用封面";
                Status = "素材已载入。右侧开关和参数保存到当前配置；生成预览查看合成结果。";
            }
            string safeTitle = string.Concat(project.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim(' ', '.');
            OutputPath = Path.Combine(OutputDirectory, $"{(safeTitle.Length == 0 ? "wallpaper" : safeTitle)}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        }
        catch (OperationCanceledException) { project = null; Effects.Clear(); PosterSource = null; Status = "已取消载入。"; }
        catch (Exception e) { project = null; Effects.Clear(); PosterSource = null; Status = "无法载入：" + e.Message; }
        finally
        {
            if (project == null) { Title = "素材未载入"; ProjectInfo = "Scene / Video"; ViewerInfo = "画面监视器"; SelectedEffect = null; }
            Notify(nameof(ContentHint)); Notify(nameof(PreviewNotice)); Notify(nameof(EffectCountText));
            RecordingProgress = ExportProgress = 0; Notify(nameof(RecordingApplicable)); Notify(nameof(RecordingProgressText));
            cancellation.Dispose(); cancellation = null; Busy = false; UpdateValidation();
        }
    }

    private void LoadPoster(string? path)
    {
        if (path == null) return;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 1280;
            image.UriSource = new Uri(Path.GetFullPath(path));
            image.EndInit();
            image.Freeze();
            PosterSource = image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException or FormatException)
        { PosterSource = null; }
    }

    private void ChooseOutput()
    {
        var picker = new SaveFileDialog
        {
            Title = "设置输出文件",
            Filter = "MP4视频|*.mp4",
            DefaultExt = ".mp4",
            FileName = Path.GetFileName(OutputPath),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(OutputPath)) ? Path.GetDirectoryName(OutputPath) : Environment.CurrentDirectory
        };
        if (picker.ShowDialog() == true) OutputPath = picker.FileName;
    }
    public async Task RenderAsync(bool forPreview)
    {
        if (!CanRender || project == null || !TrySettings(out var settings, out _)) return;
        if (!forPreview && string.IsNullOrWhiteSpace(OutputPath)) { ChooseOutput(); if (string.IsNullOrWhiteSpace(OutputPath)) return; }
        cancellation = new(); Busy = true; Progress = RecordingProgress = ExportProgress = 0; IsRecording = false; IsPlaying = false; PlayRequested?.Invoke(false);
        var reporter = new Progress<RenderProgress>(p =>
        {
            if (!Busy) return;
            Status = p.Stage; Progress = p.Fraction * 100; IsRecording = p.Activity == RenderActivity.Capturing;
            if (p.Activity == RenderActivity.Capturing) RecordingProgress = p.StageFraction * 100;
            if (p.Activity == RenderActivity.Encoding) { if (RecordingApplicable) RecordingProgress = 100; ExportProgress = p.StageFraction * 90; }
            if (p.Activity == RenderActivity.Validating && exportProgress > 0) ExportProgress = 95;
            if (p.Activity == RenderActivity.Completed) { if (RecordingApplicable) RecordingProgress = 100; ExportProgress = 100; }
        });
        try
        {
            var renderer = Renderer();
            if (forPreview)
            {
                await ReleasePreviewAsync();
                preview = project.Kind == "scene"
                    ? await Task.Run(() => PreviewLease.RenderSceneAsync(SceneRenderer(), scenePlan!, sceneChoices!, Path.Combine(OutputDirectory, "csharp-previews"), SelectedQuality.Width, EngineSetting, reporter, EmbeddedRecording ? LiveMonitor : null, cancellation.Token))
                    : await Task.Run(() => PreviewLease.RenderAsync(renderer, project, settings, Path.Combine(OutputDirectory, "csharp-previews"), SelectedQuality.Width, reporter, cancellation.Token));
                SetMedia(preview.Artifact.Path, true, $"循环预览 · {preview.Artifact.Validation.Width} × {preview.Artifact.Validation.Height}", (double)settings.Frames / settings.Fps);
                Status = "预览已生成，可播放或拖动进度。";
            }
            else
            {
                var artifact = project.Kind == "scene"
                    ? await Task.Run(() => SceneRenderer().ExportAsync(scenePlan!, sceneChoices!, OutputPath, reporter, EmbeddedRecording ? LiveMonitor : null, cancellation.Token))
                    : await Task.Run(() => renderer.ExportAsync(project, OutputPath, settings, reporter, cancellation.Token));
                Status = "已保存：" + artifact.Path + (artifact.Validation.Warnings.Count > 0 ? " · " + string.Join("；", artifact.Validation.Warnings) : "");
            }
        }
        catch (OperationCanceledException) { Status = "已取消，临时文件已清理。"; }
        catch (Exception e) { Status = "任务失败：" + e.Message; }
        finally { IsRecording = false; cancellation.Dispose(); cancellation = null; Busy = false; }
    }
    private void SetMedia(string path, bool generated, string description, double duration)
    {
        CloseMedia?.Invoke(); mediaReady = false; generatedPreview = generated; stale = false; IsPlaying = false;
        PosterSource = null; NativePlaybackError = null; expectedMediaDuration = duration; MediaDuration = duration; scrubbing = false; scrubTimer.Stop(); awaitingSeek = null; ignorePlaybackUntil = DateTime.MinValue;
        MediaSource = new Uri(Path.GetFullPath(path)); ViewerInfo = description; UpdatePosition(0); Notify(nameof(PreviewNotice));
    }
    public void MediaOpened(double duration) { mediaReady = true; MediaDuration = expectedMediaDuration > 0 ? expectedMediaDuration : double.IsFinite(duration) ? Math.Max(0, duration) : 0; Notify(nameof(PreviewNotice)); Notify(nameof(CanSeek)); CommandManager.InvalidateRequerySuggested(); }
    public void MediaFailed(string message) { mediaReady = false; IsPlaying = false; Notify(nameof(CanSeek)); Status = "无法播放当前视频：" + message; }
    public int? NativePlaybackError { get; private set; }
    public void UseDecodedPlayback(int nativeError) { NativePlaybackError = nativeError; Status = "视频预览已就绪 · 已自动切换兼容播放。"; }
    public void UpdatePosition(double value)
    {
        if (scrubbing) return;
        if (awaitingSeek is double target && Math.Abs(value - target) > Math.Max(FrameStep * 2, .15) && DateTime.UtcNow < ignorePlaybackUntil) return;
        awaitingSeek = null;
        if (Set(ref position, BoundPosition(value), nameof(Position))) Notify(nameof(TimeText));
    }
    public bool PlaybackEnded() { if (scrubbing || awaitingSeek != null && DateTime.UtcNow < ignorePlaybackUntil) return false; awaitingSeek = null; ignorePlaybackUntil = DateTime.MinValue; UpdatePosition(0); return true; }
    public async Task ReleasePreviewAsync()
    {
        CloseMedia?.Invoke(); MediaSource = null; mediaReady = false; IsPlaying = false;
        generatedPreview = false; stale = true; Notify(nameof(PreviewNotice));
        expectedMediaDuration = 0; scrubbing = false; scrubTimer.Stop(); awaitingSeek = null; ignorePlaybackUntil = DateTime.MinValue; MediaDuration = 0; UpdatePosition(0);
        Notify(nameof(CanSeek));
        if (preview != null) { await preview.DisposeAsync(); preview = null; }
    }
    public void Cancel() => cancellation?.Cancel();
    private void OpenPath(string path)
    {
        try { if (Path.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); else Status = "输出文件尚未生成。"; }
        catch (Exception e) { Status = e.Message; }
    }
}
