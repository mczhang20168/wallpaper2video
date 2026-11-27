using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wallpaper2Video.Runtime.Windows;

namespace Wallpaper2Video.Desktop;

/// <summary>Exercises scene loading and the real poster binding, without starting WPE.</summary>
internal static class SceneAcceptance
{
    public static async Task<int> RunAsync(string source, string comparison, string output)
    {
        using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(comparison));
        var row = expected.RootElement.GetProperty("projects").EnumerateArray().Single(p =>
            Path.GetFullPath(p.GetProperty("source").GetString()!).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase));
        string[] ids = row.GetProperty("workspace_ids").EnumerateArray().Select(v => v.GetString()!).ToArray();
        var evidence = new Dictionary<string, object?>();
        evidence["runtimeDirectory"] = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var sourceSnapshot = ProjectLoader.Load(source);
        Dictionary<string, string> Hashes() => new[] { sourceSnapshot.PackagePath, Path.Combine(sourceSnapshot.Root, "project.json") }
            .Where(p => p != null && File.Exists(p)).ToDictionary(p => p!, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p!))));
        var before = Hashes();
        var window = new MainWindow { ShowActivated = false, Title = "Wallpaper2Video · 场景加载验收" };
        string parent = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(parent);
        string work = SafeFiles.NewWorkDirectory(parent);
        try
        {
            window.Show();
            await window.Model.LoadAsync(source);
            window.UpdateLayout();
            var model = window.Model;
            var snapshot = ProjectLoader.Load(source);
            evidence["targetInventoryMatches"] = model.Effects.Select(e => e.Key).SequenceEqual(ids);
            evidence["actualRows"] = model.Effects.Count;
            evidence["expectedRows"] = ids.Length;
            var poster = window.FindName("Poster") as Image;
            evidence["posterFileExists"] = snapshot.PreviewPath != null;
            evidence["posterDisplayed"] = poster?.Source?.Width > 0 && poster.IsVisible;
            evidence["posterRequired"] = snapshot.PreviewPath != null;
            evidence["sceneRenderingAvailable"] = model.PreviewCommand.CanExecute(null);
            evidence["sceneExportAvailable"] = model.ExportCommand.CanExecute(null);
            evidence["status"] = model.Status;
            evidence["notice"] = model.PreviewNotice;
            evidence["sourceHashes"] = Hashes();
            evidence["sourceUnchanged"] = before.OrderBy(p => p.Key).SequenceEqual(Hashes().OrderBy(p => p.Key));
            bool passed = model.CanRender && (bool)evidence["targetInventoryMatches"]! &&
                          (snapshot.PreviewPath == null || (bool)evidence["posterDisplayed"]!);

            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.ChangeExtension(output, ".png"))) encoder.Save(file);

            if (snapshot.PreviewPath != null)
            {
                using var exclusive = new FileStream(snapshot.PreviewPath, FileMode.Open, FileAccess.Read, FileShare.None);
                evidence["posterFileReleased"] = exclusive.Length > 0;
            }

            // Switching source must clear the previous poster, row selection and player state.
            await File.WriteAllTextAsync(Path.Combine(work, "project.json"), """
                {"type":"Scene","file":"scene.json","preview":"missing.png","title":"封面回归"}
                """);
            await File.WriteAllTextAsync(Path.Combine(work, "scene.json"), """
                {"objects":[{"particle":"particles/test.json"}]}
                """);
            await model.LoadAsync(work);
            window.UpdateLayout();
            bool noPoster = model.Effects.Count == 1 && poster?.Source == null && model.MediaSource == null && model.MediaDuration == 0;
            evidence["missingPosterClearsPrevious"] = noPoster;
            await File.WriteAllTextAsync(Path.Combine(work, "missing.png"), "invalid image");
            await model.LoadAsync(work);
            window.UpdateLayout();
            bool corruptPoster = model.Effects.Count == 1 && poster?.Source == null && model.Title == "封面回归";
            evidence["corruptPosterDoesNotBlockLoad"] = corruptPoster;
            model.SelectedEffect = model.Effects.FirstOrDefault();
            await model.LoadAsync(Path.Combine(work, "not-a-source"));
            window.UpdateLayout();
            bool failedLoad = model.Effects.Count == 0 && model.SelectedEffect == null && poster?.Source == null &&
                              model.MediaSource == null && !model.CanRender;
            evidence["failedLoadClearsPrevious"] = failedLoad;
            passed &= noPoster && corruptPoster && failedLoad && (bool)evidence["sourceUnchanged"]!;
            evidence["scope"] = "target inventory and poster binding; scene rendering is reported separately";
            evidence["passed"] = passed;
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            evidence["passed"] = false;
            evidence["error"] = error.ToString();
            return 1;
        }
        finally
        {
            await window.Model.ReleasePreviewAsync();
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            evidence["idleClosePassed"] = true;
            SafeFiles.DeleteWorkDirectory(work, parent);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
