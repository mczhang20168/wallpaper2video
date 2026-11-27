# 工作原理与兼容范围

Wallpaper2Video 在 Windows 上把 Wallpaper Engine 场景、视频壁纸和独立视频导出为静音 H.264 MP4，可用作电视桌面的动态壁纸。目前支持 Scene 和视频素材，Web 壁纸暂不支持导出。

[电视端设置](#tv-playback) · [TV setup in English](#tv-playback-en)

## 场景怎样变成视频

1. 读取壁纸配置和依赖，识别可以调节周期的效果、时间线、骨骼动画和内嵌视频。用循环次数把可调节的动画对齐到目标时长，界面会标出需要确认的效果。 [场景规划](../csharp/src/Wallpaper2Video.Runtime.Windows/ScenePlanner.cs) · [周期分析](../csharp/src/Wallpaper2Video.Runtime.Windows/AlignmentPlanner.cs)
2. 把效果显隐和时间设置写入临时副本。对识别出的鼠标、键盘和日期时间等内容，按规则关闭图层、移除脚本或固定输入。 [场景副本](../csharp/src/Wallpaper2Video.Runtime.Windows/PreparedScene.cs)
3. 由本机 Wallpaper Engine 在独立窗口渲染，用带 `gfxcapture` 的 FFmpeg 录制，再编码成 MP4。程序在任务结束后清理录制窗口和临时文件。 [场景导出](../csharp/src/Wallpaper2Video.Runtime.Windows/SceneRenderer.cs)

视频素材直接由 FFmpeg 处理。循环模式在成片末尾把画面渐变到素材开头。目标时长和交叠时长都按帧计算。例如，导出 20 秒、30 fps 的视频时，成片有 600 帧。若设置 2 秒交叠，源视频至少需要 22 秒。 [视频导出](../csharp/src/Wallpaper2Video.Runtime.Windows/VideoRenderer.cs)

## 哪些情况要看预览

- **同名效果的周期可能不同。** 创意工坊作者可以修改着色器、速度参数和启用分支。可用 `preflight` 查看当前素材识别出的周期和待确认项。 [预检命令](../csharp/README.md#命令行)
- **粒子发射时段控制的是新粒子的出现。** 停止发射后，已有粒子继续自然消亡。如果发射停止得较晚，粒子或拖尾可能到接缝处仍然可见。
- **随机模拟、实时音频和有历史状态的脚本可能产生不同的首尾画面。** 导出前播放循环预览，检查切回开头时是否有明显跳变。保留一次性入场动画会生成普通视频，适合保留开场动作。

MP4 旁的 `.report.json` 记录实际时长、帧数和接缝检查结果。场景报告还记录所选效果和周期调整。 [开发与操作说明](../csharp/README.md) · [贡献指南](../CONTRIBUTING.md)

<a id="tv-playback"></a>

## 在电视上设置动态壁纸

1. 在电视上安装 **Aerial**，打开「控制中心 → 个性化」显示二维码，让手机或电脑与电视连接到同一个局域网。 [Aerial 安装说明](https://github.com/pengchujin/aerial-tv/blob/v2.20/README.md#安装)
2. 手机扫码后，在「壁纸与屏保」的「我的视频」里点「添加视频」，选择导出的 MP4。也可以在同一局域网的电脑浏览器打开二维码中的网址，把 MP4 拖到网页上。 [视频上传说明](https://github.com/pengchujin/aerial-tv/blob/v2.20/docs/SCREENSAVER_VIDEOS.md) · [电脑拖放支持](https://github.com/pengchujin/aerial-tv/blob/v2.20/app/src/main/assets/web/index.html#L1749-L1786)
3. 在电视的「个性化 → 壁纸」选中刚上传的视频，或在网页的「动态」一栏选中它。单独选中一段视频时，Aerial 会循环播放它。 [Aerial 视频壁纸说明](https://github.com/pengchujin/aerial-tv/blob/v2.20/docs/ARCHITECTURE.md#L169-L174)

### 其他电视端选择

- **Projectivy Launcher（桌面壁纸）**支持把视频设为主页背景。在「Appearance → Wallpaper → Launcher wallpaper」选择「Media File」，再通过文件选择器挑选本地视频。 [Projectivy 功能](https://projectivylauncher.com/) · [文件选择说明](https://github.com/spocky/miproja1/issues/564)
- **Aerial Views（空闲屏保）**可在 Android TV 和 Google TV 上播放本地、Samba 或 WebDAV 视频。部分较新的 Google TV 需要额外设置才能启用第三方屏保。 [Aerial Views 与设置说明](https://github.com/theothernt/AerialViews#how-to-set-aerial-views-as-the-default-screensaver)

<a id="tv-playback-en"></a>

## Set up a video wallpaper on a TV

1. Install **Aerial** on the TV and open **Control Center → Personalize** to show the QR code. Connect the TV and your phone or PC to the same local network. [Aerial installation guide](https://github.com/pengchujin/aerial-tv/blob/v2.20/README.en.md#install)
2. Scan the QR code on your phone. In **Wallpaper & Screensaver**, find **My videos** and select **Add videos** to upload the exported MP4. On a PC, you can open the URL from the QR code and drag the MP4 onto the page. [Aerial video import guide](https://github.com/pengchujin/aerial-tv/blob/v2.20/docs/SCREENSAVER_VIDEOS.md) · [Desktop drag-and-drop support](https://github.com/pengchujin/aerial-tv/blob/v2.20/app/src/main/assets/web/index.html#L1749-L1786)
3. Select the video in **Personalize → Wallpaper** on the TV or in the **Moving** section of the web page. Aerial loops the selected clip. [Aerial video wallpaper documentation](https://github.com/pengchujin/aerial-tv/blob/v2.20/docs/ARCHITECTURE.md#L169-L174)

### Other options

- **Projectivy Launcher (home wallpaper)** can display a local video behind the home screen. Choose **Media File** under **Appearance → Wallpaper → Launcher wallpaper**, then select the video using the file picker. [Projectivy features](https://projectivylauncher.com/) · [File picker details](https://github.com/spocky/miproja1/issues/564)
- **Aerial Views (idle screensaver)** can play videos from local storage, Samba, or WebDAV on Android TV and Google TV. Some newer Google TV devices require extra setup to enable a third-party screensaver. [Aerial Views setup](https://github.com/theothernt/AerialViews#how-to-set-aerial-views-as-the-default-screensaver)
