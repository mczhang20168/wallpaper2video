using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Wallpaper2Video.Runtime.Windows;

namespace Wallpaper2Video.Desktop;

public sealed class BrowserEntry(string key, string label, int depth, EffectRow? effect = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public int Depth { get; } = depth;
    public Thickness Indent => new(Depth * 20, 0, 0, 0);
    public double MinimumWidth => 164 + Depth * 20;
    public string? ParentKey { get; init; }
    public string GuideMask { get; init; } = "";
    public bool LastSibling { get; init; }
    public bool HasChildren { get; init; }
    public EffectRow? Effect { get; } = effect;
    public bool IsGroup => Effect == null;
    public bool IsLeaf => !IsGroup;
    public string Icon => Effect?.Kind switch { "particle" => "particle", "model" => "model", "script" => "script", "timeline" => "timeline", "camera" => "camera", null => "folder", _ => "effect" };
    public string[] Members { get; init; } = [];
    public bool Expanded { get; set; } = true;
    public string Arrow => Expanded ? "▾" : "▸";
    public string Summary { get; set; } = "";
    public string Hint { get; init; } = "";
    public ICommand ExpandCommand { get; set; } = null!;
    public ICommand SelectGroupCommand { get; set; } = null!;
    public void Refresh() { foreach (string property in new[] { nameof(Arrow), nameof(Summary) }) PropertyChanged?.Invoke(this, new(property)); }
}

