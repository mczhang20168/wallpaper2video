# Wallpaper2Video Windows x64

**简体中文** · [English](#english)

把 Wallpaper Engine 壁纸导出为循环 MP4，用 Aerial 设为电视的动态桌面壁纸。

1. 在 Windows 11 x64 上解压整个 ZIP。这个绿色包已附带 .NET 10 桌面运行时。使用框架依赖版时，请先安装 [.NET 10 Desktop Runtime（Windows x64）](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)。
2. `with-ffmpeg` 版已附带 FFmpeg 和 FFprobe。标准版可使用系统已有的工具，或下载 [FFmpeg Windows 完整版](https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full.7z)，解压后把 `bin` 里的 `ffmpeg.exe` 和 `ffprobe.exe` 放到桌面程序旁。录制 Scene 壁纸前，在 Steam 启动 Wallpaper Engine。
3. 双击 `Wallpaper2Video.Desktop.exe`，打开壁纸文件夹或视频，生成预览并检查接缝，再导出 MP4。
4. 在 Aerial 打开「控制中心 → 个性化」。扫码打开网页，在「我的视频」添加 MP4，再到电视「壁纸」选中它。 [Aerial 设置说明](https://github.com/pengchujin/aerial-tv/blob/main/docs/SCREENSAVER_VIDEOS.md)

## English

Export a looping MP4 from Wallpaper Engine and set it as your TV home screen wallpaper with Aerial.

1. Extract the ZIP on Windows 11 x64. It includes the .NET 10 Desktop Runtime. For a framework-dependent build, first install the [Windows x64 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
2. The `with-ffmpeg` ZIP includes FFmpeg and FFprobe. For the standard ZIP, use an existing installation or extract the [full Windows build of FFmpeg](https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full.7z) and copy `ffmpeg.exe` and `ffprobe.exe` from its `bin` folder next to the desktop app. Launch Wallpaper Engine from Steam before recording a Scene wallpaper.
3. Double-click `Wallpaper2Video.Desktop.exe`, open the wallpaper folder or video, check the loop preview, then export the MP4.
4. In Aerial, open **Control Center → Personalize** and scan the QR code. Add the MP4 in **My videos** on the web page, then select it in **Wallpaper** on the TV. [Aerial setup guide](https://github.com/pengchujin/aerial-tv/blob/main/docs/SCREENSAVER_VIDEOS.md)

## CLI and licenses

Run `Wallpaper2Video.Cli.exe` for command help. Use `preflight` to inspect a scene, `export` to export it, or `loop` to process a video. The app writes a silent H.264 MP4 and a `.report.json` file.

`LICENSE` and `THIRD-PARTY-NOTICES.txt` cover the project and its bundled data. Files beginning with `Microsoft.NETCore.App` or `Microsoft.WindowsDesktop.App` contain the runtime license notices.

The `with-ffmpeg` ZIP includes the separate Gyan FFmpeg tools. Their upstream license and build information are in `FFmpeg-LICENSE.txt` and `FFmpeg-BUILD.txt`. `FFmpeg-source/` contains the matching upstream FFmpeg source archive.

FFmpeg 上游 / upstream: [Gyan 2025-10-09 build](https://github.com/GyanD/codexffmpeg/releases/tag/2025-10-09-git-469aad3897) · [对应 FFmpeg 源码 / matching FFmpeg source](https://github.com/FFmpeg/FFmpeg/archive/469aad38977e47574f019c8d69715dc7be74f92d.tar.gz) · [组件、版本与源码范围 / components, versions and source coverage](https://github.com/mczhang20168/wallpaper2video/blob/main/third_party/ffmpeg/README.md).
