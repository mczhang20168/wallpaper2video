using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Wallpaper2Video.Runtime.Windows;

namespace Wallpaper2Video.Desktop;

public sealed class DwmLiveMonitor : ILiveMonitor, IDisposable
{
    private readonly Window window;
    private readonly FrameworkElement surface;
    private nint thumbnail, source;
    private int width, height;
    private NativeWindows.Rect rectangle;
    private bool disposed, lastVisible;
    public Exception? Failure { get; private set; }
    public bool Attached => thumbnail != 0;
    public nint SourceWindow => source;
    public (int Width, int Height) SourceSize => (width, height);
    public (int X, int Y) Position => NativeWindows.OffscreenPosition();
    public DwmLiveMonitor(Window window, FrameworkElement surface)
    {
        this.window = window; this.surface = surface;
        window.LayoutUpdated += Resize;
        window.LocationChanged += Resize;
        window.StateChanged += Resize;
    }
    public async Task AttachAsync(nint hwnd, int expectedWidth, int expectedHeight, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await window.Dispatcher.InvokeAsync(() =>
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (disposed || !window.IsVisible) throw new IOException("工作区已关闭，不能挂接录制窗口");
            Detach(); Failure = null;
            try
            {
                if (!NativeWindows.IsWindow(hwnd)) throw new IOException("录制窗口已关闭");
                NativeWindows.Park(hwnd, expectedWidth, expectedHeight);
                nint destination = new WindowInteropHelper(window).Handle;
                int result = NativeWindows.DwmRegisterThumbnail(destination, hwnd, out thumbnail);
                if (result < 0) throw new IOException($"无法显示实时录制画面（0x{result:X8}）");
                source = hwnd; width = expectedWidth; height = expectedHeight; rectangle = default;
                Update(); timeout.Token.ThrowIfCancellationRequested();
            }
            catch { Detach(); throw; }
        }, DispatcherPriority.Normal, timeout.Token).Task;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("工作区录制监视器响应超时"); }
    }
    public async Task DetachAsync(nint hwnd)
    {
        if (window.Dispatcher.HasShutdownStarted) return;
        await window.Dispatcher.InvokeAsync(() => { if (source == hwnd || source == 0) Detach(); }, DispatcherPriority.Send).Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private void Resize(object? sender, EventArgs e)
    {
        if (thumbnail == 0) return;
        try { Update(); }
        catch (Exception error) { Failure = error; Detach(); }
    }
    private void Update()
    {
        if (thumbnail == 0) return;
        nint destination = new WindowInteropHelper(window).Handle;
        var left = surface.PointToScreen(new Point(0, 0));
        var right = surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight));
        var a = new NativeWindows.Point { X = (int)Math.Round(left.X), Y = (int)Math.Round(left.Y) };
        var b = new NativeWindows.Point { X = (int)Math.Round(right.X), Y = (int)Math.Round(right.Y) };
        NativeWindows.ScreenToClient(destination, ref a); NativeWindows.ScreenToClient(destination, ref b);
        var fit = NativeWindows.Fit(new(a.X, a.Y, b.X, b.Y), width, height);
        bool visible = !NativeWindows.IsIconic(destination);
        if (fit.Equals(rectangle) && visible == lastVisible) return;
        rectangle = fit;
        lastVisible = visible;
        var properties = new NativeWindows.ThumbnailProperties { Flags = 1 | 4 | 8 | 16, Destination = fit, Opacity = 255, Visible = visible, ClientOnly = true };
        int result = NativeWindows.DwmUpdateThumbnailProperties(thumbnail, in properties);
        if (result < 0) throw new IOException($"实时录制画面更新失败（0x{result:X8}）");
    }
    private void Detach()
    {
        if (thumbnail != 0) NativeWindows.DwmUnregisterThumbnail(thumbnail);
        thumbnail = source = 0; width = height = 0; rectangle = default; lastVisible = false;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Detach();
        window.LayoutUpdated -= Resize; window.LocationChanged -= Resize; window.StateChanged -= Resize;
    }
}
