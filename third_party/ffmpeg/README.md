# FFmpeg 源码与许可 / Source and license

本目录保存自带版中 FFmpeg 与 FFprobe 对应的 FFmpeg 项目源码。

| 文件 | 内容 |
| --- | --- |
| [ffmpeg-469aad3897.tar.gz](ffmpeg-469aad3897.tar.gz) | 上游 FFmpeg 提交 `469aad38977e47574f019c8d69715dc7be74f92d` 的完整源码压缩包。 |
| [LICENSE.txt](LICENSE.txt) | Gyan 构建附带的 GPL v3 许可全文。 |
| [BUILD.txt](BUILD.txt) | 上游附带的版本、配置及功能清单。 |
| [SHA256SUMS](SHA256SUMS) | 源码压缩包的 SHA-256 校验值。 |

包内版本为 `2025-10-09-git-469aad3897-full_build-www.gyan.dev`。源码来自 [FFmpeg 官方仓库的对应提交](https://github.com/FFmpeg/FFmpeg/commit/469aad38977e47574f019c8d69715dc7be74f92d)。压缩包保留原始源码，未做修改。自带版 ZIP 的 `FFmpeg-source/` 中也包含这些文件。

上游入口：[此版本的 Gyan 发布页](https://github.com/GyanD/codexffmpeg/releases/tag/2025-10-09-git-469aad3897) · [FFmpeg 对应版本源码下载](https://github.com/FFmpeg/FFmpeg/archive/469aad38977e47574f019c8d69715dc7be74f92d.tar.gz) · [上游组件列表](https://www.gyan.dev/ffmpeg/builds/#libraries)。本次构建记录的组件版本见 [BUILD.txt](BUILD.txt)。

此 Gyan 完整版静态链接了 x264 等第三方库。这个压缩包包含 FFmpeg 本体源码。公开分发时，GPL 的对应源码要求还涉及实际链接的依赖和构建材料，上游版本清单见 `BUILD.txt`。 [FFmpeg 许可说明](https://ffmpeg.org/legal.html)

This directory holds the FFmpeg source revision used by the bundled FFmpeg and FFprobe tools. The archive contains the unmodified upstream FFmpeg project. `LICENSE.txt` and `BUILD.txt` are the license and build information supplied with the Gyan binaries. These files are also included under `FFmpeg-source/` in the bundled ZIP.

Upstream links: [matching Gyan release](https://github.com/GyanD/codexffmpeg/releases/tag/2025-10-09-git-469aad3897) · [matching FFmpeg source download](https://github.com/FFmpeg/FFmpeg/archive/469aad38977e47574f019c8d69715dc7be74f92d.tar.gz) · [upstream component list](https://www.gyan.dev/ffmpeg/builds/#libraries). See [BUILD.txt](BUILD.txt) for the component versions recorded for this build.

The Gyan full build statically links third-party libraries such as x264. GPL corresponding source requirements for public distribution also cover the linked dependencies and build materials. `BUILD.txt` lists the upstream configuration. [FFmpeg licensing information](https://ffmpeg.org/legal.html)
