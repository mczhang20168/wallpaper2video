using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wallpaper2Video.Desktop;
using static Program;

// Detached WPF layout/raster checks; no screen capture, desktop input or system DPI change.
internal static class LayoutChecks
{
    public static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var window = new MainWindow(); var model = window.Model;
        await model.LoadAsync(@"E:\SteamLibrary\steamapps\workshop\content\431960\3299228616");
        var particle = model.CurrentScenePlan!.ParticleControls().First(p => p.CanDrain);
        model.SetEffectSelection([particle.Id]); model.SetParticle(particle.Id, "drain", 2, 8);
        model.BrowserGrouping = "按对象"; model.SearchText = particle.Label;
        var surface = (FrameworkElement)window.Content;
        TextElement.SetFontFamily(surface, window.FontFamily);
        TextElement.SetFontSize(surface, window.FontSize);
        surface.UseLayoutRounding = window.UseLayoutRounding; surface.SnapsToDevicePixels = window.SnapsToDevicePixels;
        TextOptions.SetTextFormattingMode(surface, TextFormattingMode.Display);
        ((Panel)surface).Background = window.Background;
        window.Content = null;
        surface.Resources.MergedDictionaries.Add(window.Resources); surface.DataContext = model;
        var metrics = new List<object>();
        try
        {
            foreach (var layout in new[]{
                (Name:"compact-100",Width:1040,Height:650,Dpi:96),
                (Name:"laptop-100",Width:1280,Height:680,Dpi:96),
                (Name:"desktop-100",Width:1440,Height:800,Dpi:96),
                (Name:"desktop-125",Width:1520,Height:800,Dpi:120),
                (Name:"desktop-150",Width:1264,Height:680,Dpi:144),
                (Name:"large-150",Width:2560,Height:1380,Dpi:144)})
            {
                await Check("layout-" + layout.Name, async () =>
                {
                    surface.Width = layout.Width; surface.Height = layout.Height;
                    surface.Measure(new Size(layout.Width, layout.Height)); surface.Arrange(new Rect(0, 0, layout.Width, layout.Height)); surface.UpdateLayout();
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle); surface.UpdateLayout();
                    var tree = Descendants<EffectBrowserList>(surface).Single();
                    var eye = Descendants<EffectCheckBox>(tree).First(c => c.ActualWidth > 0);
                    var label = Descendants<TextBlock>(tree).First(t => t.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == "Label");
                    var icon = Descendants<BrowserNodeIcon>(tree).First();
                    Require(label.FontSize >= 14 && label.FontSize <= 16, "列表正文不符合可读字号");
                    Require(icon.Width >= label.FontSize && icon.Width <= label.FontSize * 1.5, "图标相对文字过大或过小");
                    Require(eye.ActualWidth >= 32 && eye.ActualHeight >= 32, "眼睛点击区域过小");
                    Require(tree.ActualHeight >= 110 && tree.ActualWidth >= 280, "效果列表视口过小");
                    var viewer = Descendants<ScrollViewer>(tree).First();
                    Require(viewer.ScrollableWidth < 2, "浅层对象出现多余横向滚动条");
                    var controls = Descendants<FrameworkElement>(surface).Where(e => AutomationProperties.GetName(e) is "效果搜索" or "效果分组方式" or "效果状态筛选" or "效果面板菜单" or "播放进度").ToArray();
                    Require(controls.Length == 5, "缺少主要控件");
                    foreach (var control in controls)
                    {
                        var bounds = control.TransformToAncestor(surface).TransformBounds(new Rect(control.RenderSize));
                        Require(bounds.Left >= 0 && bounds.Right <= layout.Width + 1 && bounds.Top >= 0 && bounds.Bottom <= layout.Height + 1, "控件超出窗口：" + AutomationProperties.GetName(control));
                        Require(control.ActualHeight >= 30, "主要控件高度不足：" + AutomationProperties.GetName(control));
                    }
                    var start = (TextBox)window.FindName("ParticleStartInput"); var stop = (TextBox)window.FindName("ParticleStopInput");
                    Require(start.IsEnabled && stop.IsEnabled && start.ActualWidth >= 100 && stop.ActualWidth >= 100, "粒子时间输入拥挤或不可编辑");
                    Require(start.FontSize >= 14 && stop.FontSize >= 14, "粒子时间字号过小");
                    var properties = Descendants<ScrollViewer>(surface).Single(s => s.Content is StackPanel p && Descendants<TextBox>(p).Contains(start));
                    var timeBounds = start.TransformToAncestor(properties).TransformBounds(new Rect(start.RenderSize));
                    Require(timeBounds.Top >= 0 && timeBounds.Bottom <= properties.ViewportHeight, "粒子时间框被挤到属性视口外");
                    foreach (var button in Descendants<Button>(surface).Where(b => b.Content is string && b.IsEnabled && b.ActualWidth > 0 && b.Visibility == Visibility.Visible))
                    {
                        var content = Descendants<ContentPresenter>(button).FirstOrDefault();
                        if (content != null) Require(content.DesiredSize.Width <= content.ActualWidth + 1, "按钮文字裁切：" + button.Content);
                    }
                    double scale = layout.Dpi / 96d;
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(layout.Width * scale), (int)Math.Ceiling(layout.Height * scale), layout.Dpi, layout.Dpi, PixelFormats.Pbgra32); bitmap.Render(surface);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(output, layout.Name + ".png"))) encoder.Save(file);
                    metrics.Add(new { layout.Name, logicalWidth = layout.Width, logicalHeight = layout.Height, rasterDpi = layout.Dpi, listFont = label.FontSize, iconWidth = icon.Width, eyeWidth = eye.ActualWidth, eyeHeight = eye.ActualHeight, listWidth = tree.ActualWidth, listHeight = tree.ActualHeight, inputWidth = start.ActualWidth });
                });
            }
            await Check("layoutChecksNeverShowWindows", () => { Require(!window.IsVisible && Application.Current.Windows.Cast<Window>().All(w => !w.IsVisible), "离屏排版显示了窗口"); return Task.CompletedTask; });
            File.WriteAllText(Path.Combine(output, "layout-metrics.json"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Content = surface; model.StopPlanning(); await model.ReleasePreviewAsync(); }
    }
}
