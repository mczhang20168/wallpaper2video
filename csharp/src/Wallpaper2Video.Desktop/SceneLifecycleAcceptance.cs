using System.IO;
using System.Text.Json;
using System.Windows;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Desktop;

internal static class SceneLifecycleAcceptance
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Wait(Func<bool> condition, string message, int seconds = 15)
    { var end = DateTime.UtcNow.AddSeconds(seconds); while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException(message); await Task.Delay(50); } }
    private sealed class FailedMonitor : ILiveMonitor
    {
        public nint Source;
        public (int X, int Y) Position => NativeWindows.OffscreenPosition();
        public Task AttachAsync(nint source, int width, int height, CancellationToken cancellation) { Source = source; throw new IOException("验收注入：监视器挂接失败"); }
        public Task DetachAsync(nint source) => Task.CompletedTask;
    }
    public static async Task<int> RunAsync(string source, string directory)
    {
        Directory.CreateDirectory(directory);
        var evidence = new Dictionary<string, object?>();
        var window = new MainWindow { ShowActivated = false, Title = "Wallpaper2Video · 场景边界验收" };
        try
        {
            window.Show(); var model = window.Model; await model.LoadAsync(source);
            model.DurationText = "20"; model.OverlapText = "2"; model.WidthText = "640"; model.HeightText = "320"; model.WarmupText = "0.5";
            await Wait(() => !model.Planning && model.CanRender, "场景配置未就绪：" + model.Status);
            model.SelectedEffect = model.Effects.First(e => e.Key == "particle:1");
            model.SetParticle("particle:1", "drain", 20, 5);
            Require(Truth(model.CurrentScenePlan!.ParticlePlan.Configure(model.CurrentSceneSelection!.Particles).Single(r => Text(r["id"]) == "418")["emission_disabled"]), "反向窗口应不发射");
            model.SetParticle("particle:1", "drain", 1, 20);
            Require(model.ParticleWarning.Contains("不保证闭合"), "晚停发未显示风险");
            model.ParticleStartText = "NaN"; await Task.Delay(150); Require(!model.CanRender && double.IsFinite(model.ParticleStart), "非法粒子输入未禁用导出或进入渲染坐标");
            model.ParticleStartText = "2"; Require(model.CanRender, "修正粒子输入后未恢复");
            model.SetParticle("particle:1", "off"); model.SetParticle("particle:1", "drain");
            Require(model.CurrentSceneSelection!.Particles.Single(p => p.Id == "particle:1").Start == 2, "粒子模式切换丢失已设置时段");
            model.SelectedEffect = model.Effects.First(e => e.Key == "effect:0:0");
            model.Phases[0].CycleText = "0"; Require(!model.CanRender, "非法循环次数未禁用导出");
            model.Phases[0].CycleText = "3"; await Wait(() => !model.Planning && model.CanRender, "修正循环次数后未恢复");
            evidence["editorBoundaries"] = true;
            model.DurationText = "21"; await Task.Delay(310); model.ToggleEffect("effect:0:0", false);
            await Wait(() => !model.Planning && model.CanRender, "快速编辑后规划未完成");
            Require(model.CurrentSceneSelection!.DisabledEffects.Contains("effect:0:0"), "异步规划覆盖了较新的效果选择");
            model.ToggleEffect("effect:0:0", true); evidence["planningPreservesNewerEdits"] = true;
            foreach (var particle in model.CurrentSceneSelection!.Particles.ToArray()) model.SetParticle(particle.Id, "off");
            model.DurationText = "6"; model.OverlapText = "1";
            await Wait(() => !model.Planning && model.CanRender, "短视频配置未就绪");
            var failed = new FailedMonitor(); model.LiveMonitor = failed; model.OutputPath = Path.Combine(directory, "monitor-failure.mp4");
            await model.RenderAsync(false);
            Require(model.Status.Contains("监视器挂接失败") && failed.Source != 0 && !NativeWindows.IsWindow(failed.Source), "挂接失败没有关闭本任务源窗口：" + model.Status);
            Require(!File.Exists(model.OutputPath) && !File.Exists(Path.ChangeExtension(model.OutputPath, ".report.json")), "失败留下半成品");
            evidence["monitorFailureCleanup"] = true;
            var real = new DwmLiveMonitor(window, (FrameworkElement)window.FindName("CaptureSurface"));
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); bool rejected = false;
                try { await real.AttachAsync(1, 640, 320, cancel.Token); } catch (OperationCanceledException) { rejected = true; }
                Require(rejected && !real.Attached, "取消后的迟到挂接未被拒绝");
            }
            real.Dispose(); evidence["lateAttachRejected"] = true;
            window.Close(); await Wait(() => !window.IsVisible, "主窗口未关闭");
            evidence["closeAfterCleanup"] = true;
            evidence["passed"] = true; return 0;
        }
        catch (Exception error)
        {
            evidence["passed"] = false; evidence["error"] = error.ToString();
            window.Model.Cancel(); while (window.Model.Busy) await Task.Delay(50); window.Close(); return 1;
        }
        finally { await File.WriteAllTextAsync(Path.Combine(directory, "scene-lifecycle.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true })); }
    }
}
