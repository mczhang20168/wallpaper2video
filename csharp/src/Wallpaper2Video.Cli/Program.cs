using System.Globalization;
using System.Text;
using System.Text.Json;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length == 0)
{
    Console.WriteLine("Wallpaper2Video：inspect / inspect-pkg / unpack / periods / preflight / loop / export / capture-acceptance\n" +
        "capture-acceptance <壁纸目录> -o <原始录制.mp4> [--ffmpeg 路径 --engine 路径]\n" +
        "export <素材> -o <输出.mp4> [--duration 20 --overlap 2 --fps 30 --width 1920 --height 1080 --warmup 4 --crf 18]\n" +
        "[--ffmpeg 路径 --engine 路径 --particles drain|off|original --particle ID=MODE[:START:STOP[:PREWARM]] --period ID=秒数 --cycles ID=次数]\n" +
        "[--disable-uncertain | --enable-all-uncertain | --enable-uncertain ID] [--disable-effect ID]");
    return 0;
}
try
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    string command = args[0];
    var positional = new List<string>();
    var flags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    var switches = new HashSet<string>();
    var switchNames = new HashSet<string> { "--disable-uncertain", "--enable-all-uncertain", "--preserve-entrance" };
    var optionNames = new HashSet<string> { "--output", "--duration", "--overlap", "--fps", "--width", "--height", "--warmup", "--crf", "--ffmpeg", "--engine", "--particles", "--particle", "--period", "--cycles", "--enable-uncertain", "--disable-effect" };
    for (int i = 1; i < args.Length; i++)
    {
        string key = args[i] == "-o" ? "--output" : args[i];
        if (switchNames.Contains(key)) { switches.Add(key); continue; }
        if (key.StartsWith('-'))
        {
            if (!optionNames.Contains(key) || i + 1 >= args.Length) throw new ArgumentException("未知或缺少值的参数：" + key);
            if (!flags.TryGetValue(key, out var values)) flags[key] = values = [];
            values.Add(args[++i]);
        }
        else positional.Add(args[i]);
    }
    string? Option(string name, string? fallback = null) => flags.TryGetValue("--" + name, out var values) ? values[^1] : fallback;
    IEnumerable<string> Many(string name) => flags.GetValueOrDefault("--" + name) ?? [];
    double Number(string name, double fallback) => double.Parse(Option(name, fallback.ToString("R", CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);
    int Integer(string name, int fallback) => int.Parse(Option(name, fallback.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);
    void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, SceneJson.Output));
    if (positional.Count == 0) throw new ArgumentException("需要素材路径");
    if (command == "inspect-pkg")
    {
        using var package = PackageReader.Open(positional[0]);
        var scene = ProjectLoader.ParseObject(package.Read("scene.json"));
        Print(new { package_version = package.Version, files = package.Entries.Select(e => new { name = e.Name, size = e.Size }), objects = scene.TryGetProperty("objects", out var array) ? array.GetArrayLength() : 0 });
        return 0;
    }
    if (command == "inspect") { Console.WriteLine(JsonSerializer.Serialize(ProjectLoader.Load(positional[0]), new JsonSerializerOptions { WriteIndented = true })); return 0; }
    if (command == "unpack")
    {
        if (positional.Count != 2) throw new ArgumentException("unpack 需要素材与新目录");
        string input = positional[0].EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) ? positional[0] : ProjectLoader.Load(positional[0]).PackagePath ?? throw new ArgumentException("这个项目没有 scene.pkg");
        Print(new { files = PackageExtractor.Extract(input, positional[1], cancellation.Token), destination = Path.GetFullPath(positional[1]) }); return 0;
    }
    var project = ProjectLoader.Load(positional[0]);
    string? engine = Option("engine");
    if (command == "capture-acceptance")
    {
        if (positional.Count != 1 || !Directory.Exists(positional[0])) throw new ArgumentException("需要一个壁纸目录");
        if (switches.Count != 0 || flags.Keys.Any(k => k is not ("--output" or "--engine" or "--ffmpeg")))
            throw new ArgumentException("capture-acceptance 仅接受 --output、--engine 和 --ffmpeg");
        Print(await AcceptanceCapture.RunAsync(project, Option("output") ?? throw new ArgumentException("需要 -o 原始录制.mp4"),
            Option("ffmpeg"), engine, cancellation.Token));
        return 0;
    }
    var bundle = new AssetBundle(project, engine);
    var alignment = AlignmentPlanner.Analyze(project, bundle);
    double defaultDuration = alignment.DefaultVideoDuration ?? 20;
    if (command is "periods" or "preflight" && positional.Count > 1) defaultDuration = double.Parse(positional[1], CultureInfo.InvariantCulture);
    if (command is "periods" or "preflight" && positional.Count > 2) { engine ??= positional[2]; bundle = new(project, engine); alignment = AlignmentPlanner.Analyze(project, bundle); }
    double defaultOverlap = 2;
    string? output = Option("output");
    if (command == "loop" && output == null && positional.Count > 1)
    {
        output = positional[1];
        if (positional.Count > 2) defaultDuration = double.Parse(positional[2], CultureInfo.InvariantCulture);
        if (positional.Count > 3) defaultOverlap = double.Parse(positional[3], CultureInfo.InvariantCulture);
    }
    string? ffmpeg = Option("ffmpeg", command == "loop" && positional.Count > 4 ? positional[4] : null);
    var settings = new ExportSettings
    {
        Duration = Number("duration", defaultDuration),
        Overlap = Number("overlap", defaultOverlap),
        Fps = Integer("fps", 30),
        Width = Integer("width", 1920),
        Height = Integer("height", 1080),
        Warmup = Number("warmup", 4),
        Crf = Integer("crf", 18),
        AutomaticStartupWait = Option("warmup") == null,
        ParticleMode = Option("particles", "original")!,
        PreserveEntrance = switches.Contains("--preserve-entrance")
    };
    var overrides = new Dictionary<string, double>();
    foreach (string value in Many("period"))
    {
        int split = value.LastIndexOf('='); if (split <= 0) throw new ArgumentException("周期设置应为 ID=秒数");
        overrides[value[..split]] = double.Parse(value[(split + 1)..], CultureInfo.InvariantCulture);
    }
    double duration = (double)settings.Frames / settings.Fps;
    var cycles = alignment.Choose(duration, overrides);
    foreach (string value in Many("cycles"))
    {
        int split = value.LastIndexOf('='); if (split <= 0 || !cycles.ContainsKey(value[..split])) throw new ArgumentException("循环次数设置应为有效的 ID=次数");
        cycles[value[..split]] = int.Parse(value[(split + 1)..], CultureInfo.InvariantCulture);
    }
    alignment.Validate(cycles);
    if (command == "periods") { Print(alignment.Summary(duration, cycles)); return 0; }
    settings.Validate();
    if (command == "loop")
    {
        if (project.Kind != "video") throw new ArgumentException("loop 命令需要视频文件；场景请用 export");
        if (output == null) throw new ArgumentException("需要 -o 输出.mp4");
        Print(await new VideoRenderer(ffmpeg).ExportAsync(project, output, settings, cancellation: cancellation.Token)); return 0;
    }
    var plan = ScenePlanner.Build(project, settings, engine, cycles, bundle, alignment);
    var particles = new List<ParticleChoice>();
    foreach (string value in Many("particle"))
    {
        int split = value.LastIndexOf('='); if (split <= 0) throw new ArgumentException("粒子设置应为 ID=MODE[:START:STOP[:PREWARM]]");
        string[] parts = value[(split + 1)..].Split(':');
        if (parts.Length is not (1 or 3 or 4)) throw new ArgumentException("粒子设置应为 ID=MODE[:START:STOP[:PREWARM]]");
        bool timed = parts[0] == "drain";
        if (!timed && parts.Length >= 3 && (double.Parse(parts[1], CultureInfo.InvariantCulture) != 0 || double.Parse(parts[2], CultureInfo.InvariantCulture) != 0)) throw new ArgumentException("自然播放/关闭模式的起止占位须为0");
        particles.Add(new(value[..split], parts[0], parts.Length >= 3 && timed ? double.Parse(parts[1], CultureInfo.InvariantCulture) : null, parts.Length >= 3 && timed ? double.Parse(parts[2], CultureInfo.InvariantCulture) : null, parts.Length == 4 ? double.Parse(parts[3], CultureInfo.InvariantCulture) : null));
    }
    if ((switches.Contains("--enable-all-uncertain") ? 1 : 0) + (switches.Contains("--disable-uncertain") ? 1 : 0) + (Many("enable-uncertain").Any() ? 1 : 0) > 1) throw new ArgumentException("风险选择参数不能同时使用");
    var enabled = switches.Contains("--enable-all-uncertain") ? plan.Optional.Where(p => p.CanEnable).Select(p => p.Id) : switches.Contains("--disable-uncertain") || Many("enable-uncertain").Any() ? Many("enable-uncertain") : null;
    var choice = plan.Select(enabled, particles, Many("disable-effect"));
    if (command == "preflight") { Print(new { review = plan.Summary(choice), period_alignment = alignment.Summary(duration, cycles), targets = plan.Targets() }); return 0; }
    if (command != "export") throw new ArgumentException("未知命令：" + command);
    if (output == null) throw new ArgumentException("需要 -o 输出文件");
    if (plan.Optional.Count > 0 && switches.Count == 0 && !Many("enable-uncertain").Any()) throw new ArgumentException("请先查看 preflight，并用 --disable-uncertain、--enable-all-uncertain 或 --enable-uncertain ID 明确选择风险项");
    string previousStage = ""; int previousPercent = -1;
    var progress = new Progress<RenderProgress>(p =>
    {
        int percent = (int)(p.Fraction * 100);
        if (p.Stage != previousStage || percent / 5 != previousPercent / 5)
        { Console.Error.WriteLine($"{p.Stage} {p.Fraction:P0}"); previousStage = p.Stage; previousPercent = percent; }
    });
    Print(project.Kind == "video" ? await new VideoRenderer(ffmpeg).ExportAsync(project, output, settings, progress, cancellation.Token)
        : await new SceneRenderer(ffmpeg, engine).ExportAsync(plan, choice, output, progress, cancellation: cancellation.Token));
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("已取消"); return 130; }
catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException or JsonException or OverflowException or NotSupportedException or TimeoutException)
{ Console.Error.WriteLine(exception.Message); return 1; }
