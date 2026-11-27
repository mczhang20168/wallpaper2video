using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Wallpaper2Video.Desktop;
using static Program;

internal static class ParticleEditingChecks
{
    internal static async Task RunAsync(string output, string? source = null)
    {
        var window = new MainWindow(); var model = window.Model;
        await model.LoadAsync(source ?? @"E:\SteamLibrary\steamapps\workshop\content\431960\2815800031");
        Require(model.CurrentScenePlan != null, "样例未载入：" + model.Status);
        var control = model.CurrentScenePlan!.ParticleControls().First(c => c.CanOriginal);
        model.SetEffectSelection([control.Id]);
        var surface = (FrameworkElement)window.Content; window.Content = null;
        surface.Resources.MergedDictionaries.Add(window.Resources); surface.DataContext = model;
        void Layout() { surface.Measure(new Size(1440, 1000)); surface.Arrange(new Rect(0, 0, 1440, 1000)); surface.UpdateLayout(); }
        Layout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle); Layout();
        var mode = Descendants<ComboBox>(surface).Single(c => c.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path.Path == nameof(WorkspaceModel.SelectedParticleMode));
        var start = Descendants<TextBox>(surface).Single(c => c.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == nameof(WorkspaceModel.ParticleStartText));
        var stop = Descendants<TextBox>(surface).Single(c => c.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == nameof(WorkspaceModel.ParticleStopText));
        var startSlider = Descendants<Slider>(surface).Single(c => c.GetBindingExpression(RangeBase.ValueProperty)?.ParentBinding.Path.Path == nameof(WorkspaceModel.ParticleStart));
        var stopSlider = Descendants<Slider>(surface).Single(c => c.GetBindingExpression(RangeBase.ValueProperty)?.ParentBinding.Path.Path == nameof(WorkspaceModel.ParticleStop));
        var snapshots = new List<object>();
        void Snapshot(string stage) => snapshots.Add(new
        {
            stage,
            mode = (mode.SelectedItem as ParticleModeOption)?.Code,
            model.CanSetParticleWindow,
            startEnabled = start.IsEnabled,
            stopEnabled = stop.IsEnabled,
            startSliderEnabled = startSlider.IsEnabled,
            stopSliderEnabled = stopSlider.IsEnabled,
            model.ParticleStartText,
            model.ParticleStopText,
            model.Status,
            choice = model.CurrentSceneSelection!.Particles.Single(p => p.Id == control.Id)
        });
        Snapshot("natural");
        await Check("particleModeUnlocksActualControls", async () =>
        {
            mode.SetCurrentValue(Selector.SelectedItemProperty, model.ParticleModes.Single(m => m.Code == "drain"));
            mode.GetBindingExpression(Selector.SelectedItemProperty)!.UpdateSource();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle); Layout(); Snapshot("custom");
            Require(model.SelectedParticleMode?.Code == "drain", "下拉框没有切换为自定义时段");
            Require(start.IsEnabled && stop.IsEnabled && startSlider.IsEnabled && stopSlider.IsEnabled, "切换自定义时段后，时间框或滑块仍被禁用");
        });
        await Check("particleTypingAndSliderReachSelection", async () =>
        {
            Require(start.IsEnabled && stop.IsEnabled, "实际控件不能编辑");
            start.SetCurrentValue(TextBox.TextProperty, "2"); start.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            stop.SetCurrentValue(TextBox.TextProperty, "8"); stop.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle); Layout(); Snapshot("typed");
            var choice = model.CurrentSceneSelection!.Particles.Single(p => p.Id == control.Id);
            Require(choice.Start == 2 && choice.Stop == 8, "时间框输入没有保存到逐层设置");
            startSlider.SetCurrentValue(RangeBase.ValueProperty, 3d); stopSlider.SetCurrentValue(RangeBase.ValueProperty, 9d);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle); Layout(); Snapshot("sliders");
            choice = model.CurrentSceneSelection.Particles.Single(p => p.Id == control.Id);
            Require(choice.Start == 3 && choice.Stop == 9, "实际滑块没有写回发射时段");
        });
        await Check("particleAudioAndCopyPreserveTiming", () =>
        {
            var plan = model.CurrentScenePlan!; var selection = model.CurrentSceneSelection!;
            Require(selection.Particles.Single(p => p.Id == control.Id) is { Mode: "drain", Start: 3, Stop: 9 }, "界面编辑没有进入导出选择");
            using var copy = Wallpaper2Video.Runtime.Windows.PreparedScene.Create(plan, selection, output);
            Require(copy.ParticleManifest != null, "导出副本缺少粒子控制清单");
            var rows = Wallpaper2Video.Core.SceneJson.Array(copy.ParticleManifest!["managed_layers"]);
            var row = rows.Single(r => Wallpaper2Video.Core.SceneJson.Number(r?["index"]) == control.Index);
            Require(Wallpaper2Video.Core.SceneJson.Number(row?["video_emit_start"]) == 3 && Wallpaper2Video.Core.SceneJson.Number(row?["video_emit_stop"]) == 9, "导出副本时段与界面不一致");
            if (source?.Contains("3299228616") == true) Require(Wallpaper2Video.Core.SceneJson.Truth(row?["source_audio_response"]) && !Wallpaper2Video.Core.SceneJson.Truth(row?["audio_emission"]), "没有记录源音频响应已关闭");
            var copiedScene = Wallpaper2Video.Core.SceneJson.Parse(copy.Project.Scene!.Value.GetRawText());
            var copiedObject = Wallpaper2Video.Core.SceneJson.Array(copiedScene["objects"]).Single(o => Wallpaper2Video.Core.SceneJson.Text(o?["id"]) == Wallpaper2Video.Core.SceneJson.Text(row?["id"]));
            string particleFile = Path.Combine(copy.Project.Root, Wallpaper2Video.Core.SceneJson.Text(copiedObject?["particle"]));
            var definition = Wallpaper2Video.Core.SceneJson.Parse(File.ReadAllText(particleFile));
            if (source?.Contains("3299228616") == true) Require(Wallpaper2Video.Core.SceneJson.Array(definition["emitter"]).All(e => Wallpaper2Video.Core.SceneJson.Number(e?["audioprocessingmode"]) == 0), "副本仍依赖音频发射");
            return Task.CompletedTask;
        });
        await Check("particleTextEditingKeepsIntermediateInput", async () =>
        {
            start.SetCurrentValue(TextBox.TextProperty, ""); start.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Require(start.Text == "" && !model.CanRender, "清空时间框被滑块回写或非法输入仍可导出");
            foreach (string value in new[] { "2", "2.", "2.5" }) { start.SetCurrentValue(TextBox.TextProperty, value); start.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); await Dispatcher.Yield(DispatcherPriority.ContextIdle); }
            Require(start.Text == "2.5" && model.CurrentSceneSelection!.Particles.Single(p => p.Id == control.Id).Start == 2.5, "小数时间无法连续键入");
            Require(model.CanRender, "修正时间后未恢复预览和导出");
        });
        if (source?.Contains("3299228616") == true) await Check("allAvailableAudioParticlesAcceptTimeWindows", () =>
        {
            var plan = model.CurrentScenePlan!; var controls = plan.ParticleControls();
            Require(controls.Count(c => c.CanOriginal) == 36 && controls.Count(c => !c.CanOriginal) == 6, "用户素材粒子清单发生非预期变化");
            foreach (var particle in controls.Where(c => c.CanOriginal))
            {
                Require(particle.CanDrain, "可用粒子仍不能定时：" + particle.Label);
                var particles = model.CurrentSceneSelection!.Particles.Select(p => p.Id == particle.Id ? new Wallpaper2Video.Runtime.Windows.ParticleChoice(p.Id, "drain", 2, 8) : p);
                Require(plan.Select(model.CurrentSceneSelection.Enabled, particles, model.CurrentSceneSelection.DisabledEffects).Particles.Any(p => p.Id == particle.Id && p.Start == 2 && p.Stop == 8), "粒子时段未保存");
            }
            return Task.CompletedTask;
        });
        Directory.CreateDirectory(output); await File.WriteAllTextAsync(Path.Combine(output, "particle-input-trace.json"), JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true }));
        model.StopPlanning(); await model.ReleasePreviewAsync();
    }
}
