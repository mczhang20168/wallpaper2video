using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wallpaper2Video.Core;
using Wallpaper2Video.Desktop;
using Wallpaper2Video.Runtime.Windows;
using static Program;

internal static class WorkspaceChecks
{
    private const string Workshop = @"E:\SteamLibrary\steamapps\workshop\content\431960";
    private static async Task WaitPlan(WorkspaceModel model)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        do { await Task.Delay(80); if (DateTime.UtcNow > until) throw new TimeoutException(model.Status); } while (model.Planning);
        Require(model.CurrentScenePlan != null && model.CanRender, "规划无效：" + model.Status);
    }
    public static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        await Check("cataloguesOnExistingWallpapers", async () =>
        {
            var rows = await Task.Run(() =>
            {
                var results = new List<object>();
                string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "workshop-ids.txt");
                foreach (string workshopId in File.ReadLines(fixture))
                {
                    string source = Path.Combine(Workshop, workshopId);
                    var project = ProjectLoader.Load(source); var plan = ScenePlanner.Build(project, new ExportSettings()); var catalog = SceneBrowser.Build(plan);
                    Require(catalog.Items.Select(i => i.Target.Id).Distinct().Count() == catalog.Items.Count, "分组产生重复项：" + source);
                    Require(plan.Targets().All(t => catalog.Items.Any(i => i.Target.Id == t.Id)), "分组丢失原有条目：" + source);
                    Require(catalog.Items.All(i => SceneBrowser.Categories.Contains(i.Category)), "存在未归组条目");
                    foreach (var obj in catalog.Objects)
                    {
                        var seen = new HashSet<int> { obj.Index }; var current = obj;
                        while (current.Parent is int parent) { Require(seen.Add(parent), "分组父子关系成环"); current = catalog.Objects.Single(o => o.Index == parent); }
                    }
                    foreach (var phase in plan.Alignment.Phases)
                        Require(plan.Cycles[phase.Id] == (phase.ReferenceKind == "natural" ? FrameMath.NearestCycles(20 / phase.Origin) : 1), "默认次数不符合20秒规则");
                    results.Add(new
                    {
                        source,
                        title = project.Title,
                        objects = catalog.Objects.Count,
                        parentLinks = catalog.Objects.Count(o => o.Parent != null),
                        items = catalog.Items.Count,
                        categories = catalog.Items.GroupBy(i => i.Category).ToDictionary(g => g.Key, g => g.Count()),
                        hierarchy = catalog.Objects.Where(o => catalog.Items.Any(i => i.ObjectIndex == o.Index)).Select(o => new { o.Index, o.Name, o.Parent, o.Path, o.Note }).ToArray(),
                        entries = catalog.Items.Select(i => new { id = i.Target.Id, i.Name, i.Category, i.ObjectIndex, i.EffectOrder }).ToArray()
                    });
                }
                return results;
            });
            File.WriteAllText(Path.Combine(output, "wallpaper-grouping-audit.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Audited {rows.Count} existing scenes without starting Wallpaper Engine.");
        });
        var window = new MainWindow(); var model = window.Model;
        string astronaut = Path.Combine(Workshop, "2815800031");
        await model.LoadAsync(astronaut);
        await Check("twentySecondsAndNaturalParticles", () =>
        {
            Require(model.DurationText == "20", "素材周期覆盖了默认总长");
            var plan = model.CurrentScenePlan!;
            Require(model.CurrentSceneSelection!.Particles.All(p => p.Mode == (plan.ParticleControls().Single(c => c.Id == p.Id).CanOriginal ? "original" : "off")), "粒子初始模式不是自然播放");
            Require(plan.Settings.AutomaticStartupWait, "没有默认自动启动等待");
            return Task.CompletedTask;
        });
        await Check("ctrlShiftSelectionAndBatchIsolation", () =>
        {
            var items = model.BrowserRows.Where(r => r.IsLeaf && r.Effect!.CanToggle).Take(3).ToArray(); Require(items.Length == 3, "样例条目不足");
            string[] keys = items.Select(i => i.Effect!.Key).ToArray();
            string before = JsonSerializer.Serialize(model.CurrentSceneSelection);
            model.SelectBrowserItem(keys[0], false, false); model.SelectBrowserItem(keys[1], true, false);
            Require(model.SelectedEffectKeys.Count == 2 && model.MultipleEffectsSelected, "Ctrl多选失败");
            Require(before == JsonSerializer.Serialize(model.CurrentSceneSelection), "选择条目改变了开关");
            model.SelectBrowserItem(keys[0], true, false); Require(model.SelectedEffectKeys.SetEquals([keys[1]]), "Ctrl取消选择失败");
            model.SelectBrowserItem(keys[0], false, false); model.SelectBrowserItem(keys[2], false, true);
            Require(keys.All(k => model.SelectedEffectKeys.Contains(k)), "Shift未连续选择");
            var untouched = model.Effects.Where(e => !model.SelectedEffectKeys.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Active);
            model.DisableSelectedCommand.Execute(null);
            Require(model.Effects.Where(e => model.SelectedEffectKeys.Contains(e.Key) && e.CanToggle).All(e => !e.Active), "批量关闭未生效");
            Require(model.Effects.Where(e => untouched.ContainsKey(e.Key)).All(e => e.Active == untouched[e.Key]), "批量操作影响未选择条目");
            model.UndoEffectsCommand.Execute(null); Require(before == JsonSerializer.Serialize(model.CurrentSceneSelection), "撤销没有恢复原开关");
            return Task.CompletedTask;
        });
        await Check("particleModesSurviveOffOn", () =>
        {
            string key = model.CurrentScenePlan!.ParticleControls().First(c => c.CanDrain).Id;
            model.SetEffectSelection([key]); model.SetParticle(key, "drain", 2, 8);
            model.DisableSelectedCommand.Execute(null); model.EnableSelectedCommand.Execute(null);
            var choice = model.CurrentSceneSelection!.Particles.Single(p => p.Id == key);
            Require(choice.Mode == "drain" && choice.Start == 2 && choice.Stop == 8, "批量重新启用丢失粒子模式或时段");
            model.SetParticle(key, "original"); model.ToggleEffect(key, false); model.ToggleEffect(key, true);
            Require(model.CurrentSceneSelection.Particles.Single(p => p.Id == key).Mode == "original", "自然播放被重新启用改成了自定义模式");
            return Task.CompletedTask;
        });
        await Check("automaticAndManualCycles", async () =>
        {
            var phase = model.CurrentScenePlan!.Alignment.Phases.First(p => p.ReferenceKind == "natural");
            model.DurationText = "24"; await WaitPlan(model);
            Require(model.CurrentScenePlan!.Cycles[phase.Id] == FrameMath.NearestCycles(24 / phase.Origin), "自动次数没有跟随总长");
            model.SetCycles(phase.Id, 7); await WaitPlan(model); model.DurationText = "30"; await WaitPlan(model);
            Require(model.CurrentScenePlan!.Cycles[phase.Id] == 7, "修改总长丢失手动次数");
            model.ResetCycles(phase.Id); await WaitPlan(model);
            Require(model.CurrentScenePlan!.Cycles[phase.Id] == FrameMath.NearestCycles(30 / phase.Origin), "恢复自动无效");
        });
        await Check("filteredSelectAllAndLockedItems", () =>
        {
            model.BrowserGrouping = "按类型"; model.SearchText = "粒子";
            model.CollapseGroupsCommand.Execute(null); model.SelectAllEffects();
            Require(model.SelectedEffectKeys.Count > 0, "折叠分组后全选结果为空");
            Require(model.Effects.Where(e => model.SelectedEffectKeys.Contains(e.Key)).All(e => (e.Name + " " + e.ObjectPath + " " + e.Category + " " + e.Blocked).Contains("粒子")), "全选越过当前筛选");
            model.SearchText = ""; model.ExpandGroupsCommand.Execute(null); model.SelectAllEffects(); model.EnableSelectedCommand.Execute(null);
            Require(model.Effects.Where(e => !e.CanToggle).All(e => !e.Active), "批量启用绕过锁定");
            model.SetEffectSelection([]); return Task.CompletedTask;
        });
        await Check("detachedWorkspaceRendering", async () =>
        {
            model.BrowserGrouping = "按类型"; model.SetEffectSelection([model.Effects.First(e => e.CanToggle && e.Kind == "effect").Key]);
            await Render(window, Path.Combine(output, "workspace-categories.png"));
            var surface = (FrameworkElement)window.Content!;
            var list = Descendants<EffectBrowserList>(surface).Single();
            Require(VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling, "列表没有采用像素滚动与回收虚拟化");
            var switches = Descendants<EffectCheckBox>(surface).Where(c => c.ActualWidth > 0).ToArray(); Require(switches.Length > 0 && switches.All(c => c.ActualWidth >= 32 && c.ActualHeight >= 32), "开关命中区域仍过小");
            Require(list.SelectedItems.Cast<BrowserEntry>().Any(r => r.Effect?.Key == model.SelectedEffect!.Key), "当前查看项没有选择高亮");
            var slider = Descendants<PlayerTimeline>(surface).Single(); Require(Math.Abs(slider.PositionAt(slider.ActualWidth / 2) - slider.Maximum / 2) < .01, "点击位置没有线性映射到时间");
            Require(!window.IsVisible && Application.Current.Windows.Cast<Window>().All(w => !w.IsVisible), "离屏排版显示了窗口");
            var obsolete = new[] { "启用所选", "关闭所选", "全选结果", "清空选择", "展开全部", "折叠全部", "撤销开关" };
            Require(Descendants<Button>(surface).All(b => !obsolete.Contains(b.Content?.ToString())), "面板仍常驻批量按钮墙");
            var viewer = Descendants<ScrollViewer>(list).First();
            Require(viewer.ScrollableWidth < 2, "浅层树不应出现多余的横向滚动条");
            list.ContextMenu!.PlacementTarget = list; list.ContextMenu.GetBindingExpression(FrameworkElement.DataContextProperty)!.UpdateTarget();
            Require(ReferenceEquals(list.ContextMenu.DataContext, model), "面板菜单没有连接当前编辑配置");
            Require(list.ContextMenu.Items.OfType<MenuItem>().Any(m => m.Header?.ToString() == "选择全部匹配项"), "面板菜单缺少全选入口");
        });
        await Check("visibilityColumnActsOnSelectedOnly", async () =>
        {
            model.SearchText = ""; model.BrowserGrouping = "按类型"; model.ExpandGroupsCommand.Execute(null);
            var keys = model.BrowserRows.Where(r => r.Effect?.CanToggle == true).Take(2).Select(r => r.Effect!.Key).ToArray();
            model.SetEffectSelection(keys); var before = model.Effects.ToDictionary(e => e.Key, e => e.Active);
            await Render(window, Path.Combine(output, "workspace-selection.png"));
            var surface = (FrameworkElement)window.Content;
            var check = Descendants<EffectCheckBox>(surface).First(c => c.ActualWidth > 0 && c.DataContext is EffectRow r && r.Key == keys[0]);
            bool target = !model.Effects.Single(e => e.Key == keys[0]).Active;
            typeof(ToggleButton).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(check, null);
            Require(model.Effects.Where(e => keys.Contains(e.Key)).All(e => e.Active == target), "点击所选行眼睛没有统一显隐");
            Require(model.Effects.Where(e => !keys.Contains(e.Key)).All(e => e.Active == before[e.Key]), "眼睛操作影响了选择之外的行");
            model.UndoEffectsCommand.Execute(null); model.SetEffectSelection([]);
        });
        await Check("treeDisclosureAndContextScope", () =>
        {
            model.BrowserGrouping = "按对象"; model.ExpandGroupsCommand.Execute(null);
            var parent = model.BrowserRows.First(r => r.HasChildren && r.Depth == 0);
            var before = model.CurrentSceneSelection;
            model.ToggleBrowserNode(parent.Key, true); Require(model.BrowserRows.Single(r => r.Key == parent.Key).Expanded == false, "折叠当前子树没有生效");
            Require(ReferenceEquals(before, model.CurrentSceneSelection), "展开折叠改变了效果开关");
            model.ToggleBrowserNode(parent.Key, true); Require(model.BrowserRows.Single(r => r.Key == parent.Key).Expanded, "递归展开没有恢复子树");
            parent = model.BrowserRows.Single(r => r.Key == parent.Key); model.SetBrowserContext(parent); model.SelectContextGroupCommand.Execute(null);
            Require(model.SelectedEffectKeys.SetEquals(parent.Members), "组内选择越过当前对象范围");
            model.SetEffectSelection([]); return Task.CompletedTask;
        });
        await model.LoadAsync(Path.Combine(Workshop, "3304630630"));
        await Check("objectHierarchyAndSearchPaths", async () =>
        {
            model.BrowserGrouping = "按对象"; model.ExpandGroupsCommand.Execute(null);
            Require(model.BrowserRows.Count(r => r.IsLeaf) == model.Effects.Count, "对象树丢失或重复条目");
            Require(model.BrowserRows.Any(r => r.Depth >= 5), "五层父子结构被扁平化");
            Require(model.BrowserRows.All(r => r.Indent.Left == r.Depth * 20), "深层对象缩进被截断");
            Require(model.BrowserRows.Where(r => r.Depth > 0).All(r => r.ParentKey != null), "树节点缺少可导航的父节点");
            var deep = model.BrowserRows.Where(r => r.IsLeaf).OrderByDescending(r => r.Depth).First();
            string path = deep.Effect!.ObjectPath; model.SearchText = deep.Effect.ShortName;
            Require(model.BrowserRows.Any(r => r.Effect?.Key == deep.Effect.Key), "搜索隐藏了命中叶子");
            Require(model.BrowserRows.Any(r => r.IsGroup && path.Contains(r.Label)), "搜索没有保留父对象路径");
            model.SearchText = ""; model.ExpandGroupsCommand.Execute(null);
            await Render(window, Path.Combine(output, "workspace-objects.png"));
            var tree = Descendants<EffectBrowserList>((FrameworkElement)window.Content).Single(); tree.ScrollIntoView(model.BrowserRows.Single(r => r.Key == deep.Key));
            await Render(window, Path.Combine(output, "workspace-deep-hierarchy.png"));
        });
        await model.LoadAsync(Path.Combine(Workshop, "3409595232"));
        await Check("modelParentStatePreservesChildren", () =>
        {
            var plan = model.CurrentScenePlan!; var catalog = SceneBrowser.Build(plan);
            var parent = model.Effects.First(e => e.Kind == "model" && e.CanToggle && catalog.Items.Any(i => i.Target.Kind == "effect" && i.ObjectIndex == e.Metadata!.ObjectIndex));
            var child = model.Effects.First(e => e.Kind == "effect" && e.CanToggle && e.Metadata!.ObjectIndex == parent.Metadata!.ObjectIndex);
            model.ApplyEffectStates([parent.Key, child.Key], true); Require(child.Effective, "启用模型与效果后未生效");
            model.ApplyEffectStates([parent.Key], false); Require(child.Active && !child.Effective && child.Ineffective, "关闭父层覆盖了子效果开关或未报告失效");
            model.ApplyEffectStates([parent.Key], true); Require(child.Active && child.Effective, "父层恢复后子效果没有恢复");
            return Task.CompletedTask;
        });
        await model.LoadAsync(Path.Combine(Workshop, "3299228616"));
        await Check("largeSceneScrollAndSelection", async () =>
        {
            model.BrowserGrouping = "按类型"; model.ExpandGroupsCommand.Execute(null);
            await Render(window, Path.Combine(output, "workspace-large-scene.png"));
            var surface = (FrameworkElement)window.Content; var list = Descendants<EffectBrowserList>(surface).Single();
            int realized = Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
            Require(model.Effects.Count > 250 && realized < model.BrowserRows.Count / 2, "大场景未按视口虚拟化");
            var scroll = Descendants<ScrollViewer>(list).First(); double extent = scroll.ExtentHeight;
            foreach (double offset in new[] { 500d, 1500d, 300d, 2000d, 0d })
            {
                scroll.ScrollToVerticalOffset(offset); surface.UpdateLayout(); await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                Require(double.IsFinite(scroll.VerticalOffset), "滚动偏移无效");
            }
            Require(model.SelectedEffectKeys.Count == 0, "滚动意外改变选择");
            model.SelectAllEffects(); Require(model.SelectedEffectKeys.Count == model.Effects.Count, "大场景全选漏项");
            model.DisableSelectedCommand.Execute(null); Require(model.Effects.Where(e => e.CanToggle).All(e => !e.Active), "大场景批量关闭失败");
            model.UndoEffectsCommand.Execute(null);
        });
        model.StopPlanning(); await model.ReleasePreviewAsync();
        await Check("backgroundChecksNeverShowWindows", () => { Require(Application.Current.Windows.Cast<Window>().All(w => !w.IsVisible), "后台测试显示了窗口"); return Task.CompletedTask; });
    }
    private static async Task Render(MainWindow window, string path)
    {
        var surface = (FrameworkElement)window.Content;
        System.Windows.Documents.TextElement.SetFontFamily(surface, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(surface, window.FontSize);
        surface.UseLayoutRounding = window.UseLayoutRounding; surface.SnapsToDevicePixels = window.SnapsToDevicePixels;
        TextOptions.SetTextFormattingMode(surface, TextFormattingMode.Display);
        ((Panel)surface).Background = window.Background;
        window.Content = null;
        try
        {
            if (!surface.Resources.MergedDictionaries.Contains(window.Resources)) surface.Resources.MergedDictionaries.Add(window.Resources);
            surface.DataContext = window.Model; surface.Width = 1440; surface.Height = 800;
            surface.Measure(new Size(1440, 800)); surface.Arrange(new Rect(0, 0, 1440, 800)); surface.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1440, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(path)) encoder.Save(file);
        }
        finally { window.Content = surface; }
    }
}
