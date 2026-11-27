using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wallpaper2Video.Desktop;

internal static class FullSceneAcceptance
{
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Wait(Func<bool> condition, string message, double seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException(message); await Task.Delay(50); }
    }
    public static async Task<int> RunAsync(string source, string directory)
    {
        Directory.CreateDirectory(directory);
        var result = new Dictionary<string, object?>();
        var window = new MainWindow { ShowActivated = false, Title = "Wallpaper2Video · 完整场景验收" };
        string package = Path.Combine(source, "scene.pkg"), metadata = Path.Combine(source, "project.json");
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var original = new[] { package, metadata }.Where(File.Exists).ToDictionary(p => p, Hash);
        try
        {
            window.Show(); window.UpdateLayout();
            var model = window.Model;
            await model.LoadAsync(source);
            Require(model.CanRender && model.Effects.Count >= 21, "场景加载失败：" + model.Status);
            result["loadedRows"] = model.Effects.Count;
            Require(((Image)window.FindName("Poster")).Source != null, "封面未显示");
            model.SearchText = "Shooting";
            Require(model.BrowserRows.Any(r => r.IsLeaf) && model.BrowserRows.Count(r => r.IsLeaf) < model.Effects.Count, "搜索未过滤效果");
            model.SearchText = "";
            model.SelectedEffect = model.Effects.First(e => e.Key == "effect:0:0");
            Require(model.Phases.Count > 0, "选中效果未显示子时钟");
            string phase = model.Phases[0].Id;
            int cycles = model.Phases[0].Cycles;
            model.SetCycles(phase, 3);
            await Wait(() => !model.Planning, "周期更新超时");
            Require(model.CurrentScenePlan!.Cycles[phase] == 3, "循环次数未保存");
            model.ToggleEffect("effect:0:0", false);
            Require(model.CurrentSceneSelection!.DisabledEffects.Contains("effect:0:0"), "效果关闭未保存");
            model.ToggleEffect("effect:0:0", true);
            model.DurationText = "6"; model.OverlapText = "1"; model.FpsText = "30"; model.WidthText = "640"; model.HeightText = "320"; model.WarmupText = "0.5";
            await Wait(() => !model.Planning && model.CanRender, "参数更新失败：" + model.Status);
            Require(model.CurrentScenePlan!.Cycles[phase] == 3, "更改总长丢失循环次数");
            foreach (var p in model.CurrentSceneSelection!.Particles.ToArray()) model.SetParticle(p.Id, "off");
            model.SelectedEffect = model.Effects.First(e => e.Kind == "particle");
            Require(model.HasSelectedParticle, "粒子设置未显示");
            result["editorControls"] = true;
            model.SelectedQuality = model.Qualities[0];
            Task preview = model.RenderAsync(true);
            bool sawLive = false;
            while (!preview.IsCompleted)
            {
                if (model.LiveMonitor is DwmLiveMonitor monitor && monitor.Attached)
                {
                    sawLive = true; Require(monitor.SourceSize == (640, 320), "源窗口尺寸不符");
                    window.Width = 1320; window.UpdateLayout();
                }
                await Task.Delay(50);
            }
            await preview;
            Require(sawLive, "没有观察到工作区实时监看");
            Require(model.MediaSource != null && File.Exists(model.MediaSource.LocalPath), "场景预览失败：" + model.Status);
            var player = (MediaElement)window.FindName("Player");
            await Wait(() => window.PlaybackWidth == 640 && model.PlayCommand.CanExecute(null), "预览未在播放器打开：" + model.Status);
            model.PlayCommand.Execute(null); await Wait(() => model.Position > .1, "预览未前进"); model.PlayCommand.Execute(null);
            model.Position = 2; await Task.Delay(200); Require(Math.Abs(window.PlaybackPosition - 2) < .5, "预览定位失败");
            result["scenePreview"] = true; result["liveMonitor"] = sawLive;
            model.SelectedEffect = model.Effects.First(e => e.Key == "effect:0:0");
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ContextIdle);
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var image = File.Create(Path.Combine(directory, "workspace.png"))) encoder.Save(image);
            model.OutputPath = Path.Combine(directory, "scene-export.mp4");
            await model.RenderAsync(false);
            Require(File.Exists(model.OutputPath), "场景导出失败：" + model.Status);
            using (var report = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(model.OutputPath, ".report.json"))))
            { result["exportValidation"] = report.RootElement.GetProperty("validation").Clone(); Require(report.RootElement.GetProperty("source_window_closed").GetBoolean(), "导出未关闭源窗口"); }
            model.OutputPath = Path.Combine(directory, "cancel.mp4");
            Task cancelled = model.RenderAsync(false);
            await Task.Delay(800); model.Cancel(); await cancelled;
            Require(!File.Exists(model.OutputPath) && !File.Exists(Path.ChangeExtension(model.OutputPath, ".report.json")), "取消留下半成品");
            result["cancelCleanup"] = true;
            model.OutputPath = Path.Combine(directory, "close.mp4");
            Task closing = model.RenderAsync(false); await Task.Delay(800); window.Close(); await closing;
            await Wait(() => !window.IsVisible, "任务中关窗未完成");
            Require(!File.Exists(model.OutputPath), "关窗留下半成品");
            result["closeCleanup"] = true;
            Require(original.All(p => Hash(p.Key) == p.Value), "源文件被修改"); result["sourceUnchanged"] = true;
            result["passed"] = true;
            return 0;
        }
        catch (Exception error)
        {
            result["passed"] = false; result["error"] = error.ToString();
            window.Model.Cancel(); while (window.Model.Busy) await Task.Delay(50); window.Close();
            return 1;
        }
        finally { await File.WriteAllTextAsync(Path.Combine(directory, "full-scene-acceptance.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); }
    }
}
