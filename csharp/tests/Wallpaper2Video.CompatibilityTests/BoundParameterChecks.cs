using Wallpaper2Video.Runtime.Windows;
using static Wallpaper2Video.Core.SceneJson;

internal static class BoundParameterChecks
{
    public static void Run()
    {
        var scene = Parse("""
            {"objects":[{"effects":[
              {"passes":[{"constantshadervalues":{"speed":{"user":"breathingspeed","value":1},"strength":{"user":"breathingspeed","value":0.1}}}]},
              {"passes":[{"constantshadervalues":{"speed":{"user":"breathingspeed","value":1}}}]}
            ]}]}
            """);
        var plan = new AlignmentPlan();
        foreach (int i in new[] { 0, 1 }) plan.Phases.Add(new($"p{i}", "shake", 2 * Math.PI, ["objects", "0", "effects", i.ToString()], Parameter: "speed", Raw: System.Text.Json.Nodes.JsonValue.Create(1), Factor: 2 * Math.PI));
        AlignmentPatcher.Apply(scene, Path.GetTempPath(), plan, new Dictionary<string, int> { { "p0", 2 }, { "p1", 1 } }, 10);
        for (int i = 0; i < 2; i++)
        {
            var speed = scene["objects"]?[0]?["effects"]?[i]?["passes"]?[0]?["constantshadervalues"]?["speed"];
            if (speed is System.Text.Json.Nodes.JsonObject binding && binding.ContainsKey("user")) throw new InvalidOperationException("对齐速度仍由作者用户属性覆盖");
            if (Math.Abs(Number(speed) - 2 * Math.PI / (i == 0 ? 5 : 10)) > 1e-9) throw new InvalidOperationException("共享用户属性的两个效果未独立对齐");
        }
        if (Text(scene["objects"]?[0]?["effects"]?[0]?["passes"]?[0]?["constantshadervalues"]?["strength"]?["user"]) != "breathingspeed")
            throw new InvalidOperationException("周期对齐修改了无关的用户属性绑定");
        Console.WriteLine("PASS retimed shader values override author properties independently; unrelated bindings retained");
    }
}