public sealed partial class WorkspaceModel
{
    private SceneBrowser? browserCatalog;
    private readonly HashSet<string> collapsedGroups = [];
    private readonly HashSet<string> selectedKeys = [];
    private string grouping = "按对象", stateFilter = "全部状态";
    private string? rangeAnchor;
    private string? browserFocusKey;
    private BrowserEntry? browserContextEntry;
    private readonly Stack<(SceneSelection Selection, Dictionary<string, ParticleChoice> Remembered)> undoStates = [];
    public ObservableCollection<BrowserEntry> BrowserRows { get; } = [];
    public IReadOnlyList<string> BrowserGroupings { get; } = ["按对象", "按类型"];
    public IReadOnlyList<string> EffectStateFilters { get; } = ["全部状态", "已启用", "已关闭", "未生效", "已锁定"];
    public string BrowserGrouping { get => grouping; set { if (Set(ref grouping, value)) RebuildBrowser(); } }
    public string EffectStateFilter { get => stateFilter; set { if (Set(ref stateFilter, value)) RebuildBrowser(true); } }
    public IReadOnlySet<string> SelectedEffectKeys => selectedKeys;
    public string? BrowserFocusKey => browserFocusKey;
    public bool HasBrowserGroupContext => browserContextEntry?.HasChildren == true;
    public bool MultipleEffectsSelected => selectedKeys.Count > 1;
    public string SelectionSummary => ShowingSources ? $"{selectedSources.Count} 个原素材已选" : selectedKeys.Count == 0 ? "点击名称查看设置 · Ctrl / Shift 多选" : selectedKeys.Count == 1 ? "1 项已选 · 眼睛控制显隐" : $"{selectedKeys.Count} 项已选 · 点击所选行眼睛可批量显隐";
    public string BrowserCountText => $"当前筛选 {MatchingEffects().Count()} / {Effects.Count} 项";
    public event Action? BrowserChanged;
    public ICommand EnableSelectedCommand { get; private set; } = null!;
    public ICommand DisableSelectedCommand { get; private set; } = null!;
    public ICommand SelectAllEffectsCommand { get; private set; } = null!;
    public ICommand ClearEffectSelectionCommand { get; private set; } = null!;
    public ICommand UndoEffectsCommand { get; private set; } = null!;
    public ICommand ExpandGroupsCommand { get; private set; } = null!;
    public ICommand CollapseGroupsCommand { get; private set; } = null!;
    public ICommand ToggleVisibilityCommand { get; private set; } = null!;
    public ICommand SelectContextGroupCommand { get; private set; } = null!;
    public ICommand ShowContextGroupCommand { get; private set; } = null!;
    public ICommand HideContextGroupCommand { get; private set; } = null!;
    private void InitializeBrowser()
    {
        EnableSelectedCommand = new WorkspaceCommand(() => ApplyEffectStates(selectedKeys.ToArray(), true), () => CanEdit && selectedKeys.Any(k => Effects.Any(e => e.Key == k && e.CanToggle)));
        DisableSelectedCommand = new WorkspaceCommand(() => ApplyEffectStates(selectedKeys.ToArray(), false), () => CanEdit && selectedKeys.Any(k => Effects.Any(e => e.Key == k && e.CanToggle)));
        SelectAllEffectsCommand = new WorkspaceCommand(SelectAllEffects, () => CanEdit && Effects.Count > 0);
        ClearEffectSelectionCommand = new WorkspaceCommand(() => SetEffectSelection([]), () => selectedKeys.Count > 0);
        UndoEffectsCommand = new WorkspaceCommand(UndoEffects, () => CanEdit && undoStates.Count > 0);
        ExpandGroupsCommand = new WorkspaceCommand(() => { collapsedGroups.Clear(); RebuildBrowser(); });
        CollapseGroupsCommand = new WorkspaceCommand(() => { foreach (var row in BrowserRows.Where(r => r.HasChildren)) collapsedGroups.Add(row.Key); RebuildBrowser(); });
        ToggleVisibilityCommand = new WorkspaceCommand(() => ApplyEffectStates(selectedKeys.ToArray(), !Effects.Where(e => selectedKeys.Contains(e.Key) && e.CanToggle).All(e => e.Active)), () => CanEdit && Effects.Any(e => selectedKeys.Contains(e.Key) && e.CanToggle));
        SelectContextGroupCommand = new WorkspaceCommand(() => SetEffectSelection(browserContextEntry?.Members ?? []), () => HasBrowserGroupContext);
        ShowContextGroupCommand = new WorkspaceCommand(() => ApplyEffectStates(browserContextEntry?.Members ?? [], true), () => CanEdit && HasBrowserGroupContext);
        HideContextGroupCommand = new WorkspaceCommand(() => ApplyEffectStates(browserContextEntry?.Members ?? [], false), () => CanEdit && HasBrowserGroupContext);
    }
    private void ResetBrowser()
    {
        browserCatalog = null; selectedKeys.Clear(); collapsedGroups.Clear(); undoStates.Clear(); rangeAnchor = null; browserFocusKey = null; browserContextEntry = null;
        collapsedGroups.Add("category:" + SceneBrowser.Categories[8]); BrowserRows.Clear(); NotifySelection();
    }
    private IEnumerable<EffectRow> MatchingEffects() => Effects.Where(e =>
        (SearchText.Length == 0 || (e.Name + " " + e.ObjectPath + " " + e.Category + " " + e.Blocked).Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)) &&
        (EffectStateFilter switch { "已启用" => e.Active && e.CanToggle, "已关闭" => !e.Active && e.CanToggle, "未生效" => e.Active && e.Ineffective && e.CanToggle, "已锁定" => !e.CanToggle, _ => true }));
    private void DescribeEffects()
    {
        if (browserCatalog == null || scenePlan == null || sceneChoices == null) return;
        var removed = scenePlan.RemovedLayers(sceneChoices);
        var inactiveInputs = scenePlan.InactiveRenderTargetLayers(sceneChoices);
        foreach (var row in Effects)
        {
            var metadata = browserCatalog.Items.Single(i => i.Target.Id == row.Key);
            var obj = browserCatalog.Objects.FirstOrDefault(o => o.Index == metadata.ObjectIndex);
            bool hidden = false;
            if (row.Kind == "effect" && obj != null && scenePlan.Alignment.HiddenRenderTargetLayers.Contains(obj.Index))
                hidden = inactiveInputs.Contains(obj.Index);
            else
                for (var current = obj; current != null; current = browserCatalog.Objects.FirstOrDefault(o => o.Index == current.Parent))
                    if (removed.Contains(current.Index) || !sceneChoices.SourceVisibility.GetValueOrDefault(current.Index, current.SourceVisible) && !(current.Index == obj?.Index && row.Kind == "particle" && row.Active)) { hidden = true; break; }
            row.Describe(metadata, obj?.Path ?? "场景", hidden);
        }
    }
    private void RebuildBrowser(bool expandMatches = false)
    {
        var matches = MatchingEffects().ToArray(); var output = new List<BrowserEntry>();
        var objects = browserCatalog?.Objects.ToDictionary(o => o.Index) ?? [];
        var children = objects.Values.GroupBy(o => o.Parent ?? -1).ToDictionary(g => g.Key, g => g.ToArray());
        EffectRow[] Under(int index) => matches.Where(m =>
        {
            for (var obj = objects.GetValueOrDefault(m.Metadata?.ObjectIndex ?? -1); obj != null; obj = objects.GetValueOrDefault(obj.Parent ?? -1)) if (obj.Index == index) return true;
            return false;
        }).ToArray();
        BrowserEntry Node(string key, string label, int depth, EffectRow? effect, EffectRow[] members, string? parent, string mask, bool last, bool hasChildren, string hint)
        {
            if (expandMatches) collapsedGroups.Remove(key);
            var node = new BrowserEntry(key, label, depth, effect)
            {
                Members = members.Select(m => m.Key).ToArray(),
                ParentKey = parent,
                GuideMask = mask,
                LastSibling = last,
                HasChildren = hasChildren,
                Expanded = !collapsedGroups.Contains(key),
                Hint = hint,
                Summary = hasChildren ? $"{members.Count(m => m.Effective)}/{members.Length}" : ""
            };
            node.ExpandCommand = new WorkspaceCommand(() => ToggleBrowserNode(key, false), () => hasChildren);
            node.SelectGroupCommand = new WorkspaceCommand(() => SetEffectSelection(node.Members)); output.Add(node); return node;
        }
        string NextMask(string mask, bool last, int depth) => mask + (depth == 0 ? "" : last ? "0" : "1");
        void ObjectNode(BrowserObject obj, int depth, string? parent, string mask, bool last)
        {
            var members = Under(obj.Index); if (members.Length == 0) return;
            var direct = matches.Where(m => m.Metadata?.ObjectIndex == obj.Index).ToArray();
            var primary = direct.FirstOrDefault(m => m.Kind is "particle" or "model" && m.Metadata!.Target.Path.Length == 2);
            var own = direct.Where(m => m != primary).ToArray();
            var nested = (children.GetValueOrDefault(obj.Index) ?? []).Where(c => Under(c.Index).Length > 0).ToArray();
            string key = "object:" + obj.Index;
            var node = Node(key, obj.Name, depth, primary, members, parent, mask, last, own.Length + nested.Length > 0, obj.Path + (obj.Note.Length > 0 ? "\n" + obj.Note : "") + (primary == null ? "" : "\n" + primary.Hint));
            if (!node.Expanded) return;
            string next = NextMask(mask, last, depth); int count = own.Length + nested.Length; int position = 0;
            foreach (var row in own) Node("item:" + row.Key, row.Caption, depth + 1, row, [row], key, next, ++position == count, false, row.Hint);
            foreach (var child in nested) ObjectNode(child, depth + 1, key, next, ++position == count);
        }
        if (browserCatalog != null && BrowserGrouping == "按对象")
        {
            var roots = (children.GetValueOrDefault(-1) ?? []).Where(o => Under(o.Index).Length > 0).ToArray();
            var general = matches.Where(m => m.Metadata?.ObjectIndex == null).ToArray();
            for (int i = 0; i < roots.Length; i++) ObjectNode(roots[i], 0, null, "", i == roots.Length - 1 && general.Length == 0);
            if (general.Length > 0)
            {
                var group = Node("scene", "场景控制", 0, null, general, null, "", true, true, "不属于单个对象的参数");
                if (group.Expanded) for (int i = 0; i < general.Length; i++) Node("item:" + general[i].Key, general[i].Caption, 1, general[i], [general[i]], group.Key, "", i == general.Length - 1, false, general[i].Hint);
            }
        }
        else
        {
            var categories = SceneBrowser.Categories.Where(c => matches.Any(m => m.Category == c)).ToArray();
            for (int ci = 0; ci < categories.Length; ci++)
            {
                string category = categories[ci]; var members = matches.Where(m => m.Category == category).ToArray();
                var group = Node("category:" + category, category, 0, null, members, null, "", ci == categories.Length - 1, true, category);
                if (group.Expanded) for (int i = 0; i < members.Length; i++) Node("item:" + members[i].Key, members[i].Caption + "  ·  " + members[i].ObjectPath.Split('›').Last().Trim(), 1, members[i], [members[i]], group.Key, "", i == members.Length - 1, false, members[i].Hint);
            }
        }
        bool same = BrowserRows.Select(r => r.Key).SequenceEqual(output.Select(r => r.Key));
        if (same) for (int i = 0; i < output.Count; i++) { BrowserRows[i].Expanded = output[i].Expanded; BrowserRows[i].Summary = output[i].Summary; BrowserRows[i].Refresh(); }
        else { BrowserRows.Clear(); foreach (var row in output) BrowserRows.Add(row); }
        Notify(nameof(BrowserCountText)); BrowserChanged?.Invoke();
    }
    public void ToggleBrowserNode(string key, bool recursive)
    {
        var row = BrowserRows.FirstOrDefault(r => r.Key == key); if (row?.HasChildren != true) return;
        bool collapse = row.Expanded;
        if (recursive && key.StartsWith("object:") && int.TryParse(key[7..], out int root) && browserCatalog != null)
        {
            foreach (var obj in browserCatalog.Objects)
            {
                for (var current = obj; current != null; current = browserCatalog.Objects.FirstOrDefault(o => o.Index == current.Parent))
                    if (current.Index == root) { if (collapse) collapsedGroups.Add("object:" + obj.Index); else collapsedGroups.Remove("object:" + obj.Index); break; }
            }
        }
        else { if (collapse) collapsedGroups.Add(key); else collapsedGroups.Remove(key); }
        RebuildBrowser();
    }
    public void SetBrowserContext(BrowserEntry? entry) { browserContextEntry = entry; Notify(nameof(HasBrowserGroupContext)); }
    public void FocusBrowserEntry(BrowserEntry entry, bool control = false, bool shift = false)
    {
        if (entry.Effect != null) SelectBrowserItem(entry.Effect.Key, control, shift);
        else { SetEffectSelection([]); Detail = entry.Label + "\n\n" + entry.Hint + "\n\n" + entry.Members.Length + " 项效果与控制。"; }
        browserFocusKey = entry.Key; BrowserChanged?.Invoke();
    }
    private void ToggleRowVisibility(string key, bool enabled)
    {
        ApplyEffectStates(selectedKeys.Count > 1 && selectedKeys.Contains(key) ? selectedKeys.ToArray() : [key], enabled);
    }
    private void RefreshBrowserStates()
    {
        if (EffectStateFilter != "全部状态") { RebuildBrowser(); return; }
        foreach (var entry in BrowserRows.Where(r => r.HasChildren))
        { entry.Summary = $"{Effects.Count(e => entry.Members.Contains(e.Key) && e.Effective)}/{entry.Members.Length}"; entry.Refresh(); }
        NotifySelection();
    }
    public void SelectAllEffects() => SetEffectSelection(MatchingEffects().Select(e => e.Key));
    public void SetEffectSelection(IEnumerable<string> keys)
    {
        selectedKeys.Clear(); selectedKeys.UnionWith(keys.Where(k => Effects.Any(e => e.Key == k)));
        if (selectedKeys.Count == 0) { rangeAnchor = null; browserFocusKey = null; }
        else if (selectedKeys.Count == 1) browserFocusKey = BrowserRows.FirstOrDefault(r => r.Effect != null && selectedKeys.Contains(r.Effect.Key))?.Key;
        SelectedEffect = selectedKeys.Count == 1 ? Effects.Single(e => selectedKeys.Contains(e.Key)) : null;
        NotifySelection(); UpdateSceneDetail(); BrowserChanged?.Invoke();
    }
    public void SelectBrowserItem(string key, bool control, bool shift)
    {
        var visible = BrowserRows.Where(r => r.IsLeaf).Select(r => r.Effect!.Key).ToArray();
        if (shift && rangeAnchor != null && System.Array.IndexOf(visible, rangeAnchor) is int anchor && anchor >= 0)
        {
            int end = System.Array.IndexOf(visible, key); if (end < 0) return;
            var range = visible.Skip(Math.Min(anchor, end)).Take(Math.Abs(anchor - end) + 1);
            SetEffectSelection(control ? selectedKeys.Concat(range).ToArray() : range);
        }
        else
        {
            var next = control ? selectedKeys.ToHashSet() : [];
            if (!next.Add(key)) next.Remove(key);
            SetEffectSelection(next); rangeAnchor = key;
        }
    }
    private void NotifySelection() { Notify(nameof(SelectionSummary)); Notify(nameof(MultipleEffectsSelected)); Notify(nameof(HasSelectedParticle)); }
    public void ApplyEffectStates(IEnumerable<string> keys, bool enabled)
    {
        if (scenePlan == null || sceneChoices == null || !CanEdit) return;
        var rows = keys.Distinct().Select(k => Effects.FirstOrDefault(e => e.Key == k)).Where(e => e != null).Cast<EffectRow>().ToArray();
        var editable = rows.Where(e => e.CanToggle).ToArray(); if (editable.Length == 0) { Status = "所选项目均已锁定。"; return; }
        var selected = sceneChoices.Enabled.ToHashSet(); var disabled = sceneChoices.DisabledEffects.ToHashSet();
        var visibility = sceneChoices.SourceVisibility.ToDictionary(p => p.Key, p => p.Value);
        var remembered = rememberedParticles.ToDictionary(p => p.Key, p => p.Value);
        var particles = sceneChoices.Particles.ToDictionary(p => p.Id);
        foreach (var row in editable)
        {
            if (row.Kind == "particle")
            {
                if (!particles.TryGetValue(row.Key, out var previous)) { Status = "粒子清单已变化，请重新载入素材。"; return; }
                if (previous.Mode != "off") remembered[row.Key] = previous;
                var restored = previous.Mode != "off" ? previous : remembered.GetValueOrDefault(row.Key, new(row.Key, "original"));
                if (restored.Mode == "drain")
                {
                    var control = scenePlan.ParticleControls().Single(c => c.Id == row.Key);
                    restored = control.CanDrain ? restored with { Start = Math.Min(scenePlan.Duration, restored.Start ?? control.DefaultStart), Stop = Math.Min(scenePlan.Duration, restored.Stop ?? control.DefaultStop!.Value) } : new(row.Key, "original");
                }
                particles[row.Key] = enabled ? restored : new(row.Key, "off");
            }
            else if (row.Kind is "video" or "sprite") visibility[int.Parse(row.Key.Split(':')[1])] = enabled;
            else
            {
                if (scenePlan.Optional.Any(i => i.Id == row.Key)) { if (enabled) selected.Add(row.Key); else selected.Remove(row.Key); }
                if (row.Kind is "effect" or "bone" || row.Kind == "timeline" && scenePlan.Alignment.Phases.Any(p => p.Id == row.Key && p.Method == "timeline_retime")) { if (enabled) disabled.Remove(row.Key); else disabled.Add(row.Key); }
            }
        }
        try
        {
            var next = scenePlan.Select(selected, particles.Values, disabled, visibility);
            undoStates.Push((sceneChoices, rememberedParticles.ToDictionary(p => p.Key, p => p.Value)));
            sceneChoices = next; rememberedParticles.Clear(); foreach (var pair in remembered) rememberedParticles[pair.Key] = pair.Value;
            SelectionChanged(); foreach (var row in Effects) row.Refresh(row.CanToggle && Active(row.Key));
            DescribeEffects(); RefreshBrowserStates(); UpdateSceneDetail(); RefreshSourceLayers();
            int ineffective = editable.Count(e => e.Active && e.Ineffective);
            Status = $"已{(enabled ? "启用" : "关闭")} {editable.Length} 项；跳过 {rows.Length - editable.Length} 个锁定项" + (ineffective > 0 ? $"；{ineffective} 项在当前输出中未生效。" : "。");
        }
        catch (Exception error) { Status = error.Message; }
    }
    private void UndoEffects()
    {
        if (scenePlan == null || undoStates.Count == 0) return;
        var previous = undoStates.Pop();
        try
        {
            sceneChoices = scenePlan.Select(previous.Selection.Enabled, previous.Selection.Particles, previous.Selection.DisabledEffects, previous.Selection.SourceVisibility);
            rememberedParticles.Clear(); foreach (var pair in previous.Remembered) rememberedParticles[pair.Key] = pair.Value;
            SelectionChanged(); foreach (var row in Effects) row.Refresh(row.CanToggle && Active(row.Key));
            DescribeEffects(); RefreshBrowserStates(); RefreshSourceLayers(); if (ShowingSources) UpdateSourceDetail(); else UpdateSceneDetail(); Status = "已撤销显隐操作。";
        }
        catch (Exception error) { Status = "无法撤销到旧配置：" + error.Message; }
    }
}

