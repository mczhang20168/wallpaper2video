using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Desktop;

public sealed record ParticleModeOption(string Code, string Label, bool IsAvailable = true, string Hint = "")
{
    public override string ToString() => Label;
}

public sealed class EffectRow(SceneTarget target, bool active, Action<string, bool> changed) : INotifyPropertyChanged
{
    private SceneTarget target = target;
    private bool enabled = active;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Key => target.Id;
    public string Name => target.Label;
    public string Layer => target.Kind switch { "effect" => "图像效果", "particle" => "粒子层", "script" => "参数脚本", "timeline" => "时间线", "model" => "模型 / 实例", "camera" => "相机 / 风场", _ => target.Kind };
    public string File => Key;
    public string Kind => target.Kind;
    public string Blocked => target.Blocked;
    public bool CanToggle => Blocked.Length == 0;
    public bool Active => enabled;
    public BrowserItem? Metadata { get; private set; }
    public string ShortName => Metadata?.Name ?? Name;
    public string Category => Metadata?.Category ?? Layer;
    public string ObjectPath { get; private set; } = "";
    public bool Ineffective { get; private set; }
    public bool Effective => Active && CanToggle && !Ineffective;
    public string StateText => !CanToggle ? "已锁定" : !Active ? "已关闭" : Ineffective ? "未生效" : "已启用";
    public string StateColor => !CanToggle ? "#B0A7BA" : !Active ? "#B4BAC5" : Ineffective ? "#E9B778" : "#8CD9B2";
    public string Caption => (Metadata?.EffectOrder is int order ? order + ". " : "") + ShortName;
    public string Hint => ObjectPath + "\n" + Category + " · " + StateText + (Blocked.Length > 0 ? "\n" + Blocked : "");
    public void Describe(BrowserItem metadata, string path, bool ineffective)
    {
        Metadata = metadata; ObjectPath = path; Ineffective = ineffective;
        RaiseState();
    }
    private void RaiseState() { foreach (string p in new[] { nameof(Active), nameof(Effective), nameof(ShortName), nameof(Category), nameof(ObjectPath), nameof(Ineffective), nameof(StateText), nameof(StateColor), nameof(Caption), nameof(Hint) }) PropertyChanged?.Invoke(this, new(p)); }
    private ICommand? toggleCommand;
    public ICommand ToggleCommand => toggleCommand ??= new WorkspaceCommand(Toggle, () => CanToggle);
    public void Toggle() => changed(Key, !enabled);
    public void Refresh(bool value) { if (enabled == value) return; enabled = value; RaiseState(); }
    public void RefreshTarget(SceneTarget value) { target = value; foreach (string p in new[] { nameof(Name), nameof(Layer), nameof(Blocked), nameof(CanToggle) }) PropertyChanged?.Invoke(this, new(p)); }
}

