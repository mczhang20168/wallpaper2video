using System.Text.Json.Serialization;

namespace Wallpaper2Video.Core;

public sealed record ExportSettings
{
    public double Duration { get; init; } = 20;
    public double Overlap { get; init; } = 2;
    public int Fps { get; init; } = 30;
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public double Warmup { get; init; } = 4;
    public bool AutomaticStartupWait { get; init; } = true;
    public bool PreserveEntrance { get; init; }
    public int Crf { get; init; } = 18;
    [JsonPropertyName("particle_mode")]
    public string ParticleMode { get; init; } = "original";
    public long Frames => FrameMath.Quantize(Duration, Fps);
    public long FadeFrames => FrameMath.Quantize(Overlap, Fps);

    public void Validate()
    {
        if (ParticleMode is not ("drain" or "off" or "original"))
            throw new ArgumentException("未知粒子模式");
        if (!double.IsFinite(Duration) || !double.IsFinite(Overlap) || !double.IsFinite(Warmup))
            throw new ArgumentException("时间参数必须为有限数字");
        if (Fps is < 1 or > 120 || Width is < 16 or > 8192 || Height is < 16 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(Width));
        if (Width % 2 != 0 || Height % 2 != 0)
            throw new ArgumentException("H.264分辨率必须为偶数");
        if (Crf is < 0 or > 35 || Warmup is < 0 or > 120)
            throw new ArgumentOutOfRangeException(nameof(Crf));
        if (Frames < 4 || FadeFrames < 2 || FadeFrames > Frames / 2)
            throw new ArgumentException("交叠至少2帧且不能超过视频的一半");
    }
}
