using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Data;

namespace Wallpaper2Video.Desktop;

public sealed class BrowserRowWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => Math.Max(values.Length > 1 && values[1] is double min ? min : 164, values[0] is double width ? Math.Max(0, width) : 164);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class HierarchyGuides : FrameworkElement
{
    public static readonly DependencyProperty DepthProperty = DependencyProperty.Register(nameof(Depth), typeof(int), typeof(HierarchyGuides), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaskProperty = DependencyProperty.Register(nameof(Mask), typeof(string), typeof(HierarchyGuides), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LastProperty = DependencyProperty.Register(nameof(Last), typeof(bool), typeof(HierarchyGuides), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public int Depth { get => (int)GetValue(DepthProperty); set => SetValue(DepthProperty, value); }
    public string Mask { get => (string)GetValue(MaskProperty); set => SetValue(MaskProperty, value); }
    public bool Last { get => (bool)GetValue(LastProperty); set => SetValue(LastProperty, value); }
    protected override void OnRender(DrawingContext drawing)
    {
        if (Depth <= 0) return;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(78, 91, 111)), 1);
        for (int i = 0; i < Depth - 1; i++) if (i < Mask.Length && Mask[i] == '1') drawing.DrawLine(pen, new(i * 20 + 9.5, 0), new(i * 20 + 9.5, ActualHeight));
        double x = (Depth - 1) * 20 + 9.5, mid = ActualHeight / 2;
        drawing.DrawLine(pen, new(x, 0), new(x, Last ? mid : ActualHeight));
        drawing.DrawLine(pen, new(x, mid), new(Depth * 20 + 2, mid));
    }
}

public sealed class BrowserNodeIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(BrowserNodeIcon), new FrameworkPropertyMetadata("effect", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    protected override void OnRender(DrawingContext d)
    {
        var blue = new SolidColorBrush(Color.FromRgb(133, 172, 224)); var pen = new Pen(blue, 1.2);
        if (Kind == "folder")
        {
            d.DrawGeometry(new SolidColorBrush(Color.FromRgb(174, 151, 91)), new Pen(new SolidColorBrush(Color.FromRgb(211, 188, 127)), 1), Geometry.Parse("M1,4 L6,4 8,6 16,6 16,15 1,15 Z"));
            d.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(223, 201, 151)), 1), new(2, 8), new(15, 8));
        }
        else if (Kind == "image") d.DrawGeometry(null, pen, Geometry.Parse("M1,3 L16,3 16,16 1,16 Z M2,14 L6,9 10,13 13,9 16,13 M5,6 L5,7"));
        else if (Kind == "sound") d.DrawGeometry(null, pen, Geometry.Parse("M2,7 L5,7 9,3 9,16 5,12 2,12 Z M12,6 C16,8 16,12 12,14"));
        else if (Kind == "text") d.DrawGeometry(null, pen, Geometry.Parse("M2,4 L15,4 M8,4 L8,16 M5,16 L11,16 M2,4 L2,7 M15,4 L15,7"));
        else if (Kind == "particle")
        {
            d.DrawEllipse(blue, null, new(4, 6), 2, 2); d.DrawEllipse(blue, null, new(12, 4), 1.5, 1.5); d.DrawEllipse(blue, null, new(10, 13), 2.5, 2.5); d.DrawEllipse(blue, null, new(2, 14), 1, 1);
        }
        else if (Kind == "model") d.DrawGeometry(null, pen, Geometry.Parse("M8,2 L15,6 15,13 8,17 1,13 1,6 Z M1,6 L8,10 15,6 M8,10 L8,17"));
        else if (Kind == "timeline")
        {
            d.DrawEllipse(null, pen, new(8.5, 9), 7, 7);
            d.DrawLine(pen, new(8.5, 9), new(8.5, 4)); d.DrawLine(pen, new(8.5, 9), new(12, 11));
        }
        else
        {
            string text = Kind == "script" ? "<>" : Kind == "camera" ? "◈" : "fx";
            var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, blue, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            d.DrawText(label, new(0, 1));
        }
    }
}
