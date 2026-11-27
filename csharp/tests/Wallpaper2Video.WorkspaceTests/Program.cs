using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wallpaper2Video.Desktop;
using Wallpaper2Video.Runtime.Windows;

internal static class Program
{
    private static readonly Dictionary<string, object> Results = [];
    private static int failures;
    internal static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "csharp", "Wallpaper2Video.sln")))
                    return directory.FullName;
            }
            throw new DirectoryNotFoundException("测试必须位于仓库内");
        }
    }
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task Check(string name, Func<Task> action)
    {
        try { await action(); Results[name] = new { passed = true }; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures++; Results[name] = new { passed = false, error = error.ToString() }; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 1 && args[1] == "--startup-hidden") return StartupChecks.RunHidden(args[0]);
        if (args.Length > 1 && args[1] == "--startup") return StartupChecks.Run(args[0], args.Length > 2 && args[2] == "fish");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string root = RepositoryRoot;
        var resources = System.Xml.Linq.XDocument.Load(Path.Combine(root, "csharp/src/Wallpaper2Video.Desktop/App.xaml"));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new System.Xml.Linq.XElement(ns + "ResourceDictionary",
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            resources.Root!.Element(ns + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try { await RunAsync(args); }
            catch (Exception error) { failures++; Console.Error.WriteLine(error); }
            finally { Dispatcher.ExitAllFrames(); }
        });
        Dispatcher.Run();
        if (args.Length > 0) { Directory.CreateDirectory(args[0]); File.WriteAllText(Path.Combine(args[0], "workspace-tests.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true })); }
        return failures == 0 ? 0 : 1;
    }
    private static async Task RunAsync(string[] args)
    {
        await Check("cyclesFollowOutputDuration", () =>
        {
            var plan = new AlignmentPlan();
            plan.Phases.Add(new("short", "short", 4, [])); plan.Phases.Add(new("long", "long", 30, []));
            Require(plan.Choose(20)["short"] == 5 && plan.Choose(40)["short"] == 10, "默认次数仍依赖最长原周期，没有跟随成片总长");
            return Task.CompletedTask;
        });
        await Check("scrubbingUpdatesPicture", () =>
        {
            var model = new WorkspaceModel(); model.MediaOpened(20);
            var seeks = new List<double>(); model.SeekRequested += seeks.Add;
            model.BeginScrub(); model.Position = 12;
            Require(seeks.Count > 0 && Math.Abs(seeks[^1] - 12) < .001, "拖动期间没有向播放器提交画面定位");
            model.EndScrub(); model.StopPlanning(); return Task.CompletedTask;
        });
        await Check("timelineClicksSeekToPoint", () =>
        {
            var window = new MainWindow();
            var slider = Descendants<Slider>((DependencyObject)window.Content).Single(s => System.Windows.Automation.AutomationProperties.GetName(s) == "播放进度");
            Require(slider.IsMoveToPointEnabled, "时间轴仍按步长移动，没有点击直接定位");
            Require(!window.IsVisible, "后台检查意外显示窗口");
            window.Model.StopPlanning(); return Task.CompletedTask;
        });
        await Check("scrubLatestTargetAndResume", async () =>
        {
            var model = new WorkspaceModel(); model.MediaOpened(20); var seeks = new List<double>();
            var latestTarget = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            model.SeekRequested += seconds =>
            {
                seeks.Add(seconds);
                if (Math.Abs(seconds - 13) < .001) latestTarget.TrySetResult();
            };
            model.PlayCommand.Execute(null); model.BeginScrub(); model.Position = 1; model.Position = 8; model.Position = 13; model.UpdatePosition(.2);
            Require(Math.Abs(model.Position - 13) < .001, "拖动被播放器旧反馈拉回");
            await latestTarget.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(Math.Abs(seeks[^1] - 13) < .001, "限频丢失最后一次拖动位置");
            model.EndScrub(); Require(model.IsPlaying, "松开后没有恢复播放");
            model.UpdatePosition(.3); Require(Math.Abs(model.Position - 13) < .001, "松开后接受了旧反馈");
            model.UpdatePosition(13.033); Require(model.Position > 13, "跳转确认后仍冻结进度");
            model.BeginScrub(); model.Position = 20; model.EndScrub(); Require(Math.Abs(seeks[^1] - (20 - model.FrameStep)) < .001, "末尾没有定位到最后有效帧");
            model.StopPlanning();
        });
        await Check("noForegroundWindows", () => { Require(Application.Current.Windows.Cast<Window>().All(w => !w.IsVisible), "测试显示了桌面窗口"); return Task.CompletedTask; });
        if (args.Length > 1 && args[1] == "--wallpapers") await WorkspaceChecks.RunAsync(args[0]);
        if (args.Length > 1 && args[1] == "--particle-input") await ParticleEditingChecks.RunAsync(args[0], args.Length > 2 ? args[2] : null);
        if (args.Length > 1 && args[1] == "--layout") await LayoutChecks.RunAsync(args[0]);
        if (args.Length > 2 && args[1] == "--particle-children") await ParticleChildChecks.RunAsync(args[0], args[2]);
        if (args.Length > 2 && args[1] == "--fixed-particles") await FixedParticleChecks.RunAsync(args[0], args[2]);
        if (args.Length > 2 && args[1] == "--silent-particles") await SilentParticleChecks.RunAsync(args[0], args[2]);
        if (args.Length > 2 && args[1] == "--particle-availability") await ParticleAvailabilityChecks.RunAsync(args[0], args[2]);
        if (args.Length > 2 && args[1] == "--source-layers") await SourceLayerChecks.RunAsync(args[0], args[2]);
        if (args.Length > 2 && args[1] == "--embedded-video") await EmbeddedVideoChecks.RunAsync(args[0], args[2]);
        if (args.Length > 2 && args[1] == "--hidden-particle") await HiddenParticleChecks.RunAsync(args[0], args[2]);
        if (args.Length > 1 && args[1] == "--approved-native") await ApprovedNativeChecks.RunAsync(args[0], args.Length > 2 ? args[2] : null);
        if (args.Length > 1 && args[1] == "--approved-timing") await ApprovedTimingChecks.RunAsync(args[0]);
        if (args.Length > 1 && args[1] == "--fish-motion") await FishMotionChecks.RunAsync(args[0]);
        if (args.Length > 1 && args[1] == "--fish-motion-native") await FishMotionChecks.RunNativeAsync(args[0]);
        if (args.Length > 1 && args[1] == "--skeletal-v6") await SkeletalV6Checks.RunAsync(args[0], args.Length > 2 && args[2] == "native");
        if (args.Length > 1 && args[1] == "--spin") await SpinChecks.RunAsync(args[0], args.Length > 2 && args[2] == "native");
        if (args.Length > 1 && args[1] == "--shake") await ShakeChecks.RunAsync(args[0], args.Length > 2 && args[2] == "native");
        if (args.Length > 1 && args[1] == "--video-v4") await VideoV4Checks.RunAsync(args[0], args.Length > 2 && args[2] == "native");
        if (args.Length > 1 && args[1] == "--index-scene") await IndexSceneChecks.RunAsync(args[0], args.Length > 2 && args[2] == "native");
        if (args.Length > 1 && args[1] == "--dragon-spin") await DragonSpinChecks.RunAsync(args[0], args.Length > 2 && args[2] == "native");
        if (args.Length > 1 && args[1] == "--torch-ready") await TorchReadyChecks.RunAsync(args[0]);
    }
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
