using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Wallpaper2Video.Runtime.Windows;

namespace Wallpaper2Video.Desktop;

public sealed class SourceLayerRow(SourceLayerControl control, Action<int, bool> toggle) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public SourceLayerControl Control { get; private set; } = control;
    public int Index => Control.Index;
    public string Label => Control.Name;
    public string Caption => "原素材：" + Label;
    public string Icon => Control.Kind;
    public bool CanToggle => Control.CanToggle;
    public bool Active { get; private set; }
    public bool Ineffective { get; private set; }
    public int Depth { get; set; }
    public Thickness Indent => new(Depth * 20, 0, 0, 0);
    public double MinimumWidth => 180 + Depth * 20;
    public string GuideMask { get; set; } = "";
    public bool LastSibling { get; set; }
    public bool HasChildren { get; set; }
    public bool Expanded { get; set; } = true;
    public string Arrow => Expanded ? "▾" : "▸";
    public string Path { get; set; } = "";
    public string KindLabel => Control.Kind switch { "image" => "图像", "sound" => "声音", "particle" => "粒子", "model" => "模型", "text" => "文字", _ => "容器" };
    public string Hint => Path + "\n" + KindLabel + " · " + (!CanToggle ? Control.Reason : !Active ? "已隐藏" : Ineffective ? "父层或对应效果已关闭，未生效" : "已显示") + (Control.Source.Length > 0 ? "\n" + Control.Source : "");
    public void Toggle() { if (CanToggle) toggle(Index, !Active); }
    public void Refresh(SourceLayerControl value, bool active, bool ineffective)
    { Control = value; Active = active; Ineffective = ineffective; PropertyChanged?.Invoke(this, new("")); }
}