public sealed class PhaseRow(PeriodPhase phase, int count, double duration, Action<string, int?> changed, Action<string>? reset = null, bool automatic = true) : INotifyPropertyChanged
{
    private PeriodPhase phase = phase;
    private double duration = duration;
    private int cycles = count;
    private string text = count.ToString(CultureInfo.InvariantCulture);
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id => phase.Id;
    public string Label => phase.Method == "timeline_retime" ? SceneBrowser.TimelineName(phase) : phase.Label;
    public bool Automatic { get; private set; } = automatic;
    public string ModeText => Automatic ? "自动 · 跟随视频总长" : "手动次数";
    public ICommand ResetCommand => new WorkspaceCommand(() => reset?.Invoke(Id), () => !Automatic && reset != null);
    public string Origin => phase.Method == "skeletal_rate" ? $"原始片段 {Number(phase.Settings?["clip_duration"], phase.Factor):0.######} 秒 · {Number(phase.Settings?["fps"]):0.###} fps\n原始速度 {Number(phase.Raw):0.######}× · 实际周期 {phase.Origin:0.######} 秒" : phase.Method == "timeline_retime" ? $"单程时长 {phase.Origin / (Text(phase.Settings?["mode"]) == "loop" ? 1 : 2):0.###} 秒 · {Text(phase.Settings?["mode"])}\n" + (phase.ReferenceKind == "natural" ? $"完整周期 {phase.Origin:0.###} 秒" : phase.Note) : phase.ReferenceKind == "natural" ? $"原始周期 {phase.Origin:0.######} 秒" : "合成周期 · " + phase.Note;
    public int Maximum => 100;
    // Numeric input remains unrestricted by the fixed 1–100 convenience slider.
    public int SliderCycles { get => Math.Clamp(cycles, 1, Maximum); set => Cycles = value; }
    public bool IsSkeletal => phase.Method is "skeletal_rate" or "embedded_video_rate";
    private string? requestedRate;
    private bool editingRate;
    public string RateText
    {
        get => requestedRate ?? (phase.Factor * cycles / duration).ToString("0.#########", CultureInfo.InvariantCulture);
        set
        {
            requestedRate = value;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) && double.IsFinite(rate) && rate > 0 && rate * duration / phase.Factor <= 1_000_000_000)
            {
                editingRate = true;
                try { CycleText = FrameMath.NearestCycles(rate * duration / phase.Factor).ToString(CultureInfo.InvariantCulture); }
                finally { editingRate = false; }
            }
            else { Valid = false; changed(Id, null); Raise(); }
        }
    }
    public bool Valid { get; private set; } = true;
    public int Cycles { get => cycles; set { if (cycles == value && Valid && requestedRate == null) return; CycleText = value.ToString(CultureInfo.InvariantCulture); } }
    public string CycleText
    {
        get => text;
        set
        {
            if (!editingRate) requestedRate = null;
            Automatic = false;
            text = value;
            Valid = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 1_000_000_000;
            if (Valid) cycles = n;
            Raise();
            changed(Id, Valid ? n : null);
        }
    }
    private void Raise() { foreach (string p in new[] { nameof(CycleText), nameof(Cycles), nameof(SliderCycles), nameof(PeriodText), nameof(Valid), nameof(RateText), nameof(ModeText), nameof(Automatic) }) PropertyChanged?.Invoke(this, new(p)); }
    public void Refresh(PeriodPhase value, int n, double length, bool automatic = true)
    {
        phase = value; duration = length; Automatic = automatic;
        if (Valid) { cycles = n; text = n.ToString(CultureInfo.InvariantCulture); }
        Raise(); PropertyChanged?.Invoke(this, new(nameof(Origin)));
    }
    public string PeriodText => Valid ? $"{cycles} 次 · 单次 {duration / cycles:0.######} 秒" + (IsSkeletal ? $"\n对齐速度 {Math.CopySign(phase.Factor * cycles / duration, Number(phase.Raw)):0.######}×（整次循环）" : "") : "次数须为1～1000000000；速度须为有限正数";
}

