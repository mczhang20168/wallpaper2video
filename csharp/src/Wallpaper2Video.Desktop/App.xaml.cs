using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;

namespace Wallpaper2Video.Desktop;

public partial class App : Application
{
    public App()
    {
        // Apply before WPF creates its first HWND/composition target. On affected
        // drivers hardware presentation can stay white despite a complete visual tree.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 3 && e.Args[0] == "--shooting-star-acceptance")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await ParticleEditorAcceptance.RunAsync(e.Args[1], e.Args[2], true); Shutdown(code); return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--particle-editor-acceptance")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await ParticleEditorAcceptance.RunAsync(e.Args[1], e.Args[2]); Shutdown(code); return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--playback-acceptance")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await PlaybackAcceptance.RunAsync(e.Args[1], e.Args[2]); Shutdown(code); return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--workspace-native-regressions")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await WorkspaceNativeAcceptance.RunAsync(e.Args[1], e.Args[2]); Shutdown(code); return;
        }
        if (e.Args.Length == 4 && e.Args[0] == "--workspace-regressions")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await WorkspaceRegressionAcceptance.RunAsync(e.Args[1], e.Args[2], e.Args[3]);
            Shutdown(code);
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--scene-lifecycle-acceptance")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await SceneLifecycleAcceptance.RunAsync(e.Args[1], e.Args[2]);
            Shutdown(code);
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--full-scene-acceptance")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await FullSceneAcceptance.RunAsync(e.Args[1], e.Args[2]);
            Shutdown(code);
            return;
        }
        if (e.Args.Length == 4 && e.Args[0] == "--scene-acceptance")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await SceneAcceptance.RunAsync(e.Args[1], e.Args[2], e.Args[3]);
            Shutdown(code);
            return;
        }
        if (e.Args.Length == 4 && e.Args[0] == "--acceptance")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await DesktopAcceptance.RunAsync(e.Args[1], e.Args[2], e.Args[3]);
            Shutdown(code);
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length == 2 && e.Args[0] == "--source") await window.Model.LoadAsync(e.Args[1]);
    }
}
