using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wallpaper2Video.Desktop;

public partial class MainWindow : Window
{
    public WorkspaceModel Model { get; } = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private bool closing;
    private bool closeRequested;
    private readonly DwmLiveMonitor liveMonitor;
    private DecodedVideoPlayer? decoder;
    private long mediaRevision;
    private double decodedFramePosition;
    internal double PlaybackPosition => decoder?.Position ?? Player.Position.TotalSeconds;
    internal double DisplayedFramePosition => decoder == null ? Player.Position.TotalSeconds : decodedFramePosition;
    internal int PlaybackWidth => decoder?.Width ?? Player.NaturalVideoWidth;
    internal int PlaybackHeight => decoder?.Height ?? Player.NaturalVideoHeight;
    internal string PlaybackBackend => decoder == null ? "Windows" : "FFmpeg";
    public MainWindow()
    {
        InitializeComponent();
        var desktop = SystemParameters.WorkArea;
        if (desktop.Width > 0 && desktop.Height > 0)
        {
            MinWidth = Math.Min(MinWidth, Math.Max(900, desktop.Width - 24));
            MinHeight = Math.Min(MinHeight, Math.Max(580, desktop.Height - 24));
            Width = Math.Max(MinWidth, Math.Min(Width, desktop.Width - 24));
            Height = Math.Max(MinHeight, Math.Min(Height, desktop.Height - 24));
        }
        DataContext = Model;
        liveMonitor = new(this, CaptureSurface);
        Model.LiveMonitor = liveMonitor;
        Model.CloseMedia += () => { mediaRevision++; decoder?.Dispose(); decoder = null; DecodedFrames.Source = null; DecodedFrames.Visibility = Visibility.Collapsed; Player.Stop(); Player.Close(); Player.Source = null; };
        Model.PlayRequested += play => { if (decoder != null) decoder.SetPlaying(play); else if (play) Player.Play(); else Player.Pause(); };
        Model.SeekRequested += seconds => { if (decoder != null) decoder.Seek(seconds); else if (Player.Source != null) Player.Position = TimeSpan.FromSeconds(seconds); };
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Model.MediaSource)) { Player.Source = Model.MediaSource; if (Player.Source != null) Player.Play(); }
        };
        timer.Tick += (_, _) => { if ((Player.Source != null || decoder != null) && Model.IsPlaying) Model.UpdatePosition(PlaybackPosition); };
        timer.Start();
    }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not (TextBoxBase or ComboBox or ButtonBase or Slider or ListBox or ListBoxItem) && Model.PlayCommand.CanExecute(null))
        { Model.PlayCommand.Execute(null); e.Handled = true; }
    }
    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (decoder != null) return;
        if (Player.Source != null && Player.Source == Model.MediaSource && Player.NaturalDuration.HasTimeSpan) Model.MediaOpened(Player.NaturalDuration.TimeSpan.TotalSeconds);
        if (!Model.IsPlaying) Player.Pause();
    }
    private async void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (decoder != null || Model.MediaSource == null || closeRequested) return;
        long version = mediaRevision; var source = Model.MediaSource;
        Player.Close(); Player.Source = null;
        try
        {
            var fallback = new DecodedVideoPlayer(string.IsNullOrWhiteSpace(Model.FfmpegPath) ? null : Model.FfmpegPath); decoder = fallback;
            WriteableBitmap? bitmap = null;
            fallback.Frame += (pixels, position) =>
            {
                if (decoder != fallback || version != mediaRevision) return;
                if (bitmap == null) { bitmap = new WriteableBitmap(fallback.Width, fallback.Height, 96, 96, PixelFormats.Bgra32, null); DecodedFrames.Source = bitmap; DecodedFrames.Visibility = Visibility.Visible; }
                bitmap.WritePixels(new Int32Rect(0, 0, fallback.Width, fallback.Height), pixels, fallback.Width * 4, 0);
                decodedFramePosition = position;
                if (!Model.IsPlaying) Model.UpdatePosition(position);
            };
            fallback.Ended += () => OnMediaEnded(this, new RoutedEventArgs());
            fallback.Failed += error => { if (decoder == fallback && version == mediaRevision) Model.MediaFailed(error.Message); };
            await fallback.OpenAsync(source.LocalPath);
            if (decoder != fallback || version != mediaRevision) return;
            Model.MediaOpened(fallback.Duration); Model.UseDecodedPlayback(e.ErrorException.HResult);
            fallback.SetPlaying(Model.IsPlaying);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (version == mediaRevision) Model.MediaFailed(error.Message); }
    }
    private void OnMediaEnded(object sender, RoutedEventArgs e)
    { if (Model.PlaybackEnded() && Model.IsPlaying) { if (decoder != null) { decoder.Seek(0); decoder.SetPlaying(true); } else { Player.Position = TimeSpan.Zero; Player.Play(); } } }
    private void OnSeekStarted(object sender, DragStartedEventArgs e) => Model.BeginScrub();
    private void OnSeekCompleted(object sender, DragCompletedEventArgs e) => Model.EndScrub();
    private void OnPreviousFrame(object sender, RoutedEventArgs e) { if (Model.CanSeek) Model.Position -= Model.FrameStep; }
    private void OnNextFrame(object sender, RoutedEventArgs e) { if (Model.CanSeek) Model.Position += Model.FrameStep; }
    private void OnSettingsWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer viewer) { viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta / 120d * 48); e.Handled = true; }
    }
    private void OnEffectPanelMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: ContextMenu menu } button) { Model.SetBrowserContext(null); menu.PlacementTarget = button; menu.IsOpen = true; }
    }
    private void OnBrowserExpand(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BrowserEntry entry }) Model.ToggleBrowserNode(entry.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
    }
    private void OnSourceExpand(object sender, RoutedEventArgs e) { if (sender is Button { DataContext: SourceLayerRow row }) Model.ToggleSourceNode(row.Index); e.Handled = true; }
    private void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs e) { if (sender is ListBox list) Model.SelectSourceRows(list.SelectedItems.Cast<SourceLayerRow>()); }
    private void OnSourceKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { list.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape) { list.UnselectAll(); e.Handled = true; }
        else if (e.Key == Key.Space) { var rows = list.SelectedItems.Cast<SourceLayerRow>().Where(r => r.CanToggle).ToArray(); Model.ApplySourceVisibility(rows.Select(r => r.Index), !rows.All(r => r.Active)); e.Handled = true; }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Model.UndoEffectsCommand.Execute(null); e.Handled = true; }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (closeRequested) return;
        closeRequested = true;
        IsEnabled = false;
        Model.Cancel();
        Model.StopPlanning();
        timer.Stop();
        // Finish this Closing event before invoking Close again, even when idle cleanup completes synchronously.
        await Dispatcher.Yield(DispatcherPriority.Background);
        while (Model.Busy) await Task.Delay(50);
        try { await Model.ReleasePreviewAsync(); }
        catch (IOException error) { MessageBox.Show(error.Message, "预览缓存清理", MessageBoxButton.OK, MessageBoxImage.Information); }
        liveMonitor.Dispose();
        closing = true;
        Close();
    }
}
