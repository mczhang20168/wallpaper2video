# 贡献指南

## 构建和检查

在 Windows 11 x64 上安装 [global.json](csharp/global.json) 指定的 .NET SDK。运行 JavaScript 检查还需要 Node.js 24。以下命令从项目根目录运行。

```powershell
.\csharp\build.ps1
.\csharp\src\Wallpaper2Video.Desktop\bin\Release\net10.0-windows\Wallpaper2Video.Desktop.exe
node --test tests/controller-initialization.test.mjs
```

`build.ps1` 构建解决方案并运行兼容性和后台界面检查。如需检查编码、循环接缝和取消后的文件清理，将 `ffmpeg` 和 `ffprobe` 加入 PATH，再设置环境变量并重新运行构建脚本。

```powershell
$env:W2V_FFMPEG_TESTS = '1'
.\csharp\build.ps1
```

修改 C# 后可以在 `csharp/` 目录运行以下命令统一排版。GitHub 上的 Windows 检查会执行同样的格式规则以及 C#、FFmpeg 和 JavaScript 测试。 [自动检查配置](.github/workflows/windows.yml)

```powershell
dotnet format whitespace Wallpaper2Video.sln --no-restore
```

实机录制另需 Steam、正在运行的 Wallpaper Engine、可交互桌面、带 `gfxcapture` 的 FFmpeg 和本地壁纸。 [手动验收命令](csharp/README.md#手动验收)

## 从一种效果入手

以旋转为例，固定角速度下，一圈的时间是 `2π / |速度|`。如果速度为负，调整后的速度也应为负，才能保留旋转方向。

1. 用 `preflight` 查看该效果是否已经识别、参考周期是多少。 [命令行示例](csharp/README.md#命令行)
2. 通用 shader 分析能处理常见的正弦、时间回绕和简单辅助函数。如果效果需要专用规则，核对实际使用的分支和完整依赖，再决定如何计算周期。按依赖签名登记经过验证的版本。 [通用分析](csharp/src/Wallpaper2Video.Runtime.Windows/ShaderCycleProof.cs) · [专用规则](csharp/src/Wallpaper2Video.Runtime.Windows/AlignmentPlanner.cs) · [签名表](csharp/src/Wallpaper2Video.Runtime.Windows/Resources/reviewed_effects.json)
3. 在 `ScenePlanner` 检查未知版本、交互输入和历史状态的处理，再确认 `AlignmentPatcher` 如何把周期调整写进临时副本。 [内容筛选](csharp/src/Wallpaper2Video.Runtime.Windows/ScenePlanner.cs) · [改写副本](csharp/src/Wallpaper2Video.Runtime.Windows/AlignmentPatcher.cs)
4. 加一份自制的最小素材，验证原周期、调整结果与未知版本的行为。现有旋转测试用正负两种速度检查四秒一圈，还会修改一个依赖文件来检查旧签名失效。 [旋转回归](csharp/tests/Wallpaper2Video.CompatibilityTests/SpinChecks.cs)

## 在自己的代码中调用

通过 `ProjectLoader.Load` 读取素材，再用 `ScenePlanner.Build` 生成计划。读取 `ScenePlan.Targets()` 了解可编辑项目，通过 `ScenePlan.Select(...)` 提交选择，最后把计划和选择传给 `SceneRenderer.ExportAsync`。导出前，程序会重新读取素材并重新核对选择。读取 `ScenePlan.Scene` 得到独立的场景快照，可以用于查看和诊断。

独立视频可以通过 `ProjectLoader.Load` 和 `VideoRenderer.ExportAsync` 处理。窗口版和命令行均使用这些入口。 [场景计划](csharp/src/Wallpaper2Video.Runtime.Windows/ScenePlan.cs) · [CLI](csharp/src/Wallpaper2Video.Cli/Program.cs)

## 提交修改

在 Pull Request 中说明改动涉及的素材种类、执行的检查和观察到的结果。上传最小复现素材前确认其分享权限。测试优先用自制的配置、shader 和视频。
