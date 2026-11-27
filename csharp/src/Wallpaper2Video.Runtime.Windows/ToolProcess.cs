using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Wallpaper2Video.Runtime.Windows;

public enum RenderActivity { Preparing, Capturing, Encoding, Validating, Publishing, Completed }
public sealed record RenderProgress(string Stage, double Fraction, RenderActivity Activity = RenderActivity.Preparing, double StageFraction = 0);

public static class ToolProcess
{
    public static string Find(string name, string? explicitPath = null)
    {
        var candidates = new List<string?> { explicitPath, Environment.GetEnvironmentVariable(name.ToUpperInvariant() + "_BINARY"),
                                             Path.Combine(AppContext.BaseDirectory, name + ".exe") };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), name + (OperatingSystem.IsWindows() ? ".exe" : ""))));
        return candidates.FirstOrDefault(p => p != null && File.Exists(p)) is string found
            ? Path.GetFullPath(found) : throw new FileNotFoundException($"找不到{name}，请指定工具路径");
    }

    internal static Process Start(string tool, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException($"无法启动{tool}");
    }

    internal static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        await process.WaitForExitAsync();
    }

    internal static async Task<string> DrainAsync(StreamReader reader, int limit = 65536, Action<string>? line = null)
    {
        var text = new StringBuilder();
        while (await reader.ReadLineAsync() is string value)
        {
            line?.Invoke(value);
            text.AppendLine(value);
            if (text.Length > limit) text.Remove(0, text.Length - limit);
        }
        return text.ToString();
    }

    public static async Task<string> RunAsync(string tool, IEnumerable<string> arguments, CancellationToken cancellation,
        double timeoutSeconds = 60, Action<string>? outputLine = null)
    {
        cancellation.ThrowIfCancellationRequested();
        using var process = Start(tool, arguments);
        Task<string> output = DrainAsync(process.StandardOutput, 2 * 1024 * 1024, outputLine);
        Task<string> errors = DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellation);
            string result = await output;
            string error = await errors;
            if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(tool)}失败：{error}");
            return result;
        }
        finally
        {
            await StopAsync(process);
            await Task.WhenAll(output, errors);
        }
    }

    public static string Number(double value) => value.ToString("0.000000000", CultureInfo.InvariantCulture);
}
