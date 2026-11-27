using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Wallpaper2Video.Desktop;

internal static class DesktopAcceptance
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task WaitAsync(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(message);
            await Task.Delay(50);
        }
    }
    public static async Task<int> RunAsync(string video, string scene, string directory)
    {
        Directory.CreateDirectory(directory);
        var evidence = new Dictionary<string, object?>();
        MainWindow? window = null;
        string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(video)));
        try
        {
            window = new MainWindow { Title = "Wallpaper2Video · C#自动验收", ShowActivated = false };
            window.Show();
            window.UpdateLayout();
            var model = window.Model;
            var player = (MediaElement)window.FindName("Player");
            await model.LoadAsync(video);
            Require(model.CanRender, "视频加载失败：" + model.Status);
            await WaitAsync(() => player.NaturalVideoWidth > 0 && model.PlayCommand.CanExecute(null), "系统播放器未打开源视频：" + model.Status);
            evidence["sourceMedia"] = $"{player.NaturalVideoWidth}x{player.NaturalVideoHeight}";
            model.PlayCommand.Execute(null);
            await WaitAsync(() => model.Position > .15, "播放器没有前进");
            model.PlayCommand.Execute(null);
            model.Position = 2;
            await Task.Delay(200);
            Require(Math.Abs(player.Position.TotalSeconds - 2) < .5, "播放器定位失败");
            evidence["playPauseSeek"] = true;
            model.SelectedQuality = model.Qualities[0];
            await model.RenderAsync(true);
            Require(model.MediaSource != null && model.Status.StartsWith("预览已生成"), "预览生成失败：" + model.Status);
            string previewPath = model.MediaSource!.LocalPath;
            await WaitAsync(() => player.NaturalVideoWidth == 640 && model.PlayCommand.CanExecute(null), "MP4预览未在系统播放器中打开：" + model.Status);
            evidence["previewMedia"] = $"{player.NaturalVideoWidth}x{player.NaturalVideoHeight}";
            model.SelectedQuality = model.Qualities[2];
            Require(!model.PlayCommand.CanExecute(null) && model.PreviewNotice.Contains("重新生成"), "清晰度变化未使预览过期");
            evidence["qualityInvalidation"] = true;
            string output = Path.Combine(directory, "desktop-loop.mp4");
            model.OutputPath = output;
            await model.RenderAsync(false);
            Require(File.Exists(output) && File.Exists(Path.ChangeExtension(output, ".report.json")), "桌面导出失败：" + model.Status);
            using (var report = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(output, ".report.json"))))
                evidence["exportValidation"] = report.RootElement.GetProperty("validation").Clone();
            string oldFps = model.FpsText;
            model.FpsText = "0";
            Require(!model.PreviewCommand.CanExecute(null) && !model.ExportCommand.CanExecute(null), "非法参数未阻止处理");
            model.FpsText = oldFps;
            await model.LoadAsync(scene);
            Require(model.CanRender && model.Effects.Count > 0, "场景规划或能力提示错误");
            Require(!Directory.Exists(Path.GetDirectoryName(previewPath)), "关闭媒体后预览缓存仍被占用");
            evidence["sceneEffects"] = model.Effects.Count;
            evidence["previewReleased"] = true;
            await model.LoadAsync(video);
            string cancelled = Path.Combine(directory, "cancel-on-close.mp4");
            model.OutputPath = cancelled;
            Task job = model.RenderAsync(false);
            Require(model.Busy, "任务没有进入忙碌状态");
            window.Close();
            await job;
            await WaitAsync(() => !window.IsVisible, "关闭应用未等到任务清理");
            Require(!File.Exists(cancelled) && !File.Exists(Path.ChangeExtension(cancelled, ".report.json")), "关闭应用留下半成品");
            evidence["closeDuringJob"] = true;
            evidence["sourceUnchanged"] = digest == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(video)));
            evidence["passed"] = true;
            return 0;
        }
        catch (Exception e)
        {
            evidence["passed"] = false; evidence["error"] = e.ToString();
            if (window != null) { window.Model.Cancel(); while (window.Model.Busy) await Task.Delay(50); await window.Model.ReleasePreviewAsync(); window.Close(); }
            return 1;
        }
        finally { await File.WriteAllTextAsync(Path.Combine(directory, "desktop-acceptance.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true })); }
    }
}
