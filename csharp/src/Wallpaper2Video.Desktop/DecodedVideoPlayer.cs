using System.Diagnostics;
using System.Globalization;
using System.IO;
using Wallpaper2Video.Runtime.Windows;

namespace Wallpaper2Video.Desktop;

/// <summary>Silent video fallback when the Windows media graph cannot initialize its audio device.</summary>
internal sealed class DecodedVideoPlayer : IDisposable
{
    private readonly string ffmpeg;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operationStop;
    private Process? process;
    private Task? pump;
    private string source = "";
    private bool playing, pendingSeek, continuous, disposed;
    private long revision;
    private double fps = 30;
    private TaskCompletionSource<bool>? firstFrame;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double Duration { get; private set; }
    public double Position { get; private set; }
    public event Action<byte[], double>? Frame;
    public event Action? Ended;
    public event Action<Exception>? Failed;
    public DecodedVideoPlayer(string? ffmpegPath) { ffmpeg = ToolProcess.Find("ffmpeg", ffmpegPath); }
    public async Task OpenAsync(string path)
    {
        source = path;
        var probe = await new VideoRenderer(ffmpeg).ProbeAsync(path, lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        if (probe.Duration <= 0 || probe.Width <= 0 || probe.Height <= 0) throw new InvalidDataException("无法读取视频尺寸或时长");
        Duration = probe.Duration;
        double scale = Math.Min(1, Math.Min(1280d / probe.Width, 1280d / probe.Height));
        Width = Math.Max(2, (int)Math.Round(probe.Width * scale / 2) * 2); Height = Math.Max(2, (int)Math.Round(probe.Height * scale / 2) * 2);
        string[] rate = probe.Fps.Split('/');
        if (double.TryParse(rate[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator))
        {
            double denominator = rate.Length > 1 && double.TryParse(rate[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double divisor) ? divisor : 1;
            if (denominator > 0 && numerator > 0) fps = Math.Clamp(numerator / denominator, 1, 30);
        }
        firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Seek(0); await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(12), lifetime.Token);
    }
    public void SetPlaying(bool value)
    {
        if (disposed) return;
        playing = value;
        if (!playing && continuous) operationStop?.Cancel();
        if (playing) EnsurePump();
    }
    public void Seek(double value)
    {
        if (disposed || Duration <= 0) return;
        Position = Math.Clamp(value, 0, Math.Max(0, Duration - 1 / fps)); revision++; pendingSeek = true;
        if (continuous) operationStop?.Cancel();
        EnsurePump();
    }
    private void EnsurePump() { if (!disposed && Width > 0 && pump is not { IsCompleted: false }) pump = PumpAsync(); }
    private async Task PumpAsync()
    {
        while (!disposed && (pendingSeek || playing))
        {
            long version = revision; double start = Position;
            bool run = !pendingSeek && playing; pendingSeek = false; continuous = run;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); operationStop = stop;
            try
            {
                bool complete = await DecodeAsync(start, run, version, stop.Token);
                if (run && complete && version == revision && playing && !disposed) { playing = false; Ended?.Invoke(); }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!disposed && !stop.IsCancellationRequested) { playing = false; pendingSeek = false; firstFrame?.TrySetException(error); Failed?.Invoke(error); }
            }
            finally { if (ReferenceEquals(operationStop, stop)) operationStop = null; continuous = false; }
        }
    }
    private async Task<bool> DecodeAsync(double start, bool run, long version, CancellationToken token)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var args = new List<string> { "-hide_banner","-loglevel","error","-nostdin","-threads","2","-ss",start.ToString("0.#########",CultureInfo.InvariantCulture),"-i",source,
            "-map","0:v:0","-an","-sn","-dn","-vf",$"fps={fps.ToString("0.######",CultureInfo.InvariantCulture)},scale={Width}:{Height}","-pix_fmt","bgra" };
        if (!run) args.AddRange(["-frames:v", "1"]);
        args.AddRange(["-f", "rawvideo", "pipe:1"]);
        foreach (string argument in args) info.ArgumentList.Add(argument);
        using var decoder = Process.Start(info) ?? throw new IOException("无法启动视频预览解码器"); process = decoder;
        Task<string> error = decoder.StandardError.ReadToEndAsync();
        using var cancel = token.Register(() => { try { if (!decoder.HasExited) decoder.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        try
        {
            var buffer = new byte[checked(Width * Height * 4)]; int frame = 0; var clock = new Stopwatch();
            while (!token.IsCancellationRequested)
            {
                int received = 0;
                while (received < buffer.Length)
                {
                    int count = await decoder.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(received), token);
                    if (count == 0) break; received += count;
                }
                if (received == 0) break;
                if (received != buffer.Length) throw new InvalidDataException("视频预览收到不完整画面");
                if (!clock.IsRunning) clock.Start();
                double wait = frame / fps - clock.Elapsed.TotalSeconds;
                if (run && wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), token);
                if (version != revision || disposed || run && !playing) return false;
                Position = Math.Min(Duration, start + frame / fps); Frame?.Invoke(buffer, Position); firstFrame?.TrySetResult(true); frame++;
                if (!run) break;
            }
            token.ThrowIfCancellationRequested();
            await decoder.WaitForExitAsync(token);
            string message = await error;
            if (decoder.ExitCode != 0) throw new IOException("视频预览解码失败：" + message);
            if (frame == 0) throw new InvalidDataException("视频预览没有解码到画面");
            return true;
        }
        finally
        {
            try { if (!decoder.HasExited) decoder.Kill(entireProcessTree: true); await decoder.WaitForExitAsync(); } catch (InvalidOperationException) { }
            await error; if (ReferenceEquals(process, decoder)) process = null;
        }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; playing = false; pendingSeek = false; lifetime.Cancel();
        try { if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); process.WaitForExit(1500); } } catch (InvalidOperationException) { }
        lifetime.Dispose();
    }
}
