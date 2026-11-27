using System.Security.Cryptography;
using System.Text.Json;
using Wallpaper2Video.Core;

if (args.Length == 2 && args[0] == "--shader-fixture") return ShaderChecks.Run(args[1]);

string directory = args.Length == 1 ? args[0] : Path.Combine(AppContext.BaseDirectory, "fixtures");
using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "expected.json")));
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
int cases = 0;
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
void Validity(JsonElement test, Action action)
{
    bool rejected = false;
    try { action(); }
    catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or OverflowException) { rejected = true; }
    Require(rejected != test.GetProperty("valid").GetBoolean(), "拒绝行为不同：" + test);
    cases++;
}

try
{
    foreach (JsonElement test in document.RootElement.GetProperty("rounding").EnumerateArray())
    {
        Require(FrameMath.Quantize(test.GetProperty("seconds").GetDouble(), test.GetProperty("fps").GetInt32()) ==
                test.GetProperty("frames").GetInt64(), "帧量化不同：" + test);
        cases++;
    }
    foreach (JsonElement test in document.RootElement.GetProperty("cycles").EnumerateArray())
        Validity(test, () =>
        {
            long count = FrameMath.NearestCycles(test.GetProperty("ratio").GetDouble());
            Require(count == test.GetProperty("count").GetInt64(), "循环次数不同");
        });
    foreach (JsonElement test in document.RootElement.GetProperty("settings").EnumerateArray())
        Validity(test, () =>
        {
            ExportSettings settings = test.GetProperty("input").Deserialize<ExportSettings>(options)!;
            settings.Validate();
            Require(settings.Frames == test.GetProperty("frames").GetInt64() &&
                    settings.FadeFrames == test.GetProperty("fadeFrames").GetInt64(), "设置量化不同");
        });
    foreach (JsonElement test in document.RootElement.GetProperty("paths").EnumerateArray())
        Validity(test, () =>
        {
            string path = ArchivePaths.Normalize(test.GetProperty("input").GetString()!);
            Require(path == test.GetProperty("normalized").GetString(), "路径规范化不同");
        });
    foreach (JsonElement test in document.RootElement.GetProperty("packages").EnumerateArray())
        Validity(test, () =>
        {
            using var package = PackageReader.Open(Path.Combine(directory, test.GetProperty("file").GetString()!));
            Require(package.Version == test.GetProperty("version").GetString(), "版本不同");
            var entries = test.GetProperty("entries").EnumerateArray().ToArray();
            Require(package.Entries.Count == entries.Length, "条目数不同");
            for (int i = 0; i < entries.Length; i++)
            {
                var actual = package.Entries[i];
                var expected = entries[i];
                Require(actual.Name == expected.GetProperty("name").GetString() &&
                        actual.Size == expected.GetProperty("size").GetUInt32(), "条目不同");
                string hash = Convert.ToHexStringLower(SHA256.HashData(package.Read(actual.Name.ToUpperInvariant())));
                Require(hash == expected.GetProperty("sha256").GetString(), "素材内容不同");
            }
        });
    var period = new LoopPeriod(600, 30, 3);
    Require(period.Numerator == 600 && period.Denominator == 90 && period.Seconds == 20d / 3, "周期精度丢失");
    Console.WriteLine($"PASS {cases} Python/C# compatibility cases + exact cycle representation");
    await RuntimeChecks.RunAsync(directory);
    SceneChecks.Run();
    SkeletalChecks.Run();
    GenericShaderChecks.Run();
    RenderTargetChecks.Run();
    EmbeddedTimingChecks.Run();
    SpinChecks.Run();
    BoundParameterChecks.Run();
    TimelineFilteringChecks.Run();
    InstanceTimelineChecks.Run();
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL after {cases} cases: {exception}");
    return 1;
}
