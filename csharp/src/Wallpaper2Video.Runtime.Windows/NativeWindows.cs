using System.Runtime.InteropServices;

namespace Wallpaper2Video.Runtime.Windows;

public interface ILiveMonitor
{
    (int X, int Y) Position { get; }
    Exception? Failure => null;
    Task AttachAsync(nint source, int width, int height, CancellationToken cancellation);
    Task DetachAsync(nint source);
}

public static class NativeWindows
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; public Rect(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; } }
    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct ThumbnailProperties
    {
        public uint Flags;
        public Rect Destination, Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientOnly;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string? className, string title);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(nint window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool ScreenToClient(nint window, ref Point point);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustWindowRectEx(ref Rect rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint extendedStyle);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustWindowRectExForDpi(ref Rect rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint extendedStyle, uint dpi);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(nint thumbnail, in ThumbnailProperties properties);
    public static (int X, int Y) OffscreenPosition() => (GetSystemMetrics(76) + GetSystemMetrics(78) + 64, GetSystemMetrics(77) + 32);
    public static bool DesktopAvailable()
    {
        nint desktop = OpenInputDesktop(0, false, 0x0100);
        if (desktop == 0) return false;
        CloseDesktop(desktop); return true;
    }
    public static (int Width, int Height) Size(nint window)
    {
        if (!GetClientRect(window, out Rect rect)) throw new IOException("无法读取录制窗口尺寸");
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    public static void Park(nint window, int width, int height)
    {
        var (x, y) = OffscreenPosition();
        ResizeClient(window, width, height, x, y);
    }
    public static void ResizeClient(nint window, int width, int height, int x, int y)
    {
        var rect = new Rect(0, 0, width, height);
        uint dpi = GetDpiForWindow(window);
        bool adjusted = dpi > 0 ? AdjustWindowRectExForDpi(ref rect, unchecked((uint)GetWindowLongPtr(window, -16)), false, unchecked((uint)GetWindowLongPtr(window, -20)), dpi)
            : AdjustWindowRectEx(ref rect, unchecked((uint)GetWindowLongPtr(window, -16)), false, unchecked((uint)GetWindowLongPtr(window, -20)));
        if (!adjusted) throw new IOException("无法计算录制源客户区尺寸");
        if (!SetWindowPos(window, 1, x, y, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x10 | 0x20)) throw new IOException("无法将本次录制窗口移到后台");
        // WPE uses a custom non-client frame. Correct the system estimate using its measured client rectangle.
        for (int attempt = 0; attempt < 3 && Size(window) != (width, height); attempt++)
        {
            var actual = Size(window);
            if (!GetWindowRect(window, out var outer)) throw new IOException("无法读取录制窗口外框");
            if (!SetWindowPos(window, 1, x, y, outer.Right - outer.Left + width - actual.Width, outer.Bottom - outer.Top + height - actual.Height, 0x10 | 0x20)) throw new IOException("无法校正录制窗口客户区尺寸");
        }
        if (Size(window) != (width, height)) throw new IOException($"录制源尺寸不符：预期{width}×{height}，实际{Size(window)}");
    }
    public static Rect Fit(Rect bounds, int width, int height)
    {
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top || width <= 0 || height <= 0) return new(bounds.Left, bounds.Top, bounds.Left, bounds.Top);
        double scale = Math.Min((double)(bounds.Right - bounds.Left) / width, (double)(bounds.Bottom - bounds.Top) / height);
        int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));
        int x = bounds.Left + (bounds.Right - bounds.Left - w) / 2, y = bounds.Top + (bounds.Bottom - bounds.Top - h) / 2;
        return new(x, y, x + w, y + h);
    }
}
