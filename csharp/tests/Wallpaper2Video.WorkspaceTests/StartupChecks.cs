using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wallpaper2Video.Desktop;

internal static class StartupChecks
{
    public static int RunHidden(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var app = new App();
        var mode = RenderOptions.ProcessRenderMode;
        app.InitializeComponent();
        bool passed = mode == RenderMode.SoftwareOnly && Application.Current.Windows.Count == 0;
        File.WriteAllText(Path.Combine(output, "startup-hidden.json"), JsonSerializer.Serialize(new { passed, renderModeBeforeFirstWindow = mode.ToString(), windows = Application.Current.Windows.Count }));
        app.Shutdown(); return passed ? 0 : 1;
    }
    // Runs in a separate process with the real App/resources/Startup path, unlike
    // detached layout tests which cannot detect a native-window presentation failure.
    [STAThread]
    public static int Run(string output, bool loadFish)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var report = new Dictionary<string, object?>(); int code = 1;
        var app = new App(); app.InitializeComponent();
        report["renderModeBeforeFirstWindow"] = RenderOptions.ProcessRenderMode.ToString();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, e) => { report["error"] = e.Exception.ToString(); e.Handled = true; app.Shutdown(1); };
        app.Activated += async (_, _) =>
        {
            if (report.ContainsKey("activated")) return; report["activated"] = true;
            try
            {
                var window = (MainWindow)app.MainWindow;
                if (loadFish) await window.Model.LoadAsync(@"E:\SteamLibrary\steamapps\workshop\content\431960\2468489223");
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                if (RenderOptions.ProcessRenderMode != RenderMode.SoftwareOnly) throw new InvalidOperationException("启动仍依赖有白屏故障的硬件绘制路径");
                if (!window.IsVisible || window.ActualWidth < 900 || window.ActualHeight < 580) throw new InvalidOperationException("正式启动未显示主窗口");
                if (loadFish && (!window.Model.CanRender || !window.Model.Effects.Any(r => r.Key == "timeline:objects:3:origin"))) throw new InvalidOperationException("载入壁纸后周期设置不可用");
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(output, "startup.png"))) encoder.Save(file);
                report["visible"] = window.IsVisible; report["width"] = window.ActualWidth; report["height"] = window.ActualHeight; report["fishLoaded"] = loadFish;
                report["passed"] = true; code = 0;
                window.Model.StopPlanning(); await window.Model.ReleasePreviewAsync(); window.Close();
            }
            catch (Exception error) { report["error"] = error.ToString(); report["passed"] = false; }
            finally { app.Shutdown(code); }
        };
        app.Run();
        File.WriteAllText(Path.Combine(output, "startup-check.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return code;
    }
}
