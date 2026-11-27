using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wallpaper2Video.Desktop;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;
using static Program;

internal static class SourceLayerChecks
{
    public static async Task RunAsync(string output, string source)
    {
        Directory.CreateDirectory(output); var window = new MainWindow(); var model = window.Model; await model.LoadAsync(source);
        model.ShowingSources = true; var image = model.SourceRows.First(r => r.Icon == "image" && r.CanToggle);
        string Hash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(model.CurrentScenePlan!.Project.PackagePath ?? model.CurrentScenePlan.Project.Entry)));
        string original = Hash();
        var before = model.Effects.ToDictionary(e => e.Key, e => e.Active);
        await Check("sourceLayerVisibilityIsIndependent", () =>
        {
            Require(model.SourceRows.Count == model.CurrentScenePlan!.SourceLayers().Count, "原素材面板丢失对象");
            model.ApplySourceVisibility([image.Index], false);
            Require(model.CurrentSceneSelection!.SourceVisibility[image.Index] == false, "原素材显隐未保存");
            Require(!image.Active && model.Effects.All(e => e.Active == before[e.Key]), "隐藏原素材改写了效果自身的开关");
            var children = model.Effects.Where(e => e.Metadata?.ObjectIndex == image.Index).ToArray(); Require(children.All(e => !e.Effective), "隐藏原素材后关联效果仍显示为生效");
            using (var copy = PreparedScene.Create(model.CurrentScenePlan, model.CurrentSceneSelection, output))
            {
                string id = Text(model.CurrentScenePlan.Scene!["objects"]![image.Index]?["id"]);
                Require(Array(Parse(copy.Project.Scene!.Value.GetRawText())["objects"]).All(o => Text(o?["id"]) != id), "导出副本没有隐藏原素材");
            }
            model.UndoEffectsCommand.Execute(null); Require(image.Active, "撤销没有恢复原素材");
            return Task.CompletedTask;
        });
        await Check("sourceVisibilitySurvivesOtherEdits", async () =>
        {
            model.ApplySourceVisibility([image.Index], false);
            var particle = model.CurrentScenePlan!.ParticleControls().First(p => p.CanDrain); model.SetParticle(particle.Id, "drain", 1, 4);
            Require(!model.CurrentSceneSelection!.SourceVisibility[image.Index], "粒子模式编辑清除了原素材选择");
            var effect = model.Effects.First(e => e.CanToggle && e.Kind == "effect"); model.ToggleEffect(effect.Key, false);
            Require(!model.CurrentSceneSelection.SourceVisibility[image.Index], "效果编辑清除了原素材选择");
            model.DurationText = "24"; var until = DateTime.UtcNow.AddSeconds(20); while (model.Planning) { await Task.Delay(50); Require(DateTime.UtcNow < until, "重新规划超时"); }
            Require(!model.CurrentSceneSelection!.SourceVisibility[image.Index], "修改总长清除了原素材选择");
            model.ApplySourceVisibility([image.Index], true);
        });
        await Check("sourcePanelActualEyeAndLayout", async () =>
        {
            var surface = (FrameworkElement)window.Content; TextElement.SetFontFamily(surface, window.FontFamily); TextElement.SetFontSize(surface, window.FontSize); ((Panel)surface).Background = window.Background;
            window.Content = null; surface.Resources.MergedDictionaries.Add(window.Resources); surface.DataContext = model;
            try
            {
                surface.Width = 1440; surface.Height = 800; surface.Measure(new Size(1440, 800)); surface.Arrange(new Rect(0, 0, 1440, 800)); surface.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle); surface.UpdateLayout();
                var list = (ListBox)window.FindName("SourceLayerList"); Require(list.ActualHeight > 100 && list.ActualWidth > 280, "原素材列表视口太小");
                list.SelectedItems.Clear(); list.SelectedItems.Add(image); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                var eye = Descendants<EffectCheckBox>(list).First(c => c.DataContext is SourceLayerRow row && row.Index == image.Index);
                typeof(ToggleButton).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(eye, null);
                Require(!image.Active && model.Detail.Contains(image.Label), "原素材眼睛或属性来源绑定未生效");
                model.UndoEffectsCommand.Execute(null); Require(image.Active, "原素材眼睛操作不能撤销");
                var bitmap = new RenderTargetBitmap(1440, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(output, "source-panel.png"))) encoder.Save(file);
                Require(!model.HasSelectedParticle && !model.HasPhases, "原素材属性区混入了旧效果设置");
                model.ShowingSources = false; Require(model.ShowingEffects, "无法切回效果面板");
            }
            finally { window.Content = surface; }
        });
        Require(original == Hash(), "原素材被修改");
        model.StopPlanning(); await model.ReleasePreviewAsync();
    }
}
