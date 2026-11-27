using System.Buffers.Binary;
using System.Text;
using Wallpaper2Video.Core;
using Wallpaper2Video.Runtime.Windows;

internal static class EmbeddedTimingChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static byte[] Box(string type, params byte[][] payload)
    {
        var bytes = new byte[8 + payload.Sum(p => p.Length)]; BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length); Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4); int at = 8;
        foreach (var item in payload) { item.CopyTo(bytes, at); at += item.Length; }
        return bytes;
    }
    private static void U(byte[] b, int at, uint n) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at, 4), n);
    private static byte[] Clock(uint scale, uint duration) { var b = new byte[20]; U(b, 12, scale); U(b, 16, duration); return b; }
    private static byte[] Texture(uint frames, bool movieAtEnd = false, int version = 3)
    {
        var track = new byte[84]; U(track, 20, frames * 1000); U(track, 76, 3840u << 16); U(track, 80, 2160u << 16);
        var handler = new byte[12]; Encoding.ASCII.GetBytes("vide").CopyTo(handler, 8);
        var samples = new byte[16]; U(samples, 4, 1); U(samples, 8, frames); U(samples, 12, 1000);
        var video = Box("trak", Box("tkhd", track), Box("mdia", Box("mdhd", Clock(60000, frames * 1000)), Box("hdlr", handler), Box("minf", Box("stbl", Box("stts", samples)))));
        // A longer movie header must not replace the video track's own playback length.
        var moov = Box("moov", Box("mvhd", Clock(60000, 60000 * 60)), video);
        var payload = Box("ftyp", Encoding.ASCII.GetBytes("isom0000isom")).Concat(movieAtEnd ? Box("mdat", new byte[65536]).Concat(moov) : moov.Concat(Box("mdat", new byte[65536]))).ToArray();
        int extra = version == 4 ? 4 : 0;
        var header = new byte[87 + extra]; "TEXV0005\0"u8.CopyTo(header); "TEXI0001\0"u8.CopyTo(header.AsSpan(9)); Encoding.ASCII.GetBytes($"TEXB{version:0000}\0").CopyTo(header, 46);
        foreach (var (at, n) in new[] { (22, 34u), (55, 1u), (63 + extra, 1u), (83 + extra, (uint)payload.Length) }) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at, 4), n);
        return header.Concat(payload).ToArray();
    }
    public static void Run()
    {
        string parent = Path.GetTempPath(), root = SafeFiles.NewWorkDirectory(parent);
        try
        {
            File.WriteAllText(Path.Combine(root, "project.json"), """{"type":"Scene","file":"scene.json"}""");
            File.WriteAllText(Path.Combine(root, "scene.json"), """{"objects":[{"id":1,"name":"短片","image":"a.json"},{"id":2,"name":"主体","image":"b.json"},{"id":3,"visible":false,"image":"c.json"}]}""");
            Directory.CreateDirectory(Path.Combine(root, "materials"));
            foreach (string name in new[] { "a", "b", "c" })
            {
                File.WriteAllText(Path.Combine(root, name + ".json"), "{\"material\":\"materials/" + name + ".json\"}");
                File.WriteAllText(Path.Combine(root, "materials", name + ".json"), "{\"passes\":[{\"textures\":[\"" + name + "\"]}]}");
            }
            File.WriteAllBytes(Path.Combine(root, "materials/a.tex"), Texture(600)); File.WriteAllBytes(Path.Combine(root, "materials/b.tex"), Texture(725, true)); File.WriteAllBytes(Path.Combine(root, "materials/c.tex"), Texture(1800));
            var source = ProjectLoader.Load(root); var bundle = new AssetBundle(source); var alignment = AlignmentPlanner.Analyze(source, bundle);
            Require(alignment.EmbeddedVideos.Count == 3 && Math.Abs(alignment.DefaultVideoDuration!.Value - 725d / 60) < 1e-10, "默认应取最长可见视频，不能被隐藏的30秒视频覆盖");
            var main = alignment.EmbeddedVideos.Single(v => v.ObjectIndex == 1); Require(main.Timing.Frames == 725 && main.Timing.Fps == 60, "应读取视频采样表，不能用movie总时长代替");
            Require(!bundle.BinaryFiles.Keys.Any(k => k.EndsWith(".tex")), "检测不应缓存整段视频");
            using (var package = new PackageReader(new MemoryStream(RuntimeChecks.Package(("Movie.tex", Texture(725))))))
            { var range = package.ReadRange("MOVIE.TEX", 46, 9); Require(range.Data.AsSpan().SequenceEqual("TEXB0003\0"u8) && range.Length > 65536, "PKG范围读取应保持大小和casefold"); }
            byte[] corrupt = Texture(725); File.WriteAllBytes(Path.Combine(root, "materials/bad.tex"), corrupt[..^1]); bool rejected = false;
            try { EmbeddedVideoTiming.Read(bundle, "materials/bad.tex"); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "截断内嵌视频不能猜测时长");
            byte[] v4 = Texture(754, version: 4); File.WriteAllBytes(Path.Combine(root, "materials/v4.tex"), v4);
            var v4Timing = EmbeddedVideoTiming.Read(bundle, "materials/v4.tex");
            Require(v4Timing != null && v4Timing.Frames == 754 && Math.Abs(v4Timing.Duration - 754d / 60) < 1e-9, "TEXB0004视频未提取754帧/60fps的周期");
            File.WriteAllBytes(Path.Combine(root, "materials/v4-cut.tex"), v4[..^1]); rejected = false;
            try { EmbeddedVideoTiming.Read(bundle, "materials/v4-cut.tex"); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "截断TEXB0004视频不能猜测时长");
        }
        finally { SafeFiles.DeleteWorkDirectory(root, parent); }
        Console.WriteLine("PASS embedded video timing: TEX/MP4 / video-track duration / tail moov / bounded reads / longest visible / truncation");
    }
}
