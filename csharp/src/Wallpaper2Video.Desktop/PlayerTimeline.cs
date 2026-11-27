using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Wallpaper2Video.Desktop;

/// <summary>Click, then drag anywhere on the timeline, including outside the thumb.</summary>
public sealed class PlayerTimeline : Slider
{
    private bool pointerDragging;
    public PlayerTimeline() { IsMoveToPointEnabled = true; MinHeight = 32; }
    public double PositionAt(double x) => Minimum + Math.Clamp((x - 18) / Math.Max(1, ActualWidth - 36), 0, 1) * (Maximum - Minimum);
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled || Maximum <= Minimum) return;
        Focus(); pointerDragging = CaptureMouse(); if (!pointerDragging) return;
        RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        SetCurrentValue(ValueProperty, PositionAt(e.GetPosition(this).X)); e.Handled = true;
    }
    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        double time = PositionAt(e.GetPosition(this).X);
        ToolTip = TimeSpan.FromSeconds(Math.Max(0, time)).ToString(@"mm\:ss\.fff");
        if (pointerDragging) { SetCurrentValue(ValueProperty, time); e.Handled = true; }
        base.OnPreviewMouseMove(e);
    }
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!pointerDragging) { base.OnPreviewMouseLeftButtonUp(e); return; }
        SetCurrentValue(ValueProperty, PositionAt(e.GetPosition(this).X));
        Finish(); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { Finish(); base.OnLostMouseCapture(e); }
    private void Finish()
    {
        if (!pointerDragging) return;
        pointerDragging = false; if (IsMouseCaptured) ReleaseMouseCapture();
        RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { e.Handled = true; }
}
