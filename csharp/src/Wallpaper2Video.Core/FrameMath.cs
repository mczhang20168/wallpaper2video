namespace Wallpaper2Video.Core;

public static class FrameMath
{
    public static long Quantize(double seconds, int fps)
    {
        if (!double.IsFinite(seconds) || fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        return checked((long)Math.Round(seconds * fps, MidpointRounding.ToEven));
    }

    public static long NearestCycles(double ratio)
    {
        if (!double.IsFinite(ratio) || ratio <= 0)
            throw new ArgumentOutOfRangeException(nameof(ratio));
        return Math.Max(1, checked((long)Math.Floor(ratio + 0.5)));
    }
}

public readonly record struct LoopPeriod
{
    public long Numerator { get; }
    public long Denominator { get; }
    public double Seconds => (double)Numerator / Denominator;

    public LoopPeriod(long totalFrames, int fps, int cycles)
    {
        if (totalFrames < 1 || fps < 1 || cycles is < 1 or > 1_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(cycles));
        Numerator = totalFrames;
        Denominator = checked((long)fps * cycles);
    }
}
