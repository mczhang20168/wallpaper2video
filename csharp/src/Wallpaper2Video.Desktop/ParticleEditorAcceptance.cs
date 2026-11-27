using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Desktop;

internal static class ParticleEditorAcceptance
{
    internal static async Task<int> RunAsync(string source, string directory, bool shootingStar = false)
    {
        Directory.CreateDirectory(directory); var result = new Dictionary<string, object?>();
        var originals = new[] { "project.json", "scene.json", "scene.pkg" }.Select(n => Path.Combine(source, n)).Where(File.Exists).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var window = new MainWindow { Title = "Wallpaper2Video · 粒子预览后编辑验收", ShowActivated = false }; var model = window.Model;
        async Task Wait(Func<bool> predicate, string reason)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!predicate()) { if (DateTime.UtcNow > until) throw new TimeoutException(reason + "：" + model.Status); await Task.Delay(40); }
        }
        void Require(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
        try
        {
            window.Show(); await model.LoadAsync(source); Require(model.CanRender, "素材无法预览：" + model.Status);
            model.WidthText = "640"; model.HeightText = "360"; model.FpsText = "12"; model.OverlapText = "1"; model.SelectedQuality = model.Qualities[0];
            await Wait(() => !model.Planning && model.CanRender, "等待场景规划");
            var control = model.CurrentScenePlan!.ParticleControls().First(c => c.CanOriginal && (!shootingStar || c.Label.StartsWith("Shooting_Star_01", StringComparison.Ordinal)));
            string? parent = shootingStar ? model.CurrentScenePlan.ParticleControls().First(c => c.Label.StartsWith("Blinking Stars_01", StringComparison.Ordinal)).Id : null;
            foreach (var p in model.CurrentSceneSelection!.Particles.Where(p => p.Id != control.Id && p.Id != parent).ToArray()) model.SetParticle(p.Id, "off");
            if (parent != null) model.SetParticle(parent, "original");
            model.SetEffectSelection([control.Id]); model.BrowserGrouping = "按对象";
            if (shootingStar)
            {
                // Exercise the new source panel through its actual eye control: no sound
                // layer in either preview, while fixed-rate shooting stars remain enabled.
                var sound = model.SourceRows.First(r => r.Icon == "sound" && r.CanToggle); model.ShowingSources = true; model.SourceSearch = sound.Label;
                window.UpdateLayout(); await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle); window.UpdateLayout();
                var list = (ListBox)window.FindName("SourceLayerList"); list.SelectedItem = sound;
                IEnumerable<EffectCheckBox> Eyes(DependencyObject node)
                {
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { var child = VisualTreeHelper.GetChild(node, i); if (child is EffectCheckBox eye) yield return eye; foreach (var nested in Eyes(child)) yield return nested; }
                }
                var eye = Eyes(list).Single(c => c.DataContext is SourceLayerRow row && row.Index == sound.Index);
                typeof(ToggleButton).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(eye, null);
                Require(model.CurrentSceneSelection!.SourceVisibility.GetValueOrDefault(sound.Index, true) == false, "原素材面板未关闭音乐层");
                result["sourceSoundHidden"] = true; model.SourceSearch = ""; model.ShowingSources = false;
            }
            await model.RenderAsync(true); Require(model.MediaSource != null, "初次循环预览失败：" + model.Status);
            await Wait(() => model.CanSeek, "初次预览未就绪"); Require(model.CanEdit, "预览完成后仍锁定编辑");
            result["previewThenEdit"] = true;
            var mode = (ComboBox)window.FindName("ParticleModeInput"); mode.SetCurrentValue(Selector.SelectedItemProperty, model.ParticleModes.Single(m => m.Code == "drain"));
            mode.GetBindingExpression(Selector.SelectedItemProperty)!.UpdateSource();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            var start = (TextBox)window.FindName("ParticleStartInput"); var stop = (TextBox)window.FindName("ParticleStopInput");
            Require(start.IsEnabled && stop.IsEnabled && model.CanSetParticleWindow, "预览后时间框仍无法编辑");
            start.SetCurrentValue(TextBox.TextProperty, "2"); start.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            stop.SetCurrentValue(TextBox.TextProperty, "8"); stop.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            var choice = model.CurrentSceneSelection!.Particles.Single(p => p.Id == control.Id);
            Require(choice is { Mode: "drain", Start: 2, Stop: 8 }, "实际输入没有保存到粒子设置");
            result["particle"] = choice; result["controlsEditableAfterPreview"] = true;
            if (parent != null) result["parentNaturalPlayback"] = model.CurrentSceneSelection.Particles.Single(p => p.Id == parent).Mode == "original";
            model.SearchText = control.Label; window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(directory, "particle-controls.png"))) encoder.Save(file);
            using (var copy = PreparedScene.Create(model.CurrentScenePlan!, model.CurrentSceneSelection, directory))
            {
                var rows = Array(copy.ParticleManifest!["managed_layers"]);
                Require(rows.Count == 1 && Number(rows[0]?["video_emit_start"]) == 2 && Number(rows[0]?["video_emit_stop"]) == 8, "实际导出副本时段错误");
                Require(!Truth(rows[0]?["audio_emission"]), "定时副本仍依赖音频"); result["audioResponseDisabled"] = true;
            }
            model.SearchText = ""; await model.RenderAsync(true); Require(model.MediaSource != null, "修改后重新生成预览失败：" + model.Status);
            await Wait(() => model.CanSeek, "修改后预览未就绪");
            Require(model.CurrentSceneSelection!.Particles.Single(p => p.Id == control.Id) is { Mode: "drain", Start: 2, Stop: 8 }, "重新生成后丢失时段");
            File.Copy(model.MediaSource!.LocalPath, Path.Combine(directory, "timed-preview.mp4"));
            string previewReport = Path.ChangeExtension(model.MediaSource.LocalPath, ".report.json");
            File.Copy(previewReport, Path.Combine(directory, "timed-preview.report.json"));
            using (var report = JsonDocument.Parse(File.ReadAllText(previewReport)))
            {
                int maxPath = report.RootElement.GetProperty("engine_project_max_path_length").GetInt32();
                Require(maxPath < 260, "引擎副本仍有超过原生路径限制的资源"); result["engineProjectMaxPathLength"] = maxPath;
            }
            Require(originals.All(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value), "预览改动了原始壁纸文件");
            result["sourceUnchanged"] = true; result["regeneratedPreview"] = true; result["passed"] = true; return 0;
        }
        catch (Exception error) { result["passed"] = false; result["error"] = error.ToString(); return 1; }
        finally
        {
            model.Cancel(); model.StopPlanning(); while (model.Busy) await Task.Delay(40); window.Close(); while (window.IsVisible) await Task.Delay(40);
            await File.WriteAllTextAsync(Path.Combine(directory, "particle-editor-acceptance.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
