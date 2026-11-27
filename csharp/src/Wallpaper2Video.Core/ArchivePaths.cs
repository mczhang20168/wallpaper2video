using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Wallpaper2Video.Core;

public static class ArchivePaths
{
    private static readonly HashSet<string> Devices = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(1, 9).Select(n => $"COM{n}"))
            .Concat(Enumerable.Range(1, 9).Select(n => $"LPT{n}")), StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string name)
    {
        string text = name.Replace('\\', '/');
        string[] parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();
        if (text.StartsWith('/') || parts.Length == 0 || parts.Any(p => p == ".." ||
            p.EndsWith(' ') || p.EndsWith('.') || p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
            Devices.Contains(p.Split('.')[0])))
            throw new InvalidDataException($"不安全的素材路径：{name}");
        return string.Join('/', parts);
    }

    // OrdinalIgnoreCase alone does not detect Python's Straße/STRASSE collision.
    private static readonly Lazy<Dictionary<int, string>> FoldTable = new(() =>
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Wallpaper2Video.Core.CaseFold.json")
            ?? throw new InvalidOperationException("缺少Unicode折叠表");
        return JsonSerializer.Deserialize<Dictionary<int, string>>(stream)!;
    });

    public static string CaseFold(string value)
    {
        var result = new StringBuilder();
        foreach (Rune rune in value.EnumerateRunes())
            result.Append(FoldTable.Value.TryGetValue(rune.Value, out string? folded) ? folded : rune.ToString());
        return result.ToString();
    }
}
