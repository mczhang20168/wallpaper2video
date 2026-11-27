using System.Globalization;
using Wallpaper2Video.Desktop;
using static Program;

internal static class EmbeddedVideoChecks
{
    public static async Task RunAsync(string output, string source)
    {
        var model = new WorkspaceModel(); await model.LoadAsync(source);
        await Check("embeddedVideoSetsInitialDuration", () =>
        {
            Require(model.CanRender, "壁纸无法载入：" + model.Status);
            Require(Math.Abs(double.Parse(model.DurationText, CultureInfo.InvariantCulture) - 725d / 60) < 1e-8, "默认总长没有采用内嵌视频的725帧/60fps时长：" + model.DurationText);
            Require(model.ProjectInfo.Contains("内嵌视频"), "界面没有说明默认时长来源"); return Task.CompletedTask;
        });
        await Check("manualDurationSurvivesEmbeddedReplan", async () =>
        {
            model.DurationText = "18"; var until = DateTime.UtcNow.AddSeconds(20);
            while (model.Planning) { await Task.Delay(40); Require(DateTime.UtcNow < until, "重新规划超时"); }
            Require(model.DurationText == "18" && model.CurrentScenePlan?.Settings.Duration == 18, "手动总长被内嵌视频时长覆盖");
        });
        await Check("sceneWithoutEmbeddedVideoKeepsTwentySeconds", async () =>
        {
            await model.LoadAsync(@"E:\SteamLibrary\steamapps\workshop\content\431960\3299228616"); Require(model.DurationText == "20", "无内嵌视频的场景没有恢复20秒默认值");
        });
        model.StopPlanning(); await model.ReleasePreviewAsync();
    }
}
