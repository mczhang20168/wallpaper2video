using System.IO;
using Wallpaper2Video.Desktop;
using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;
using static Program;

internal static class HiddenParticleChecks
{
    public static async Task RunAsync(string output, string source)
    {
        var model = new WorkspaceModel(); await model.LoadAsync(source);
        var plan = model.CurrentScenePlan!; const string key = "particle:12";
        await Check("hiddenSnowSingleBatchUndoAndPreparedVisibility", () =>
        {
            var row = model.Effects.Single(r => r.Key == key);
            Require(!row.Active && plan.ParticleControls().Single(p => p.Id == key).CanDrain, "默认隐藏粒子清单不一致");
            model.ToggleEffect(key, true); Require(row.Active, "开启雪未更新状态：" + model.Status);
            using (var copy = PreparedScene.Create(plan, model.CurrentSceneSelection!, output))
                Require(!False(Value(Array(Parse(copy.Project.Scene!.Value.GetRawText())["objects"]).Single(o => Text(o?["id"]) == "884")?["visible"])), "开启后仍保留隐藏绑定");
            model.ApplyEffectStates([key], false); Require(!row.Active, "批量关闭失败");
            model.UndoEffectsCommand.Execute(null); Require(row.Active, "撤销未恢复");
            model.ApplySourceVisibility([12], false);
            Require(plan.RemovedLayers(model.CurrentSceneSelection!).Contains(12), "效果开启覆盖了原素材明确隐藏");
            return Task.CompletedTask;
        });
        model.StopPlanning(); await model.ReleasePreviewAsync();
    }
}
