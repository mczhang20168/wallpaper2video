using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Wallpaper2Video.Desktop;

public sealed class ParticleTimeline : FrameworkElement
{
    private static DependencyProperty Field(string name, double initial) => DependencyProperty.Register(name, typeof(double), typeof(ParticleTimeline), new FrameworkPropertyMetadata(initial, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DurationProperty = Field(nameof(Duration), 20);
    public static readonly DependencyProperty StartProperty = Field(nameof(Start), 0);
    public static readonly DependencyProperty StopProperty = Field(nameof(Stop), 0);
    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Start { get => (double)GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public double Stop { get => (double)GetValue(StopProperty); set => SetValue(StopProperty, value); }
    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth <= 0 || Duration <= 0 || !double.IsFinite(Duration)) return;
        double width = Math.Max(1, ActualWidth - 20), left = 10;
        drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(46, 51, 61)), null, new(left, 14, width, 8), 3, 3);
        double a = left + width * Math.Clamp(Start / Duration, 0, 1), b = left + width * Math.Clamp(Stop / Duration, 0, 1);
        var active = new SolidColorBrush(Color.FromRgb(141, 169, 255));
        if (b > a) drawing.DrawRoundedRectangle(active, null, new(a, 14, b - a, 8), 3, 3);
        drawing.DrawLine(new Pen(Brushes.LightGreen, 2), new(a, 9), new(a, 27));
        drawing.DrawLine(new Pen(Brushes.SandyBrown, 2), new(b, 9), new(b, 27));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = new FormattedText($"开始 {Start:0.###}s   停止 {Stop:0.###}s   /   总长 {Duration:0.###}s", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 10, Brushes.LightGray, dpi);
        drawing.DrawText(label, new(left, 31));
    }
}
