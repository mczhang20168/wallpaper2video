using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Wallpaper2Video.Runtime.Windows;

namespace Wallpaper2Video.Desktop;

internal static class WorkspaceRegressionAcceptance
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }
    public static async Task<int> RunAsync(string source, string video, string directory)
    {
        Directory.CreateDirectory(directory);
        var results = new Dictionary<string, object?>();
        var window = new MainWindow { ShowActivated = false, Title = "Wallpaper2Video · 工作区问题回归" };
        window.Show();
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); results[name] = new { passed = true }; }
            catch (Exception error) { results[name] = new { passed = false, error = error.Message }; }
            await File.WriteAllTextAsync(Path.Combine(directory, "workspace-regressions.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        try
        {
            var model = window.Model;
            await model.LoadAsync(source);
            await Check("checkboxIsolation", async () =>
            {
                Require(model.CurrentScenePlan != null, "场景加载失败：" + model.Status);
                var list = Visuals<ListBox>(window).First();
                string[] keys = model.Effects.Where(e => e.Kind == "effect" && e.CanToggle).Take(2).Select(e => e.Key).ToArray();
                Require(keys.Length == 2, "缺少两个可勾选效果");
                for (int i = 0; i < 48; i++)
                {
                    string key = keys[i % 2];
                    var row = model.Effects.Single(e => e.Key == key);
                    var entry = model.BrowserRows.Single(r => r.Effect?.Key == key);
                    list.SelectedItem = entry; list.ScrollIntoView(entry); window.UpdateLayout();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    var check = Visuals<CheckBox>(list).FirstOrDefault(c => c.DataContext is EffectRow current && current.Key == key);
                    for (int retry = 0; check == null && retry < 10; retry++)
                    {
                        row = model.Effects.Single(e => e.Key == key); list.ScrollIntoView(model.BrowserRows.Single(r => r.Effect?.Key == key)); window.UpdateLayout();
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                        check = Visuals<CheckBox>(list).FirstOrDefault(c => c.DataContext is EffectRow current && current.Key == key);
                    }
                    Require(check != null, "没有实现目标勾选控件：" + key);
                    var before = model.Effects.ToDictionary(e => e.Key, e => e.Active);
                    // Execute the real mouse/keyboard click path, including its command.
                    if (i % 2 == 0) typeof(ToggleButton).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(check, null);
                    else ((IToggleProvider)new CheckBoxAutomationPeer(check!).GetPattern(PatternInterface.Toggle)).Toggle();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    var changed = model.Effects.Where(e => before.TryGetValue(e.Key, out bool old) && e.Active != old).Select(e => e.Key).ToArray();
                    Require(changed.SequenceEqual(new[] { key }), $"点击{key}却改变了：{string.Join(',', changed)}");
                    // A recycled container or binding refresh may update IsChecked; it must not edit any scene row.
                    var selected = model.CurrentSceneSelection;
                    check!.SetCurrentValue(ToggleButton.IsCheckedProperty, !check.IsChecked);
                    check.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateTarget();
                    Require(ReferenceEquals(selected, model.CurrentSceneSelection), "控件刷新误写了场景选择");
                    foreach (var visible in Visuals<CheckBox>(list))
                        if (visible.DataContext is EffectRow current) Require(visible.IsChecked == current.Active, "UI与编辑配置状态不一致：" + current.Key);
                    if (i % 8 == 0 && model.Phases.Count > 0) { model.SetCycles(model.Phases[0].Id, i / 8 + 2); await Task.Delay(330); }
                }
            });
            await Check("fixedCycleSliderRange", () =>
            {
                var phase = new PhaseRow(new("test", "test", 2, []), 1, 20, (_, _) => { });
                int maximum = phase.Maximum;
                phase.Cycles = 15;
                Require(phase.Maximum == maximum, $"拖动过程中量程从{maximum}变成{phase.Maximum}");
                var slider = new Slider { Minimum = 1, Maximum = phase.Maximum, DataContext = phase };
                slider.SetBinding(Slider.ValueProperty, new System.Windows.Data.Binding(nameof(PhaseRow.SliderCycles)) { Mode = System.Windows.Data.BindingMode.TwoWay });
                phase.CycleText = "1000";
                Require(phase.Cycles == 1000 && slider.Value == 100, "固定量程滑块不应截断手动输入的大次数");
                return Task.CompletedTask;
            });
            await Check("skeletalPeriods", () =>
            {
                var project = ProjectLoader.Load(source);
                var periods = AlignmentPlanner.Analyze(project, new AssetBundle(project));
                var skeleton = periods.Phases.Where(p => p.Method == "skeletal_rate").ToArray();
                Require(skeleton.Length == 3, $"Miku的3段骨骼循环仅提取到{skeleton.Length}段");
                return Task.CompletedTask;
            });
            await model.LoadAsync(video);
            model.DurationText = "4"; model.OverlapText = "1"; model.FpsText = "12"; model.WidthText = "640"; model.HeightText = "360";
            model.SelectedQuality = model.Qualities[0];
            await model.RenderAsync(true);
            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (!model.PlayCommand.CanExecute(null) && DateTime.UtcNow < until) await Task.Delay(50);
            await Check("previewDurationAndPositionBounds", () =>
            {
                Require(Math.Abs(model.MediaDuration - 4) < .001, $"4秒设置生成了{model.MediaDuration}秒预览：{model.Status}");
                model.Position = 9;
                Require(model.Position <= 4, $"播放位置{model.Position}超过4秒预览总长");
                model.UpdatePosition(8);
                Require(model.Position <= 4, "播放器反馈越过时长边界");
                return Task.CompletedTask;
            });
            await Check("scrubIgnoresPlaybackFeedback", async () =>
            {
                model.Position = 1;
                if (!model.IsPlaying && model.PlayCommand.CanExecute(null)) model.PlayCommand.Execute(null);
                var seek = Visuals<Slider>(window).Single(s => System.Windows.Automation.AutomationProperties.GetName(s) == "播放进度");
                seek.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                seek.Value = 3;
                model.UpdatePosition(.2);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                double dragged = seek.Value;
                seek.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Require(Math.Abs(dragged - 3) < .01, $"拖动到3秒被播放反馈拉回{dragged}秒");
                await Task.Delay(500);
                Require(model.IsPlaying, "拖动结束没有恢复播放");
            });
        }
        finally
        {
            modelCancel();
            while (window.Model.Busy) await Task.Delay(50);
            window.Close();
            while (window.IsVisible) await Task.Delay(20);
        }
        void modelCancel() { window.Model.Cancel(); window.Model.StopPlanning(); }
        return results.Values.All(v => JsonSerializer.SerializeToElement(v).GetProperty("passed").GetBoolean()) ? 0 : 1;
    }
}