public sealed partial class WorkspaceModel
{
    private ScenePlan? scenePlan;
    private SceneSelection? sceneChoices;
    private readonly DispatcherTimer sceneTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private long sceneRevision;
    private bool planning, updatingDetail, editingParticleWindow;
    private string searchText = "", enginePath = "", warmupText = "4", sceneSummary = "", particleStartText = "", particleStopText = "", particleWarning = "";
    private string? plannedEngine;
    private bool particleInputValid = true;
    private bool particlePrewarmValid = true, preserveEntrance;
    private string particlePrewarmText = "";
    public bool PreserveEntrance
    {
        get => preserveEntrance;
        set
        {
            if (!Set(ref preserveEntrance, value)) return;

            Notify(nameof(PreviewActionText));
            Notify(nameof(PreviewNotice));
            MarkStale();
            ScheduleScenePlan();
        }
    }
    public string PreviewActionText => PreserveEntrance ? "生成普通预览" : "生成循环预览";
    public bool HasEntrance => scenePlan?.HasEntrance == true;
    public bool CanSetParticlePrewarm => HasSelectedParticle && SelectedParticleMode?.Code != "off" && scenePlan!.ParticleControls().Any(p => p.Id == SelectedEffect?.Key && p.CanDrain);
    public string ParticlePrewarmText
    {
        get => particlePrewarmText;
        set
        {
            if (!Set(ref particlePrewarmText, value) || updatingDetail) return;
            double seconds = 0;
            particlePrewarmValid = string.IsNullOrWhiteSpace(value) || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && double.IsFinite(seconds) && seconds is >= 0 and <= 120;
            if (particlePrewarmValid && SelectedEffect != null && CanSetParticlePrewarm)
            {
                var choice = sceneChoices!.Particles.Single(p => p.Id == SelectedEffect.Key);
                SetParticle(choice.Id, choice.Mode, choice.Start, choice.Stop, string.IsNullOrWhiteSpace(value) ? null : seconds, true);
            }
            else { Status = "粒子预热时间须在0～120秒之间。"; MarkStale(); }
            Notify(nameof(CanRender));
        }
    }
    private double RecommendedDuration => scenePlan != null && sceneChoices != null ? scenePlan.RecommendedDuration(sceneChoices) : 20;
    public string RecommendationText => $"推荐 {RecommendedDuration:0.###} 秒 · 视频优先，其次已启用主运动的最长可信周期";
    public ICommand ApplyRecommendationCommand => new WorkspaceCommand(() => DurationText = RecommendedDuration.ToString("R", CultureInfo.InvariantCulture), () => CanEdit && scenePlan != null);
    private bool embeddedRecording = true;
    private readonly Dictionary<string, (double Start, double Stop)> particleWindows = [];
    private readonly Dictionary<string, int> editedCycles = [];
    private readonly Dictionary<string, ParticleChoice> rememberedParticles = [];
    private ParticleModeOption? selectedParticleMode;
    public ObservableCollection<PhaseRow> Phases { get; } = [];
    public IReadOnlyList<ParticleModeOption> ParticleModes { get; private set; } = [new("original", "自然播放"), new("drain", "定时发射"), new("off", "关闭粒子")];
    public ILiveMonitor? LiveMonitor { get; set; }
    public ScenePlan? CurrentScenePlan => scenePlan;
    public SceneSelection? CurrentSceneSelection => sceneChoices;
    public bool Planning { get => planning; private set { if (Set(ref planning, value)) Notify(nameof(CanRender)); } }
    private bool SceneInputsValid => particleInputValid && particlePrewarmValid && Phases.All(p => p.Valid);
    private bool SceneReady => scenePlan != null && !Planning && SceneInputsValid && TrySettings(out var settings, out _) && scenePlan.Settings == settings && string.Equals(plannedEngine, EngineSetting, StringComparison.OrdinalIgnoreCase);
    public string SearchText { get => searchText; set { if (Set(ref searchText, value)) RebuildBrowser(true); } }
    public string EnginePath { get => enginePath; set { if (Set(ref enginePath, value)) { MarkStale(); ScheduleScenePlan(); } } }
    private string? EngineSetting => string.IsNullOrWhiteSpace(EnginePath) ? null : EnginePath;
    public string WarmupText { get => warmupText; set => Edit(ref warmupText, value, nameof(WarmupText)); }
    public bool EmbeddedRecording { get => embeddedRecording; set => Set(ref embeddedRecording, value); }
    private bool automaticStartupWait = true;
    public bool AutomaticStartupWait { get => automaticStartupWait; set { if (Set(ref automaticStartupWait, value)) { Notify(nameof(CustomStartupWait)); MarkStale(); ScheduleScenePlan(); } } }
    public bool CustomStartupWait => !AutomaticStartupWait;
    public string SceneSummary { get => sceneSummary; private set { if (Set(ref sceneSummary, value)) Notify(nameof(ContentHint)); } }
    public bool HasPhases => !ShowingSources && Phases.Count > 0;
    public string TimingAuditText => scenePlan == null ? "载入场景后提取时长。" : string.Join("\n\n", scenePlan.Alignment.Durations.Select(d => d.Label + "\n" + (d.RawSeconds is double seconds ? $"原始时长 {seconds:0.######} 秒" : "时长未知") + (d.EffectiveSeconds is double effective ? $" · 播放时长 {effective:0.######} 秒" : "") + "\n" + d.Reason));
    public bool HasSelectedParticle => !ShowingSources && !MultipleEffectsSelected && scenePlan?.ParticleControls().Any(p => p.Id == SelectedEffect?.Key) == true;
    public bool CanSetParticleWindow => HasSelectedParticle && SelectedParticleMode?.Code == "drain" && scenePlan!.ParticleControls().Any(p => p.Id == SelectedEffect?.Key && p.CanDrain);
    public double VideoDuration => TrySettings(out var settings, out _) ? (double)settings.Frames / settings.Fps : 20;
    public double FrameStep => TrySettings(out var settings, out _) ? 1d / settings.Fps : 1d / 30;
    public ParticleModeOption? SelectedParticleMode
    {
        get => selectedParticleMode;
        set
        {
            if (updatingDetail) { selectedParticleMode = value; Notify(); return; }
            if (value != null && SelectedEffect != null)
            {
                SetParticle(SelectedEffect.Key, value.Code);
                // A rejected choice must be republished after the binding setter completes.
                if (selectedParticleMode?.Code != value.Code) Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => Notify(nameof(SelectedParticleMode))));
            }
        }
    }
    public string ParticleStartText { get => particleStartText; set { if (Set(ref particleStartText, value) && !updatingDetail) UpdateParticleWindow(); } }
    public string ParticleStopText { get => particleStopText; set { if (Set(ref particleStopText, value) && !updatingDetail) UpdateParticleWindow(); } }
    public double ParticleStart { get => double.TryParse(ParticleStartText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : 0; set => ParticleStartText = value.ToString("0.#########", CultureInfo.InvariantCulture); }
    public double ParticleStop { get => double.TryParse(ParticleStopText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : 0; set => ParticleStopText = value.ToString("0.#########", CultureInfo.InvariantCulture); }
    public string ParticleWarning { get => particleWarning; private set => Set(ref particleWarning, value); }
    private void InitializeSceneWorkspace()
    {
        InitializeBrowser();
        sceneTimer.Tick += async (_, _) => { sceneTimer.Stop(); await RebuildSceneAsync(); };
    }
    private SceneRenderer SceneRenderer() => new(string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath, EngineSetting);
    private void ResetSceneWorkspace()
    {
        sceneTimer.Stop(); sceneRevision++; scenePlan = null; sceneChoices = null; Planning = false;
        preserveEntrance = false; particlePrewarmValid = true; particlePrewarmText = ""; Notify(nameof(PreserveEntrance)); Notify(nameof(PreviewActionText));
        editedCycles.Clear(); particleWindows.Clear(); rememberedParticles.Clear(); Phases.Clear(); ResetBrowser(); particleInputValid = true; SceneSummary = "";
        RefreshSourceLayers();
        Notify(nameof(HasPhases)); Notify(nameof(HasSelectedParticle)); Notify(nameof(TimingAuditText));
    }
    private void ScheduleScenePlan()
    {
        Notify(nameof(VideoDuration));
        Notify(nameof(FrameStep));
        if (Busy || project?.Kind != "scene") return;
        sceneRevision++; Planning = true; sceneTimer.Stop(); sceneTimer.Start();
    }
    private async Task LoadSceneAsync(ProjectSnapshot source, CancellationToken cancellation)
    {
        if (source.Kind == "web") throw new NotSupportedException("Web 壁纸脚本尚不能可靠执行鼠标与日期时间筛选，暂不支持导出");
        if (source.Width is int w && source.Height is int h && w > 0 && h > 0)
        {
            int width = Math.Clamp(Math.Min(1920, w) / 2 * 2, 16, 8192);
            WidthText = width.ToString(CultureInfo.InvariantCulture);
            HeightText = Math.Clamp((int)Math.Round((double)width * h / w / 2) * 2, 16, 8192).ToString(CultureInfo.InvariantCulture);
        }
        var bundle = new AssetBundle(source, EngineSetting);
        var alignment = await Task.Run(() => AlignmentPlanner.Analyze(source, bundle), cancellation);
        if (alignment.DefaultVideoDuration is double videoDuration)
        {
            int fps = int.TryParse(FpsText, out int currentFps) && currentFps is >= 1 and <= 120 ? currentFps : 30;
            double duration = Math.Max(videoDuration, 4d / fps); DurationText = duration.ToString("R", CultureInfo.InvariantCulture);
            long frames = FrameMath.Quantize(duration, fps);
            double overlap = double.TryParse(OverlapText, NumberStyles.Float, CultureInfo.InvariantCulture, out double configuredOverlap) ? configuredOverlap : 2;
            if (FrameMath.Quantize(overlap, fps) > frames / 2) OverlapText = ((frames / 2) / (double)fps).ToString("R", CultureInfo.InvariantCulture);
            ProjectInfo += $"\n内嵌视频 {videoDuration:0.###} 秒 · 默认总长来源";
        }
        if (!TrySettings(out var settings, out string error)) throw new ArgumentException(error);
        var plan = await Task.Run(() => ScenePlanner.Build(source, settings, EngineSetting, bundle: bundle, alignment: alignment), cancellation);
        cancellation.ThrowIfCancellationRequested();
        scenePlan = plan; sceneChoices = plan.Select();
        if (alignment.DefaultVideoDuration == null && Math.Abs(RecommendedDuration - settings.Duration) > 1d / settings.Fps)
        {
            DurationText = RecommendedDuration.ToString("R", CultureInfo.InvariantCulture);
            if (!TrySettings(out settings, out error)) throw new ArgumentException(error);
            scenePlan = await Task.Run(() => ScenePlanner.Build(source, settings, EngineSetting, bundle: bundle, alignment: alignment), cancellation);
            sceneChoices = scenePlan.Select();
        }
        plannedEngine = EngineSetting;
        RefreshSceneRows();
    }
    private async Task RebuildSceneAsync()
    {
        long revision = sceneRevision;
        var source = project;
        if (source?.Kind != "scene") { Planning = false; return; }
        if (!TrySettings(out var settings, out _) || !SceneInputsValid) { Planning = false; Notify(nameof(CanRender)); return; }
        var previous = sceneChoices;
        var counts = editedCycles.ToDictionary(p => p.Key, p => p.Value);
        string? engine = EngineSetting;
        try
        {
            var plan = await Task.Run(() =>
            {
                var bundle = new AssetBundle(source, engine);
                var alignment = AlignmentPlanner.Analyze(source, bundle);
                var defaults = alignment.Choose((double)settings.Frames / settings.Fps);
                foreach (string key in defaults.Keys.ToArray()) if (counts.TryGetValue(key, out int count)) defaults[key] = count;
                return ScenePlanner.Build(source, settings, engine, defaults, bundle, alignment);
            });
            if (revision != sceneRevision || project != source) return;
            var enabled = previous?.Enabled.Intersect(plan.Optional.Where(i => i.CanEnable).Select(i => i.Id)).ToHashSet() ?? [];
            var controls = plan.ParticleControls();
            var particles = new List<ParticleChoice>();
            foreach (var choice in previous?.Particles ?? [])
            {
                var control = controls.FirstOrDefault(c => c.Id == choice.Id);
                if (control == null) continue;
                particles.Add(choice.Mode == "drain" && control.CanDrain ? choice with { Start = Math.Min(plan.Duration, choice.Start!.Value), Stop = Math.Min(plan.Duration, choice.Stop!.Value) }
                    : choice.Mode == "original" && control.CanOriginal ? choice : new(choice.Id, "off"));
            }
            scenePlan = plan; sceneChoices = plan.Select(enabled, particles, previous?.DisabledEffects, previous?.SourceVisibility);
            undoStates.Clear();
            plannedEngine = engine;
            foreach (string key in editedCycles.Keys.Except(plan.Cycles.Keys).ToArray()) editedCycles.Remove(key);
            RefreshSceneRows(); Status = "当前效果配置已更新，请生成预览查看结果。";
        }
        catch (Exception error) { if (revision == sceneRevision) { scenePlan = null; Status = "场景配置无效：" + error.Message; } }
        finally { if (revision == sceneRevision) { Planning = false; Notify(nameof(CanRender)); } }
    }
    private bool Active(string key)
    {
        if (sceneChoices == null || scenePlan == null) return false;
        var particle = sceneChoices.Particles.FirstOrDefault(p => p.Id == key);
        if (particle != null) return particle.Mode != "off";
        if (sceneChoices.DisabledEffects.Contains(key)) return false;
        if (key.StartsWith("video:", StringComparison.Ordinal) || key.StartsWith("sprite:", StringComparison.Ordinal))
            return sceneChoices.SourceVisibility.GetValueOrDefault(int.Parse(key.Split(':')[1]), true);
        return scenePlan.Optional.Any(i => i.Id == key) ? sceneChoices.Enabled.Contains(key) : true;
    }
    private void RefreshSceneRows()
    {
        string? selected = SelectedEffect?.Key;
        browserCatalog = scenePlan == null ? null : SceneBrowser.Build(scenePlan);
        var targets = browserCatalog?.Items.Select(i => i.Target).ToArray() ?? [];
        foreach (var old in Effects.Where(e => targets.All(t => t.Id != e.Key)).ToArray()) Effects.Remove(old);
        for (int i = 0; i < targets.Length; i++)
        {
            var target = targets[i]; var row = Effects.FirstOrDefault(e => e.Key == target.Id);
            if (row == null) { row = new(target, target.Blocked.Length == 0 && Active(target.Id), ToggleRowVisibility); Effects.Insert(i, row); }
            else { row.RefreshTarget(target); row.Refresh(target.Blocked.Length == 0 && Active(target.Id)); if (Effects.IndexOf(row) != i) Effects.Move(Effects.IndexOf(row), i); }
        }
        SelectedEffect = Effects.FirstOrDefault(e => e.Key == selected);
        if (scenePlan != null) SceneSummary = $"粒子自然播放，原素材隐藏项保持关闭 · {scenePlan.Required.Count} 项自动排除。点击名称查看设置。";
        DescribeEffects(); RebuildBrowser();
        RefreshSourceLayers();
        selectedKeys.IntersectWith(Effects.Select(e => e.Key)); NotifySelection();
        Notify(nameof(EffectCountText));
        Notify(nameof(TimingAuditText));
    }
    public void ToggleEffect(string key, bool enabled)
    {
        ApplyEffectStates([key], enabled);
    }
    public void SetCycles(string key, int cycles)
    {
        if (cycles is < 1 or > 1_000_000_000 || scenePlan?.Alignment.Phases.All(p => p.Id != key) != false) throw new ArgumentException("循环次数或子时钟无效");
        editedCycles[key] = cycles; MarkStale(); ScheduleScenePlan();
    }
    public void SetParticle(string key, string mode, double? start = null, double? stop = null, double? prewarm = null, bool changePrewarm = false)
    {
        if (scenePlan == null || sceneChoices == null) return;
        try
        {
            if (mode == "drain" && start == null && stop == null && particleWindows.TryGetValue(key, out var window))
            { start = Math.Min(scenePlan.Duration, window.Start); stop = Math.Min(scenePlan.Duration, window.Stop); }
            double? warm = changePrewarm ? prewarm : prewarm ?? sceneChoices.Particles.FirstOrDefault(p => p.Id == key)?.Prewarm;
            var choice = new ParticleChoice(key, mode, mode == "drain" ? start : null, mode == "drain" ? stop : null, warm);
            var particles = sceneChoices.Particles.Select(p => p.Id == key ? choice : p).ToList();
            sceneChoices = scenePlan.Select(sceneChoices.Enabled, particles, sceneChoices.DisabledEffects, sceneChoices.SourceVisibility);
            undoStates.Clear();
            var resolved = sceneChoices.Particles.Single(p => p.Id == key);
            if (resolved.Mode != "off") rememberedParticles[key] = resolved;
            if (resolved.Mode == "drain") particleWindows[key] = (resolved.Start!.Value, resolved.Stop!.Value);
            SelectionChanged(); foreach (var row in Effects) row.Refresh(row.CanToggle && Active(row.Key));
            DescribeEffects(); RefreshBrowserStates();
            if (editingParticleWindow) UpdateParticleWarning(); else UpdateSceneDetail();
            RefreshSourceLayers();
        }
        catch (Exception error) { Status = error.Message; UpdateSceneDetail(); }
    }
    private void UpdateParticleWindow()
    {
        if (SelectedEffect == null || !CanSetParticleWindow) return;
        particleInputValid = double.TryParse(ParticleStartText, NumberStyles.Float, CultureInfo.InvariantCulture, out double start) && double.IsFinite(start) && start >= 0 && start <= VideoDuration &&
            double.TryParse(ParticleStopText, NumberStyles.Float, CultureInfo.InvariantCulture, out double stop) && double.IsFinite(stop) && stop >= 0 && stop <= VideoDuration;
        if (particleInputValid)
        {
            editingParticleWindow = true;
            try { SetParticle(SelectedEffect.Key, "drain", start, double.Parse(ParticleStopText, CultureInfo.InvariantCulture)); }
            finally { editingParticleWindow = false; }
        }
        else { StopPlanning(); ParticleWarning = $"开始与停止时间均须在0～{VideoDuration:0.###}秒之间"; MarkStale(); }
        Notify(nameof(ParticleStart)); Notify(nameof(ParticleStop)); Notify(nameof(CanRender));
    }
    private void UpdateParticleWarning()
    {
        if (scenePlan == null || sceneChoices == null || SelectedEffect == null) return;
        var control = scenePlan.ParticleControls().FirstOrDefault(c => c.Id == SelectedEffect.Key);
        if (control == null) return;
        var configured = scenePlan.ParticlePlan.Configure(sceneChoices.Particles).FirstOrDefault(r => Number(r["index"]) == control.Index);
        ParticleWarning = ParticleDescription(control, configured);
    }
    public void StopPlanning() { sceneTimer.Stop(); sceneRevision++; Planning = false; }
    private void UpdateSceneDetail()
    {
        updatingDetail = true;
        try
        {
            particleInputValid = true;
            particlePrewarmValid = true;
            var row = SelectedEffect;
            if (MultipleEffectsSelected) { Phases.Clear(); Detail = "点击所选行的眼睛统一显隐，或右键分别显示、隐藏。单击名称返回单项属性。"; return; }
            if (row == null || scenePlan == null || sceneChoices == null) { Phases.Clear(); Detail = "选择右侧条目，调整周期或粒子时段。"; return; }
            var target = row.Metadata?.Target ?? scenePlan.Targets().Single(t => t.Id == row.Key);
            var optional = scenePlan.Optional.FirstOrDefault(i => i.Id == row.Key);
            var motion = scenePlan.Alignment.Phases.FirstOrDefault(p => p.Id == row.Key && p.Method == "timeline_retime");
            int layer = target.Path.Length > 1 && target.Path[0] == "objects" ? int.Parse(target.Path[1], CultureInfo.InvariantCulture) : -1;
            bool ineffective = scenePlan.RemovedLayers(sceneChoices).Contains(layer);
            bool unusedInput = scenePlan.InactiveRenderTargetLayers(sceneChoices).Contains(layer);
            Detail = row.Name + "\n\n" + (row.Blocked.Length > 0 ? "已锁定：" + row.Blocked : optional?.Reason ?? (row.Kind == "particle" ? "保留原引擎粒子模拟，可自然播放或指定发射时段。" : "可确认的周期效果或静态滤镜。")) +
                (ineffective ? "\n\n该图层或其父层已关闭，本次输出中不生效。" : unusedInput ? "\n\n当前启用的效果没有引用此渲染输入。" : "");
            var unaligned = scenePlan.Alignment.Durations.Where(d => !d.Aligned && Prefix(target.Path, d.Path)).ToArray();
            foreach (var source in unaligned) Detail += $"\n\n{source.Label}：" + (source.RawSeconds is double seconds ? $"原始时长{seconds:0.######}秒 · " : "时长未知 · ") + source.Reason;
            if (motion != null) Detail = row.Name + "\n\n" + motion.Note + "\n\n次数控制该运动在视频内完成的完整循环；关闭只冻结此运动，原素材仍显示。" + (Array(motion.Settings?["combined_paths"]).Count > 0 ? "组合属性共用同一次数，不单独拆分。" : "") + (ineffective ? "\n\n该图层或其父层已关闭，本次输出中不生效。" : "");
            var phases = scenePlan.Alignment.Phases.Where(p => row.Kind is "bone" or "video" or "sprite" ? p.Id == row.Key :
                Prefix(target.Path, p.EffectPath) && (row.Kind != "model" || p.Method == "skeletal_rate")).ToArray();
            foreach (var old in Phases.Where(p => phases.All(v => v.Id != p.Id)).ToArray()) Phases.Remove(old);
            foreach (var phase in phases)
            {
                int cycles = editedCycles.GetValueOrDefault(phase.Id, scenePlan.Cycles[phase.Id]);
                var existing = Phases.FirstOrDefault(p => p.Id == phase.Id);
                if (existing != null) { existing.Refresh(phase, cycles, scenePlan.Duration, !editedCycles.ContainsKey(phase.Id)); continue; }
                Phases.Add(new(phase, cycles, scenePlan.Duration, (key, count) =>
                {
                    if (count.HasValue) SetCycles(key, count.Value); else { StopPlanning(); MarkStale(); Notify(nameof(CanRender)); }
                }, ResetCycles, !editedCycles.ContainsKey(phase.Id)));
            }
            var control = scenePlan.ParticleControls().FirstOrDefault(c => c.Id == row.Key);
            if (control != null)
            {
                var choice = sceneChoices.Particles.Single(c => c.Id == row.Key);
                particlePrewarmText = choice.Prewarm?.ToString("0.#########", CultureInfo.InvariantCulture) ?? "";
                ParticleModeOption[] modes = [new("original", "自然播放"), new("drain", "定时发射", control.CanDrain, control.CanDrain ? "" : control.Reason), new("off", "关闭粒子")];
                if (!ParticleModes.SequenceEqual(modes)) { ParticleModes = modes; Notify(nameof(ParticleModes)); }
                selectedParticleMode = ParticleModes.Single(m => m.Code == choice.Mode);
                var remembered = particleWindows.GetValueOrDefault(row.Key, (Start: control.DefaultStart, Stop: control.DefaultStop ?? 0));
                particleStartText = (choice.Start ?? Math.Min(VideoDuration, remembered.Start)).ToString("0.#########", CultureInfo.InvariantCulture);
                particleStopText = (choice.Stop ?? Math.Min(VideoDuration, remembered.Stop)).ToString("0.#########", CultureInfo.InvariantCulture);
                string sourceId = Text(At(scenePlan.Scene, target.Path)?["id"]);
                var configured = scenePlan.ParticlePlan.Configure(sceneChoices.Particles).FirstOrDefault(r => Text(r["id"]) == sourceId);
                ParticleWarning = ParticleDescription(control, configured);
            }
        }
        finally
        {
            updatingDetail = false;
            Notify(nameof(ParticlePrewarmText)); Notify(nameof(CanSetParticlePrewarm)); Notify(nameof(HasEntrance)); Notify(nameof(RecommendationText));
            foreach (string name in new[] { nameof(HasPhases), nameof(HasSelectedParticle), nameof(CanSetParticleWindow), nameof(SelectedParticleMode), nameof(ParticleStartText), nameof(ParticleStopText), nameof(ParticleStart), nameof(ParticleStop), nameof(VideoDuration), nameof(CanRender) }) Notify(name);
        }
    }
    private void SelectionChanged()
    {
        // A planning task may have captured an older selection. Invalidate it before it can overwrite this edit.
        sceneRevision++; MarkStale();
        Notify(nameof(RecommendationText));
        if (Planning || TrySettings(out var settings, out _) && scenePlan?.Settings != settings || !string.Equals(plannedEngine, EngineSetting, StringComparison.OrdinalIgnoreCase)) ScheduleScenePlan();
    }
    public void ResetCycles(string key) { editedCycles.Remove(key); MarkStale(); ScheduleScenePlan(); }
    private string ParticleDescription(ParticleControl control, System.Text.Json.Nodes.JsonObject? configured)
    {
        if (SelectedParticleMode?.Code == "original") return !control.CanDrain ? "仅支持自然播放；暂不能定时发射：" + control.Reason + "。\n音频响应已关闭。" : "音频响应已关闭，按固定速率随机发射。自然播放不使用起止时间；如需调整，请切换为“定时发射”。首尾不保证留空。";
        if (SelectedParticleMode?.Code == "off") return "本次输出关闭该粒子层；重新显示会恢复此前模式和时段。";
        if (!control.CanDrain) return control.Reason;
        double latest = Number(configured?["suggested_latest_stop"], double.NaN);
        string hint = latest >= 0 ? $"建议不晚于 {latest:0.###} 秒停发。" : "当前总长不足以等待全部粒子消亡，首尾可能残留粒子。";
        return "音频响应已关闭，按固定速率随机发射。\n停发后已有粒子继续自然消亡。\n" + $"消亡等待上界 {control.DrainSeconds:0.###} 秒；" + hint + "\n" + Text(configured?["emission_warning"]);
    }
}
