# 开发与操作说明

## 构建与启动

在 Windows 11 x64 上安装 [global.json](global.json) 指定的 .NET SDK。以下命令从项目根目录运行。

```powershell
.\csharp\build.ps1
.\csharp\src\Wallpaper2Video.Desktop\bin\Release\net10.0-windows\Wallpaper2Video.Desktop.exe
```

构建脚本会运行兼容性检查和后台工作区检查。额外检查、统一代码排版和扩展效果的步骤见[贡献指南](../CONTRIBUTING.md)。

## 使用工作区

- Ctrl+O 打开文件，Ctrl+Shift+O 打开文件夹，Ctrl+S 设置输出位置，Ctrl+Enter 导出。播放时可拖动时间轴或逐帧定位。
- 右侧可按效果类型或真实对象树查看素材。点击名称查看设置，点击眼睛切换显隐。Ctrl、Shift 可多选，Ctrl+Z 可撤销最近一次批量显隐。鼠标、键盘和日期时间等强制排除项会显示原因。
- 可编辑效果支持调整整数循环次数。骨骼子动画也可以独立调整目标倍率。自然粒子使用原始模拟，开启定时模式后可设置开始和停止发射时间。停止发射后，已有粒子仍会自然消亡。
- 生成预览后先检查接缝，再导出。修改设置后需重新生成预览。已有视频至少应有“实际输出总长 + 交叠”的时长。程序会保护原素材，视频和同名报告使用新的输出文件名。
- 视频处理需要 FFmpeg 和 FFprobe。场景录制还需要可交互的 Windows 桌面、正在运行的 Wallpaper Engine 和带 `gfxcapture` 的 FFmpeg。可在高级设置填写工具路径，也可用环境变量 `FFMPEG_BINARY`、`FFPROBE_BINARY` 和 `WPE_EXE` 指定。

## 命令行

在项目根目录运行。示例中的尖括号内容需要替换为本机路径。

```powershell
$cli = 'csharp\src\Wallpaper2Video.Cli\bin\Release\net10.0\Wallpaper2Video.Cli.dll'
dotnet $cli inspect '<项目文件夹或视频>'
dotnet $cli inspect-pkg '<scene.pkg>'
dotnet $cli unpack '<scene.pkg>' '<新的输出目录>'
dotnet $cli loop '<输入.mp4>' '<输出.mp4>' 20 2
dotnet $cli periods '<场景目录>' --duration 20
dotnet $cli preflight '<场景目录>' --duration 20
dotnet $cli export '<场景目录>' -o 'output/scene.mp4' --duration 20 --fps 30 --disable-uncertain
```

`loop` 示例最后两个参数是总长和首尾交叠的秒数。先用 `preflight` 查看效果 ID、参考周期和待确认项，再按需在 `export` 中使用 `--cycles ID=次数` 或 `--particle ID=drain:开始秒数:停止秒数`。导出报告保存在视频旁，扩展名为 `.report.json`。命令失败返回非零退出码，Ctrl+C 取消返回 130。

## 手动验收

以下检查使用你已安装的 Wallpaper Engine 和本机素材。需要创建验收窗口时，程序会在交互桌面上启动窗口。

```powershell
.\csharp\build.ps1 -TestWallpapers

$desktop = 'csharp\src\Wallpaper2Video.Desktop\bin\Release\net10.0-windows\Wallpaper2Video.Desktop.dll'
dotnet $desktop --acceptance '<至少 8 秒的测试视频>' '<含效果的场景目录>' '<新的结果目录>'
dotnet $desktop --full-scene-acceptance '<宇航员场景目录>' '<新的结果目录>'
```

`-TestWallpapers` 批量检查[清单里的创意工坊素材](tests/Wallpaper2Video.WorkspaceTests/Fixtures/workshop-ids.txt)，结果保存在 `output/workspace-checks/`。`--acceptance` 检查窗口中的播放、定位、导出和任务中关窗。`--full-scene-acceptance` 还会实际录制、预览和导出场景。

## 便携发行

在仓库根目录运行以下命令。首次下载自包含运行时包时需要连接 NuGet。

```powershell
.\csharp\publish.ps1 -Zip
```

发行包默认生成到 `dist/Wallpaper2Video-win-x64.zip`，附带 .NET 运行时和相关许可。可以用 `-Destination` 指定新目录，或用 `-Update` 更新已有的发行目录。已有对应的运行时还原结果时，可以用 `-NoRestore` 重用本地缓存。 [绿色包使用说明](RELEASE-README.md)

带 FFmpeg 的发行包使用 `.\csharp\publish.ps1 -Zip -FfmpegDirectory '<解压后的 Gyan FFmpeg 目录>'`。脚本复制两个工具、上游许可、构建说明及项目内保存的匹配源码，默认生成 `dist/Wallpaper2Video-win-x64-with-ffmpeg.zip`。 [FFmpeg 源码与许可](../third_party/ffmpeg/README.md)

## 项目说明

项目分工、新增效果的步骤和调用方式见[贡献指南](../CONTRIBUTING.md)。场景录制流程和支持范围见[工作原理与兼容范围](../docs/README.md)。
