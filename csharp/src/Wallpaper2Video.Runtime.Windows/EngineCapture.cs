using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wallpaper2Video.Core;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

public sealed record CaptureResult(nint SourceWindow, int Width, int Height, bool Workspace, JsonObject? ParticleValidation);

public static class EngineCapture
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static string FindEngine(ProjectSnapshot project, string? explicitPath = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("场景录制需要 Windows 和 Wallpaper Engine");
        string binary = Process.GetProcessesByName("wallpaper32").Length > 0 ? "wallpaper32.exe" : "wallpaper64.exe";
        string? configured = string.IsNullOrWhiteSpace(explicitPath) ? Environment.GetEnvironmentVariable("WPE_EXE") : explicitPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string path = Directory.Exists(configured) ? Path.Combine(configured, binary) : configured;
            if (File.Exists(path)) return Path.GetFullPath(path);
            throw new FileNotFoundException("Wallpaper Engine 路径不存在：" + path);
        }
        var roots = new List<string>();
        string? assets = AssetBundle.FindAssets(project, null);
        if (assets != null) roots.Add(Path.GetDirectoryName(assets)!);
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            if (key?.GetValue("SteamPath") is string steam) roots.Add(Path.Combine(steam, "steamapps", "common", "wallpaper_engine"));
        foreach (string root in roots) foreach (string name in new[] { binary, "wallpaper64.exe", "wallpaper32.exe" })
            if (File.Exists(Path.Combine(root, name))) return Path.Combine(root, name);
        throw new FileNotFoundException("找不到 Wallpaper Engine，请在高级设置中指定安装目录或 exe");
    }
    public static async Task CommandAsync(string engine, IEnumerable<string> arguments, CancellationToken cancellation = default)
    {
        using var process = StartEngine(engine);
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        Task<string> output = ToolProcess.DrainAsync(process.StandardOutput), errors = ToolProcess.DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(20), cancellation);
            await Task.WhenAll(output, errors);
            if (process.ExitCode != 0) throw new IOException("Wallpaper Engine 命令失败：" + process.ExitCode);
        }
        finally { await ToolProcess.StopAsync(process); await Task.WhenAll(output, errors); }
    }
    private static Process StartEngine(string engine) => new()
    {
        StartInfo = new(engine)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(engine)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }
    };
    public static string QuoteWindows(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static async Task PropertiesAsync(string engine, string title, JsonObject values, CancellationToken cancellation = default)
    {
        using var process = StartEngine(engine);
        process.StartInfo.Arguments = "-control applyProperties -location " + QuoteWindows(title) + " -properties RAW~(" + values.ToJsonString() + ")~END";
        process.Start();
        Task<string> output = ToolProcess.DrainAsync(process.StandardOutput), errors = ToolProcess.DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(15), cancellation);
            await Task.WhenAll(output, errors);
            if (process.ExitCode != 0) throw new IOException("无法同步指定壁纸窗口的粒子控制：" + process.ExitCode);
        }
        finally { await ToolProcess.StopAsync(process); await Task.WhenAll(output, errors); }
    }
    public static async Task<CaptureResult> CaptureAsync(PreparedScene prepared, string output, ExportSettings settings, string ffmpeg,
        string engine, IProgress<RenderProgress>? progress = null, ILiveMonitor? monitor = null, CancellationToken cancellation = default,
        double? rawDuration = null)
    {
        await Gate.WaitAsync(cancellation);
        string title = "Wallpaper2Video-" + Guid.NewGuid().ToString("N")[..12];
        nint hwnd = 0;
        bool opened = false;
        Task? capture = null, guard = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var guardStop = new CancellationTokenSource();
        Exception? guardError = null;
        try
        {
            // Control commands require an already initialized WPE process. Starting a
            // bare renderer after a crash can leave it in the reentrant crash handler.
            string processName = Path.GetFileNameWithoutExtension(engine);
            if (Process.GetProcessesByName(processName).Length == 0)
                throw new IOException("Wallpaper Engine 未运行。请先从 Steam 正常启动 Wallpaper Engine，再生成预览或导出。");
            await EmbeddedVideoPlayback.PrepareAsync(prepared, ffmpeg, progress, cancellation);
            string capabilities = await ToolProcess.RunAsync(ffmpeg, ["-hide_banner", "-h", "filter=gfxcapture"], cancellation);
            if (!capabilities.Contains("gfxcapture AVOptions", StringComparison.Ordinal)) throw new NotSupportedException("当前 FFmpeg 缺少 gfxcapture，请使用 FFmpeg 8+ 完整版");
            string log = Path.Combine(Path.GetDirectoryName(engine)!, "log.txt");
            long offset = File.Exists(log) ? new FileInfo(log).Length : 0;
            JsonObject? manifest = prepared.ParticleManifest;
            List<JsonObject> Events()
            {
                if (manifest == null || !File.Exists(log)) return [];
                using var file = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length >= offset) file.Position = offset;
                using var reader = new StreamReader(file, Encoding.UTF8);
                string prefix = Text(manifest["telemetry_prefix"]);
                var result = new List<JsonObject>();
                foreach (string line in reader.ReadToEnd().Split('\n'))
                {
                    int start = line.IndexOf(prefix, StringComparison.Ordinal);
                    if (start < 0) continue;
                    try { result.Add(Parse(line[(start + prefix.Length)..])); } catch (JsonException) { }
                }
                return result;
            }
            async Task WaitStage(string stage)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed.TotalSeconds < 10 + Number(manifest?["prewarm_seconds"], 0))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var events = Events();
                    var failure = events.LastOrDefault(e => Text(e["stage"]) == "initialization_error");
                    if (failure != null) throw new IOException("原生播放初始化失败：" + Text(failure["error"]));
                    if (events.Any(e => Text(e["stage"]) == stage)) return;
                    await Task.Delay(50, linked.Token);
                }
                throw new IOException("未收到原生粒子状态确认：" + stage);
            }
            progress?.Report(new(monitor == null ? "打开独立壁纸渲染窗口…" : "准备工作区实时录制…", 0));
            var position = monitor?.Position ?? (50, 50);
            var arguments = new List<string> { "-control", "openWallpaper", "-file", prepared.Project.Entry, "-playInWindow", title,
                "-width", settings.Width.ToString(CultureInfo.InvariantCulture), "-height", settings.Height.ToString(CultureInfo.InvariantCulture),
                "-x", position.Item1.ToString(CultureInfo.InvariantCulture), "-y", position.Item2.ToString(CultureInfo.InvariantCulture) };
            if (monitor != null) arguments.Add("-borderless");
            opened = true;
            await CommandAsync(engine, arguments, cancellation);
            var waiting = Stopwatch.StartNew();
            while (hwnd == 0 && waiting.Elapsed.TotalSeconds < 30)
            {
                cancellation.ThrowIfCancellationRequested(); hwnd = NativeWindows.FindWindowW(null, title);
                if (hwnd == 0) await Task.Delay(100, cancellation);
            }
            if (hwnd == 0) throw new IOException("找不到壁纸任务窗口，请确认引擎权限与本应用相同");
            if (monitor != null) await monitor.AttachAsync(hwnd, settings.Width, settings.Height, cancellation);
            else NativeWindows.ResizeClient(hwnd, settings.Width, settings.Height, 50, 50);
            if (manifest != null) { progress?.Report(new($"准备粒子预热与播放时钟（最长 {Number(manifest["prewarm_seconds"], 0):g} 秒）…", 0)); await PropertiesAsync(engine, title, new() { ["rate"] = 100, [Text(manifest["property"])] = 0 }, cancellation); await WaitStage("ready"); }
            if (settings.AutomaticStartupWait)
            {
                progress?.Report(new("等待引擎连续首帧就绪…", 0));
                await ToolProcess.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-filter_complex",
                    $"gfxcapture=hwnd={hwnd}:capture_cursor=0:capture_border=0:max_framerate={settings.Fps},hwdownload,format=bgra[v]",
                    "-map", "[v]", "-an", "-frames:v", "3", "-f", "null", "-"], cancellation, 20);
            }
            else
            {
                progress?.Report(new($"录制前启动等待 {settings.Warmup:g} 秒…", 0));
                await Task.Delay(TimeSpan.FromSeconds(settings.Warmup), cancellation);
            }
            long frames = rawDuration.HasValue ? FrameMath.Quantize(rawDuration.Value, settings.Fps) : settings.Frames + settings.FadeFrames;
            double duration = (double)frames / settings.Fps;
            string graph = $"gfxcapture=hwnd={hwnd}:capture_cursor=0:capture_border=0:max_framerate={settings.Fps},hwdownload,format=bgra,setpts=PTS-STARTPTS,fps={settings.Fps}," +
                $"scale={settings.Width}:{settings.Height}:force_original_aspect_ratio=decrease,pad={settings.Width}:{settings.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p[v]";
            var firstFrame = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            progress?.Report(new($"录制 {duration:g} 秒…", 0, RenderActivity.Capturing, 0));
            capture = ToolProcess.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "warning", "-nostdin", "-y", "-stats_period", "0.1", "-progress", "pipe:1", "-nostats", "-filter_complex", graph,
                "-map", "[v]", "-an", "-frames:v", frames.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-crf", "14", "-pix_fmt", "yuv420p", output],
                linked.Token, Math.Max(90, duration * 3 + 30), line =>
                {
                    if (line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line[6..].Trim(), out long count) && count > 0) firstFrame.TrySetResult(count);
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], out long micros)) progress?.Report(new($"录制 {duration:g} 秒…", Math.Clamp(micros / 1e6 / duration, 0, 1), RenderActivity.Capturing, Math.Clamp(micros / 1e6 / duration, 0, 1)));
                });
            guard = Task.Run(async () =>
            {
                try
                {
                    while (!guardStop.IsCancellationRequested)
                    {
                        if (monitor?.Failure != null) { guardError = monitor.Failure; linked.Cancel(); return; }
                        string? interrupted = !NativeWindows.IsWindow(hwnd) ? "录制源窗口已关闭" :
                            NativeWindows.IsIconic(hwnd) ? "录制源窗口已最小化" :
                            !NativeWindows.DesktopAvailable() ? "当前会话无法访问交互桌面" : null;
                        if (interrupted != null)
                        {
                            guardError = new IOException(interrupted + "，已停止本次任务");
                            linked.Cancel();
                            return;
                        }
                        await Task.Delay(200, guardStop.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
            JsonObject? synchronization = null;
            if (manifest != null)
            {
                await Task.WhenAny(firstFrame.Task, capture).WaitAsync(TimeSpan.FromSeconds(30), cancellation);
                if (capture.IsCompleted && !firstFrame.Task.IsCompleted) { await capture; throw new IOException("捕获未收到实际首帧"); }
                long count = await firstFrame.Task;
                var arm = Stopwatch.StartNew();
                await PropertiesAsync(engine, title, new() { ["rate"] = 100, [Text(manifest["property"]) + "_offset"] = (double)count / settings.Fps, [Text(manifest["property"])] = 1 }, cancellation);
                await WaitStage("armed");
                double latency = (double)count / settings.Fps + arm.Elapsed.TotalSeconds;
                if (latency > ParticlePlan.ArmBudget) throw new IOException($"粒子首帧同步超过安全预算（{latency:F3}秒），请重试或关闭粒子");
                synchronization = new() { ["first_progress_frame"] = count, ["arm_latency_upper_estimate"] = latency, ["budget_seconds"] = ParticlePlan.ArmBudget, ["window_scoped"] = true };
            }
            try { await capture; } catch (OperationCanceledException) when (guardError != null) { throw guardError; }
            progress?.Report(new("录制完成，正在核验…", 1, RenderActivity.Validating, 1));
            JsonObject? validation = null;
            if (manifest != null)
            {
                await PropertiesAsync(engine, title, new() { ["rate"] = 100, [Text(manifest["property"])] = 2 }, cancellation);
                await WaitStage("verify");
                validation = ParticlePlanner.ValidateTelemetry(Events(), Array(manifest["managed_layers"]).Select(r => r!.AsObject()).ToList(), (double)settings.FadeFrames / settings.Fps);
                validation["plan"] = manifest.DeepClone(); validation["synchronization"] = synchronization;
            }
            var actualSize = NativeWindows.Size(hwnd);
            return new(hwnd, actualSize.Width, actualSize.Height, monitor != null, validation);
        }
        finally
        {
            linked.Cancel(); guardStop.Cancel();
            if (capture != null) try { await capture; } catch (Exception) { }
            if (guard != null) await guard;
            if (monitor != null && hwnd != 0) try { await monitor.DetachAsync(hwnd); } catch (Exception) { }
            if (opened) try { await CommandAsync(engine, ["-control", "closeWallpaper", "-location", title]); } catch (Exception) { }
            if (hwnd != 0 && NativeWindows.IsWindow(hwnd))
            {
                NativeWindows.PostMessageW(hwnd, 0x0010, 0, 0);
                for (int i = 0; i < 40 && NativeWindows.IsWindow(hwnd); i++) await Task.Delay(50);
            }
            Gate.Release();
            if (hwnd != 0 && NativeWindows.IsWindow(hwnd)) throw new IOException("本任务的壁纸窗口未能关闭，已停止发布成片");
        }
    }
}
