using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wallpaper2Video.Desktop;

internal static class PlaybackAcceptance
{
    public static async Task<int> RunAsync(string video, string directory)
    {
        Directory.CreateDirectory(directory);
        var result = new Dictionary<string, object?>(); var samples = new List<object>();
        var window = new MainWindow { Title = "Wallpaper2Video · 播放器实机验收" }; var model = window.Model;
        var player = (MediaElement)window.FindName("Player");
        int opened = 0, ended = 0; string? mediaError = null;
        player.MediaOpened += (_, _) => opened++;
        player.MediaEnded += (_, _) => ended++;
        player.MediaFailed += (_, e) => mediaError = e.ErrorException.ToString();
        void Sample(string stage) => samples.Add(new
        {
            stage,
            opened,
            ended,
            mediaError,
            model.Position,
            model.IsPlaying,
            model.Status,
            model.CanSeek,
            playerPosition = window.PlaybackPosition,
            window.DisplayedFramePosition,
            window.PlaybackWidth,
            window.PlaybackHeight,
            window.PlaybackBackend,
            player.IsVisible,
            source = player.Source?.ToString(),
            modelSource = model.MediaSource?.ToString()
        });
        async Task Wait(Func<bool> predicate, string stage, double seconds = 8)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (!predicate())
            {
                if ((mediaError != null && window.PlaybackBackend == "Windows") || DateTime.UtcNow > end) { Sample(stage + " timeout"); throw new InvalidOperationException(stage + ": " + (mediaError ?? model.Status)); }
                await Task.Delay(30);
            }
            Sample(stage);
        }
        try
        {
            window.Show(); window.UpdateLayout(); await model.LoadAsync(video); Sample("loaded");
            await Wait(() => model.PlayCommand.CanExecute(null), "media opened");
            model.PlayCommand.Execute(null); await Wait(() => model.Position > .1, "playback advances");
            var timeline = Visuals<PlayerTimeline>(window).Single();
            timeline.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            timeline.Value = 2; await Wait(() => Math.Abs(window.DisplayedFramePosition - 2) < .1, "scrub frame decoded"); Sample("during scrub");
            timeline.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            await Wait(() => model.Position > 2.2 && model.IsPlaying, "scrub resumes");
            model.PlayCommand.Execute(null); timeline.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            timeline.Value = 1; await Task.Delay(120); timeline.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            await Task.Delay(200); Sample("paused scrub");
            await Wait(() => Math.Abs(window.DisplayedFramePosition - 1) < .1, "paused frame decoded");
            if (model.IsPlaying) throw new InvalidOperationException("暂停时拖动改变了播放状态");
            timeline.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            foreach (double target in new[] { .5, 4, 1.5, 3.5, 2.5 }) { timeline.Value = target; await Task.Delay(15); }
            var response = Stopwatch.StartNew();
            timeline.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            await Wait(() => Math.Abs(window.DisplayedFramePosition - 2.5) < .1, "rapid scrub latest frame");
            result["lastSeekResponseMilliseconds"] = response.Elapsed.TotalMilliseconds;
            if (model.IsPlaying) throw new InvalidOperationException("连续拖动改变了暂停状态");
            model.Position = model.MediaDuration; await Wait(() => window.DisplayedFramePosition > model.MediaDuration - .15, "last frame decoded");
            model.Position = 1; await Wait(() => Math.Abs(window.DisplayedFramePosition - 1) < .1, "return to first second");
            window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(directory, "playback.png"))) encoder.Save(file);
            result["duration"] = model.MediaDuration; result["backend"] = window.PlaybackBackend;
            model.PlayCommand.Execute(null); await Wait(() => model.Position > 1.1, "play before close");
            window.Close(); var closeDeadline = DateTime.UtcNow.AddSeconds(5); while (window.IsVisible && DateTime.UtcNow < closeDeadline) await Task.Delay(30);
            if (window.IsVisible) throw new InvalidOperationException("播放中关窗没有完成清理");
            result["closeDuringPlaybackClean"] = true; result["passed"] = true; return 0;
        }
        catch (Exception error) { result["passed"] = false; result["error"] = error.ToString(); return 1; }
        finally
        {
            Sample("finished"); result["samples"] = samples;
            await File.WriteAllTextAsync(Path.Combine(directory, "playback-acceptance.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            model.Cancel(); model.StopPlanning(); while (model.Busy) await Task.Delay(30); if (window.IsVisible) window.Close(); while (window.IsVisible) await Task.Delay(30);
        }
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }
}