/// <summary>A virtualized flat tree with explicit selection gestures. Group rows never become effect switches.</summary>
public sealed class EffectBrowserList : ListBox
{
    private WorkspaceModel? model;
    private bool syncing;
    public EffectBrowserList()
    {
        DataContextChanged += (_, _) =>
        {
            if (model != null) model.BrowserChanged -= Synchronize;
            model = DataContext as WorkspaceModel;
            if (model != null) model.BrowserChanged += Synchronize;
            Synchronize();
        };
    }
    private void Synchronize()
    {
        if (model == null || syncing) return; syncing = true;
        try
        {
            var desired = Items.OfType<BrowserEntry>().Where(e => e.Effect != null && model.SelectedEffectKeys.Contains(e.Effect.Key) || e.IsGroup && e.Key == model.BrowserFocusKey).ToArray();
            foreach (var old in SelectedItems.Cast<BrowserEntry>().Except(desired).ToArray()) SelectedItems.Remove(old);
            foreach (var entry in desired.Where(e => !SelectedItems.Contains(e))) SelectedItems.Add(entry);
        }
        finally { syncing = false; }
    }
    protected override void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e) { base.OnItemsChanged(e); Synchronize(); }
    private BrowserEntry? EntryAt(DependencyObject? source) => source != null && ItemsControl.ContainerFromElement(this, source) is ListBoxItem { DataContext: BrowserEntry entry } ? entry : null;
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        for (var current = source; current != null && current != this; current = current is Visual ? VisualTreeHelper.GetParent(current) : (current as FrameworkContentElement)?.Parent)
            if (current is ButtonBase) { base.OnPreviewMouseLeftButtonDown(e); return; }
        if (model != null)
        {
            Focus(); var entry = EntryAt(source);
            if (entry != null)
            {
                model.FocusBrowserEntry(entry, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                if (e.ClickCount == 2 && entry.HasChildren) model.ToggleBrowserNode(entry.Key, false);
            }
            else if (source is not ScrollBar && Keyboard.Modifiers == ModifierKeys.None) model.SetEffectSelection([]);
            e.Handled = true; return;
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }
    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        if (model != null)
        {
            var entry = EntryAt(e.OriginalSource as DependencyObject);
            if (entry != null && (entry.Effect == null || !model.SelectedEffectKeys.Contains(entry.Effect.Key))) model.FocusBrowserEntry(entry);
            model.SetBrowserContext(entry);
        }
        base.OnContextMenuOpening(e);
    }
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (!syncing && model != null && e.AddedItems.OfType<BrowserEntry>().Any(i => i.IsLeaf))
            model.SetEffectSelection(SelectedItems.OfType<BrowserEntry>().Where(i => i.IsLeaf).Select(i => i.Effect!.Key).ToArray());
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (model == null) { base.OnPreviewKeyDown(e); return; }
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.A && control) { model.SelectAllEffects(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { model.SetEffectSelection([]); e.Handled = true; return; }
        if (e.Key == Key.Z && control && model.UndoEffectsCommand.CanExecute(null)) { model.UndoEffectsCommand.Execute(null); e.Handled = true; return; }
        if (control && e.Key is Key.Add or Key.Subtract)
        {
            if (e.Key == Key.Add) model.ExpandGroupsCommand.Execute(null); else model.CollapseGroupsCommand.Execute(null); e.Handled = true; return;
        }
        if (e.Key == Key.Space && e.OriginalSource is not ButtonBase && model.ToggleVisibilityCommand.CanExecute(null))
        { model.ToggleVisibilityCommand.Execute(null); e.Handled = true; return; }
        var entries = Items.OfType<BrowserEntry>().ToArray();
        var current = entries.FirstOrDefault(r => r.Key == model.BrowserFocusKey) ?? entries.FirstOrDefault(r => r.Effect != null && model.SelectedEffectKeys.Contains(r.Effect.Key));
        if (e.Key is Key.Left or Key.Right && current != null)
        {
            if (e.Key == Key.Right)
            {
                if (current.HasChildren && !current.Expanded) model.ToggleBrowserNode(current.Key, false);
                else if (entries.FirstOrDefault(r => r.ParentKey == current.Key) is BrowserEntry child) { model.FocusBrowserEntry(child); ScrollIntoView(child); }
            }
            else if (current.HasChildren && current.Expanded) model.ToggleBrowserNode(current.Key, false);
            else if (entries.FirstOrDefault(r => r.Key == current.ParentKey) is BrowserEntry parent) { model.FocusBrowserEntry(parent); ScrollIntoView(parent); }
            e.Handled = true; return;
        }
        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End)
        {
            var candidates = shift ? entries.Where(r => r.IsLeaf).ToArray() : entries;
            int index = System.Array.FindIndex(candidates, r => r.Key == current?.Key);
            int next = e.Key == Key.Home ? 0 : e.Key == Key.End ? candidates.Length - 1 : index + (e.Key == Key.Up ? -1 : 1);
            if (candidates.Length > 0) { var entry = candidates[Math.Clamp(next, 0, candidates.Length - 1)]; model.FocusBrowserEntry(entry, false, shift); ScrollIntoView(entry); }
            e.Handled = true; return;
        }
        base.OnPreviewKeyDown(e);
    }
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        ScrollViewer? Find(DependencyObject node)
        {
            if (node is ScrollViewer viewer) return viewer;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) if (Find(VisualTreeHelper.GetChild(node, i)) is ScrollViewer match) return match;
            return null;
        }
        if (Find(this) is ScrollViewer scroll) { scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 120d * 48); e.Handled = true; }
        else base.OnPreviewMouseWheel(e);
    }
}
