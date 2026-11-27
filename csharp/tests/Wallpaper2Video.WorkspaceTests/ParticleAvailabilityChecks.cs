using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Wallpaper2Video.Desktop;
using static Program;

internal static class ParticleAvailabilityChecks
{
    public static async Task RunAsync(string output, string source)
    {
        var window = new MainWindow(); var model = window.Model; await model.LoadAsync(source);
        var surface = (FrameworkElement)window.Content; window.Content = null; surface.Resources.MergedDictionaries.Add(window.Resources); surface.DataContext = model;
        void Layout() { surface.Measure(new Size(1440, 1000)); surface.Arrange(new Rect(0, 0, 1440, 1000)); surface.UpdateLayout(); }
        var controls = model.CurrentScenePlan!.ParticleControls();
        await Check("lightShaftsUseDefaultSpriteRenderer", async () =>
        {
            var light = controls.Single(p => p.Label == "Light shafts 1"); Require(light.CanDrain, "省略renderer的光束被误判不可定时：" + light.Reason);
            model.SetEffectSelection([light.Id]); Layout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var mode = (ComboBox)window.FindName("ParticleModeInput"); mode.SetCurrentValue(Selector.SelectedItemProperty, model.ParticleModes.Single(p => p.Code == "drain"));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle); Layout();
            Require(model.SelectedParticleMode?.Code == "drain" && model.CanSetParticleWindow, "光束定时模式未生效");
            var input = (TextBox)window.FindName("ParticleStartInput"); Require(input.IsEnabled, "光束时间框仍不能编辑");
            Require(!model.ParticleWarning.Contains("自然播放不使用起止时间"), "定时下仍显示自然播放提示");
        });
        await Check("unsupportedStaticChildrenCannotSelectTimedMode", async () =>
        {
            var leaves = controls.Single(p => p.Label == "Leaves 3"); Require(!leaves.CanDrain && leaves.Reason.Contains("子系统"), "未核对静态子系统被强制纳入定时");
            model.SetEffectSelection([leaves.Id]); Layout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle); Layout();
            var mode = (ComboBox)window.FindName("ParticleModeInput");
            int index = model.ParticleModes.ToList().FindIndex(p => p.Code == "drain");
            var item = new ComboBoxItem { DataContext = model.ParticleModes[index], Style = mode.ItemContainerStyle }; item.Measure(new Size(300, 60)); item.Arrange(new Rect(0, 0, 300, 60)); item.UpdateLayout();
            Require(!item.IsEnabled, "不支持定时的粒子仍提供可点击的定时选项");
            mode.SetCurrentValue(Selector.SelectedItemProperty, model.ParticleModes[index]); await Dispatcher.Yield(DispatcherPriority.ContextIdle); Layout();
            Require((mode.SelectedItem as ParticleModeOption)?.Code == "original" && model.SelectedParticleMode?.Code == "original", "拒绝定时时，下拉显示与实际模式仍不一致");
            Require(model.ParticleWarning.Contains(leaves.Reason) && !model.ParticleWarning.Contains("请切换"), "自然播放提示没有说明不可定时的真实原因");
        });
        model.StopPlanning(); await model.ReleasePreviewAsync(); window.Content = surface;
    }
}
