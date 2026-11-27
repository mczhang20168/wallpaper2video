using System.Buffers.Binary;
using System.Text;

namespace Wallpaper2Video.Runtime.Windows;

public sealed record EmbeddedVideoInfo(double Duration, long Frames, double Fps);
public sealed record EmbeddedVideoSource(string Label, string Texture, int ObjectIndex, bool Visible, EmbeddedVideoInfo Timing);

/// <summary>Bounded TEX video/ISO BMFF timing reads; media packets are never decoded or cached.</summary>
public static class EmbeddedVideoTiming
{
    private readonly record struct Box(int Start, int Payload, int End, string Type);
    internal readonly record struct VideoPayload(int Offset, int SizeOffset, long Length);
    internal static VideoPayload? ReadLayout(ReadOnlySpan<byte> head, long total)
    {
        if (head.Length < 55 || !head[..9].SequenceEqual("TEXV0005\0"u8) || !head.Slice(9, 9).SequenceEqual("TEXI0001\0"u8)) return null;
        if ((BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(22, 4)) & 32) == 0) return null;
        int extra = head.Slice(46, 9).SequenceEqual("TEXB0003\0"u8) ? 0 : head.Slice(46, 9).SequenceEqual("TEXB0004\0"u8) ? 4 : -1;
        if (extra < 0) return null;
        int offset = 87 + extra, sizeOffset = 83 + extra;
        if (head.Length < offset + 8) throw new InvalidDataException("内嵌视频头被截断");
        // TEXB4 inserts a reserved field before the mip count. The MP4 payload
        // therefore starts four bytes later than TEXB3; timing and retiming share this layout.
        if (BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(55, 4)) != 1 ||
            extra == 4 && BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(63, 4)) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(63 + extra, 4)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(75 + extra, 4)) != 0) throw new InvalidDataException("未核对的内嵌视频布局");
        long length = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(sizeOffset, 4));
        if (length < 8 || length > total - offset || !head.Slice(offset + 4, 4).SequenceEqual("ftyp"u8)) throw new InvalidDataException("内嵌MP4范围无效");
        return new(offset, sizeOffset, length);
    }
    public static EmbeddedVideoInfo? Read(AssetBundle bundle, string name)
    {
        var (head, total) = bundle.ReadRange(name, 0, 100);
        var layout = ReadLayout(head, total); if (layout == null) return null;
        int offset = layout.Value.Offset; long length = layout.Value.Length;
        for (long at = 0, boxes = 0; at < length && boxes++ < 100000;)
        {
            byte[] header = bundle.ReadRange(name, offset + at, (int)Math.Min(16, length - at)).Data;
            if (header.Length < 8) throw new InvalidDataException("MP4盒被截断");
            long size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)); int prefix = 8;
            if (size == 1) { if (header.Length < 16) throw new InvalidDataException("MP4扩展盒被截断"); ulong extended = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8)); if (extended > long.MaxValue) throw new InvalidDataException("MP4盒长度过大"); size = (long)extended; prefix = 16; }
            if (size == 0) size = length - at;
            if (size < prefix || size > length - at) throw new InvalidDataException("MP4盒范围无效");
            if (header.AsSpan(4, 4).SequenceEqual("moov"u8))
            {
                if (size > 8 * 1024 * 1024) throw new InvalidDataException("视频时间索引超过读取上限");
                return ParseMovie(bundle.ReadRange(name, offset + at, (int)size).Data, prefix);
            }
            at += size;
        }
        throw new InvalidDataException("内嵌视频缺少时间索引");
    }
    private static List<Box> Children(byte[] data, int start, int end)
    {
        var result = new List<Box>();
        while (start < end)
        {
            if (end - start < 8 || result.Count > 100000) throw new InvalidDataException("MP4子盒被截断");
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start, 4)); int prefix = 8;
            if (size == 1) { if (end - start < 16) throw new InvalidDataException("MP4子盒被截断"); ulong large = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(start + 8, 8)); if (large > int.MaxValue) throw new InvalidDataException("MP4索引盒过大"); size = (long)large; prefix = 16; }
            if (size == 0) size = end - start; if (size < prefix || size > end - start) throw new InvalidDataException("MP4子盒范围无效");
            result.Add(new(start, start + prefix, start + (int)size, Encoding.ASCII.GetString(data, start + 4, 4))); start += (int)size;
        }
        return result;
    }
    private static uint U32(byte[] b, int at, int end) { if (at < 0 || at > end - 4) throw new InvalidDataException("MP4时间字段被截断"); return BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at, 4)); }
    private static ulong U64(byte[] b, int at, int end) { if (at < 0 || at > end - 8) throw new InvalidDataException("MP4时间字段被截断"); return BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at, 8)); }
    private static (double Scale, double Duration) Clock(byte[] b, Box box)
    {
        if (box.Payload >= box.End || b[box.Payload] > 1) throw new InvalidDataException("未知MP4时间版本");
        bool extended = b[box.Payload] == 1; double scale = U32(b, box.Payload + (extended ? 20 : 12), box.End);
        double ticks = extended ? U64(b, box.Payload + 24, box.End) : U32(b, box.Payload + 16, box.End);
        if (scale <= 0 || ticks <= 0 || ticks == uint.MaxValue || ticks == ulong.MaxValue) throw new InvalidDataException("视频时长无效");
        return (scale, ticks / scale);
    }
    private static EmbeddedVideoInfo ParseMovie(byte[] b, int prefix)
    {
        var movie = Children(b, prefix, b.Length); var mvhd = movie.FirstOrDefault(x => x.Type == "mvhd");
        double movieScale = mvhd.End > 0 ? Clock(b, mvhd).Scale : 0; var candidates = new List<(EmbeddedVideoInfo Info, double Area)>();
        foreach (var track in movie.Where(x => x.Type == "trak"))
        {
            var children = Children(b, track.Payload, track.End); var media = children.FirstOrDefault(x => x.Type == "mdia"); if (media.End == 0) continue;
            var md = Children(b, media.Payload, media.End); var handler = md.FirstOrDefault(x => x.Type == "hdlr");
            if (handler.End - handler.Payload < 12 || !b.AsSpan(handler.Payload + 8, 4).SequenceEqual("vide"u8)) continue;
            var clock = md.FirstOrDefault(x => x.Type == "mdhd"); if (clock.End == 0) continue; var (scale, mediaDuration) = Clock(b, clock); double duration = mediaDuration;
            var tkhd = children.FirstOrDefault(x => x.Type == "tkhd"); double area = 0;
            if (tkhd.End > 0 && movieScale > 0)
            {
                if (b[tkhd.Payload] > 1) throw new InvalidDataException("未知视频轨道版本");
                double ticks = b[tkhd.Payload] == 1 ? U64(b, tkhd.Payload + 28, tkhd.End) : U32(b, tkhd.Payload + 20, tkhd.End);
                if (ticks > 0 && ticks < uint.MaxValue) duration = ticks / movieScale;
                area = (U32(b, tkhd.End - 8, tkhd.End) / 65536d) * (U32(b, tkhd.End - 4, tkhd.End) / 65536d);
            }
            long frames = 0; double sampleTicks = 0; var minf = md.FirstOrDefault(x => x.Type == "minf");
            if (minf.End > 0)
            {
                var stbl = Children(b, minf.Payload, minf.End).FirstOrDefault(x => x.Type == "stbl");
                if (stbl.End > 0)
                {
                    var stts = Children(b, stbl.Payload, stbl.End).FirstOrDefault(x => x.Type == "stts");
                    if (stts.End > 0)
                    {
                        uint entries = U32(b, stts.Payload + 4, stts.End); if (entries > 100000 || (long)entries * 8 > stts.End - stts.Payload - 8) throw new InvalidDataException("视频采样时间表无效");
                        for (int i = 0; i < entries; i++) { uint count = U32(b, stts.Payload + 8 + i * 8, stts.End), delta = U32(b, stts.Payload + 12 + i * 8, stts.End); frames = checked(frames + count); sampleTicks += (double)count * delta; }
                    }
                }
            }
            if (!double.IsFinite(duration) || duration <= 0 || duration > 86400) throw new InvalidDataException("视频播放时长无效");
            candidates.Add((new(duration, frames, sampleTicks > 0 ? frames * scale / sampleTicks : 0), area));
        }
        return candidates.OrderByDescending(c => c.Area).ThenByDescending(c => c.Info.Duration).Select(c => c.Info).FirstOrDefault() ?? throw new InvalidDataException("内嵌视频没有有效视频轨道");
    }
}
