using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Desktop;

internal static class WorkspaceNativeAcceptance
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Wait(Func<bool> ready)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(20);
        while (!ready()) { if (DateTime.UtcNow > end) throw new TimeoutException("等待场景或播放器超时"); await Task.Delay(25); }
    }
    private static void Screenshot(Window window, string path)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    public static async Task<int> RunAsync(string source, string directory)
    {
        Directory.CreateDirectory(directory); var results = new Dictionary<string, object?>();
        var window = new MainWindow { ShowActivated = false, Title = "Wallpaper2Video · Miku参数与录制验收" }; var model = window.Model;
        var original = new[] { "scene.pkg", "project.json", "scene.json" }.Select(n => Path.Combine(source, n)).Where(File.Exists).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        try
        {
            window.Show(); await model.LoadAsync(source); Require(model.CanRender, "载入失败：" + model.Status);
            model.SelectedEffect = model.Effects.Single(e => e.Key == "model:5"); Require(model.Phases.Count(p => p.IsSkeletal) == 3, "没有三个独立骨骼参数项");
            model.ToggleEffect("model:5", true); model.DurationText = "90"; model.OverlapText = "1"; await Wait(() => !model.Planning && model.CanRender);
            foreach (var phase in model.Phases.Where(p => p.IsSkeletal)) phase.RateText = "1";
            await Wait(() => !model.Planning && model.CanRender);
            Require(model.Phases.Where(p => p.IsSkeletal).Select(p => p.Cycles).SequenceEqual(new[] { 36, 20, 15 }), "90秒原速次数不正确");
            var phaseRows = model.Phases.ToArray(); var effectRows = model.Effects.ToArray();
            using (var copy = PreparedScene.Create(model.CurrentScenePlan!, model.CurrentSceneSelection!, directory))
            {
                var objects = Array(Parse(copy.Project.Scene!.Value.GetRawText())["objects"]);
                var miku = objects.Single(o => Text(o?["name"]) == "Miku");
                Require(Array(miku?["animationlayers"]).All(l => Math.Abs(Number(l?["rate"]) - 1) < 1e-9), "90秒副本没有以原速对齐三段动画");
            }
            model.Phases[0].RateText = "invalid"; Require(!model.CanRender, "非法速度没有阻止导出");
            model.Phases[0].Cycles = 36; await Wait(() => !model.Planning && model.CanRender);
            Require(double.TryParse(model.Phases[0].RateText, out _), "修正次数后遗留非法速度文本");
            model.DurationText = "6"; model.FpsText = "30"; model.WidthText = "640"; model.HeightText = "360"; model.WarmupText = "0.5";
            await Wait(() => !model.Planning && model.CanRender);
            foreach (var phase in model.Phases.Where(p => p.IsSkeletal)) phase.RateText = "1";
            await Wait(() => !model.Planning && model.CanRender);
            Require(phaseRows.SequenceEqual(model.Phases) && effectRows.SequenceEqual(model.Effects), "重新规划重建了正在编辑的控件行");
            foreach (var p in model.CurrentSceneSelection!.Particles.ToArray()) model.SetParticle(p.Id, "off");
            model.SelectedQuality = model.Qualities[0]; Screenshot(window, Path.Combine(directory, "skeletal-controls.png"));
            bool sawBadge = false, sawRecordingProgress = false, sawExportProgress = false; Task render = model.RenderAsync(true);
            while (!render.IsCompleted)
            {
                if (model.IsRecording)
                {
                    window.UpdateLayout(); var badge = (Border)window.FindName("RecordingBadge");
                    Require(badge.IsVisible && badge.ActualHeight > 20, "录制中的标记不可见");
                    if (!sawBadge) Screenshot(window, Path.Combine(directory, "recording-indicator.png"));
                    sawBadge = true;
                }
                sawRecordingProgress |= model.RecordingProgress > 0; sawExportProgress |= model.ExportProgress > 0;
                await Task.Delay(25);
            }
            await render;
            Require(sawBadge && sawRecordingProgress && sawExportProgress, "录制 / 导出阶段进度没有分别显示");
            Require(!model.IsRecording, "录制结束标记未清除");
            Require(model.MediaSource != null && File.Exists(model.MediaSource.LocalPath), "真实场景预览失败：" + model.Status);
            File.Copy(model.MediaSource!.LocalPath, Path.Combine(directory, "miku-preview.mp4"));
            results["scenePreviewGenerated"] = true; results["recordingBadgeVisible"] = sawBadge;
            await Wait(() => model.PlayCommand.CanExecute(null)); Require(model.MediaDuration == 6, "6秒场景预览总长错误");
            model.PlayCommand.Execute(null); await Wait(() => model.Position > .1);
            model.BeginScrub(); model.Position = 2; model.UpdatePosition(.1); Require(model.Position == 2, "拖动时计时回写"); model.EndScrub();
            await Wait(() => model.Position > 2.1); Require(model.IsPlaying, "拖动后播放没有继续前进"); model.PlayCommand.Execute(null);
            results["previewDuration"] = model.MediaDuration; results["independentSkeletonControls"] = true; results["rawCommonPeriod90s"] = true;
            results["recordingBadgeVisible"] = sawBadge; results["separateProgress"] = true; results["scrubResumesAndAdvances"] = true;
            model.OutputPath = Path.Combine(directory, "miku-export.mp4"); await model.RenderAsync(false);
            Require(File.Exists(model.OutputPath), "真实场景导出失败：" + model.Status);
            using var report = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(model.OutputPath, ".report.json")));
            Require(report.RootElement.GetProperty("source_window_closed").GetBoolean(), "录制窗口未关闭");
            results["frames"] = report.RootElement.GetProperty("validation").GetProperty("frames").GetInt32();
            Require((int)results["frames"]! == 180, "6秒30fps不是180帧");
            Require(original.All(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value), "修改了原始Steam素材");
            results["sourceUnchanged"] = true; results["passed"] = true; return 0;
        }
        catch (Exception e) { results["passed"] = false; results["error"] = e.ToString(); return 1; }
        finally
        {
            model.Cancel(); model.StopPlanning(); while (model.Busy) await Task.Delay(50); window.Close(); while (window.IsVisible) await Task.Delay(25);
            await File.WriteAllTextAsync(Path.Combine(directory, "native-regressions.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