public sealed partial class WorkspaceModel
{
    private bool showingSources;
    private string sourceSearch = "";
    private readonly Dictionary<int, SourceLayerRow> sourceRows = [];
    private readonly HashSet<int> collapsedSources = [];
    private readonly HashSet<int> selectedSources = [];
    public ObservableCollection<SourceLayerRow> SourceRows { get; } = [];
    public bool ShowingSources
    {
        get => showingSources;
        set { if (!Set(ref showingSources, value)) return; Notify(nameof(ShowingEffects)); Notify(nameof(HasSelectedParticle)); Notify(nameof(HasPhases)); Notify(nameof(CanSetParticleWindow)); Notify(nameof(SelectionSummary)); if (value) UpdateSourceDetail(); else UpdateSceneDetail(); }
    }
    public bool ShowingEffects => !ShowingSources;
    public string SourceSearch { get => sourceSearch; set { if (Set(ref sourceSearch, value)) RebuildSourceRows(); } }
    public string SourceCountText => $"{SourceRows.Count} / {sourceRows.Count} 个原始对象 · Ctrl / Shift 多选";
    public ICommand ShowSourcesCommand => new WorkspaceCommand(() => ShowingSources = true);
    public ICommand ShowEffectsPanelCommand => new WorkspaceCommand(() => ShowingSources = false);
    public ICommand ShowSelectedSourcesCommand => new WorkspaceCommand(() => ApplySourceVisibility(selectedSources.ToArray(), true), () => CanEdit && selectedSources.Count > 0);
    public ICommand HideSelectedSourcesCommand => new WorkspaceCommand(() => ApplySourceVisibility(selectedSources.ToArray(), false), () => CanEdit && selectedSources.Count > 0);
    public void SelectSourceRows(IEnumerable<SourceLayerRow> rows)
    { selectedSources.Clear(); selectedSources.UnionWith(rows.Select(r => r.Index)); Notify(nameof(SelectionSummary)); if (ShowingSources) UpdateSourceDetail(); }
    public void ToggleSourceNode(int index)
    { if (!collapsedSources.Add(index)) collapsedSources.Remove(index); RebuildSourceRows(); }
    private void ToggleSourceVisibility(int index, bool enabled) => ApplySourceVisibility(selectedSources.Count > 1 && selectedSources.Contains(index) ? selectedSources.ToArray() : [index], enabled);
    public void ApplySourceVisibility(IEnumerable<int> indices, bool enabled)
    {
        if (scenePlan == null || sceneChoices == null || !CanEdit) return;
        var editable = indices.Distinct().Where(i => sourceRows.TryGetValue(i, out var row) && row.CanToggle).ToArray(); if (editable.Length == 0) return;
        var visibility = sceneChoices.SourceVisibility.ToDictionary(p => p.Key, p => p.Value); foreach (int index in editable) visibility[index] = enabled;
        var next = scenePlan.Select(sceneChoices.Enabled, sceneChoices.Particles, sceneChoices.DisabledEffects, visibility);
        undoStates.Push((sceneChoices, rememberedParticles.ToDictionary(p => p.Key, p => p.Value))); sceneChoices = next;
        SelectionChanged(); DescribeEffects(); RefreshBrowserStates(); RefreshSourceLayers();
        if (ShowingSources) UpdateSourceDetail(); else UpdateSceneDetail(); Status = $"已{(enabled ? "显示" : "隐藏")} {editable.Length} 个原素材对象，请重新生成预览。";
    }
    private void UpdateSourceDetail()
    {
        Detail = selectedSources.Count == 1 && sourceRows.TryGetValue(selectedSources.First(), out var row) ? row.Label + "\n\n" + row.Hint + "\n\n隐藏父对象时，其子对象及效果一并隐藏；再次显示后恢复各自设置。" : $"已选 {selectedSources.Count} 个原素材对象。\n点击名称查看来源；眼睛控制图像、文字、声音及对象容器的显隐。";
        Notify(nameof(SelectionSummary));
    }
    private void RefreshSourceLayers()
    {
        if (scenePlan == null || sceneChoices == null || browserCatalog == null) { sourceRows.Clear(); SourceRows.Clear(); selectedSources.Clear(); collapsedSources.Clear(); Notify(nameof(SourceCountText)); return; }
        var controls = scenePlan.SourceLayers(); var removed = scenePlan.RemovedLayers(sceneChoices);
        foreach (int old in sourceRows.Keys.Except(controls.Select(c => c.Index)).ToArray()) sourceRows.Remove(old);
        foreach (var control in controls)
        {
            if (!sourceRows.TryGetValue(control.Index, out var row)) sourceRows[control.Index] = row = new(control, ToggleSourceVisibility);
            bool particleShown = sceneChoices.Particles.Any(p => p.Id == "particle:" + control.Index && p.Mode != "off");
            bool active = sceneChoices.SourceVisibility.GetValueOrDefault(control.Index, control.DefaultVisible || particleShown);
            row.Path = browserCatalog.Objects.Single(o => o.Index == control.Index).Path;
            row.Refresh(control, active, active && removed.Contains(control.Index));
        }
        RebuildSourceRows();
        if (ShowingSources) UpdateSourceDetail();
    }
    private void RebuildSourceRows()
    {
        if (browserCatalog == null) return;
        var nodes = browserCatalog.Objects; var included = sourceRows.Values.Where(r => SourceSearch.Length == 0 || (r.Label + " " + r.Path + " " + r.Control.Source + " " + r.KindLabel).Contains(SourceSearch, StringComparison.CurrentCultureIgnoreCase)).Select(r => r.Index).ToHashSet();
        foreach (int index in included.ToArray()) for (var node = nodes.Single(n => n.Index == index); node.Parent is int parent; node = nodes.Single(n => n.Index == parent)) included.Add(parent);
        var output = new List<SourceLayerRow>();
        void Add(int? parent, int depth, string guides)
        {
            var children = nodes.Where(n => n.Parent == parent && included.Contains(n.Index)).ToArray();
            for (int i = 0; i < children.Length; i++)
            {
                var node = children[i]; var row = sourceRows[node.Index]; row.Depth = depth; row.GuideMask = guides; row.LastSibling = i == children.Length - 1;
                row.HasChildren = nodes.Any(n => n.Parent == node.Index && included.Contains(n.Index)); row.Expanded = SourceSearch.Length > 0 || !collapsedSources.Contains(node.Index); row.Refresh(row.Control, row.Active, row.Ineffective); output.Add(row);
                if (row.HasChildren && row.Expanded) Add(node.Index, depth + 1, guides + (row.LastSibling ? "0" : "1"));
            }
        }
        Add(null, 0, "");
        foreach (var old in SourceRows.Where(r => !output.Contains(r)).ToArray()) SourceRows.Remove(old);
        for (int i = 0; i < output.Count; i++) { int old = SourceRows.IndexOf(output[i]); if (old < 0) SourceRows.Insert(i, output[i]); else if (old != i) SourceRows.Move(old, i); }
        Notify(nameof(SourceCountText));
    }
}
