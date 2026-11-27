using System.Text.Json;
using System.Text.Json.Nodes;
using Wallpaper2Video.Runtime.Windows;

internal static class ShaderChecks
{
    public static int Run(string path)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        int count = 0;
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = row.GetProperty("name").GetString()!;
            var phase = new PeriodPhase(name, name, row.GetProperty("origin").GetDouble(), [], Method: row.GetProperty("method").GetString()!,
                Settings: JsonNode.Parse(row.GetProperty("settings").GetRawText())!.AsObject());
            string actual = AlignmentPatcher.Rewrite(row.GetProperty("source").GetString()!, phase, row.GetProperty("target").GetDouble());
            string expected = row.GetProperty("expected").GetString()!;
            if (actual != expected)
            {
                int index = Enumerable.Range(0, Math.Min(actual.Length, expected.Length)).FirstOrDefault(i => actual[i] != expected[i], Math.Min(actual.Length, expected.Length));
                Console.Error.WriteLine($"FAIL shader {name} at {index}; expected={expected.Substring(Math.Max(0, index - 40), Math.Min(120, expected.Length - Math.Max(0, index - 40)))}; actual={actual.Substring(Math.Max(0, index - 40), Math.Min(120, actual.Length - Math.Max(0, index - 40)))}");
                return 1;
            }
            count++;
        }
        Console.WriteLine($"PASS {count} Python/C# shader rewrites, exact generated source comparison");
        return count > 0 ? 0 : 1;
    }
}
