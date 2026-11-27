using System.Text;

namespace Wallpaper2Video.Core;

public sealed record PackageEntry(string Name, uint Offset, uint Size);

public sealed class PackageReader : IDisposable
{
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly long dataOffset;
    private readonly Dictionary<string, PackageEntry> lookup = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private bool disposed;
    public string Version { get; }
    public IReadOnlyList<PackageEntry> Entries { get; }

    public PackageReader(Stream input, bool leaveOpen = false)
    {
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("需要可读取和定位的流");
        stream = input;
        this.leaveOpen = leaveOpen;
        try
        {
            stream.Position = 0;
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
            if (reader.ReadUInt32() != 8) throw new InvalidDataException("PKG文件头无效");
            byte[] version = reader.ReadBytes(8);
            if (version.Length != 8 || !version.AsSpan(0, 4).SequenceEqual("PKGV"u8) ||
                version.Skip(4).Any(c => c is < (byte)'0' or > (byte)'9'))
                throw new InvalidDataException("PKGV版本无效");
            Version = Encoding.ASCII.GetString(version);
            uint count = reader.ReadUInt32();
            if (count is < 1 or > 100_000) throw new InvalidDataException("PKG条目数无效");
            var entries = new List<PackageEntry>();
            for (uint i = 0; i < count; i++)
            {
                uint length = reader.ReadUInt32();
                if (length is < 1 or > 4096) throw new InvalidDataException("PKG名称长度无效");
                byte[] bytes = reader.ReadBytes((int)length);
                if (bytes.Length != length) throw new EndOfStreamException();
                string name = new UTF8Encoding(false, true).GetString(bytes).Replace('\\', '/');
                ArchivePaths.Normalize(name);
                var entry = new PackageEntry(name, reader.ReadUInt32(), reader.ReadUInt32());
                if (!lookup.TryAdd(ArchivePaths.CaseFold(name), entry))
                    throw new InvalidDataException($"PKG路径重复：{name}");
                entries.Add(entry);
            }
            dataOffset = stream.Position;
            foreach (PackageEntry entry in entries)
                if (checked(dataOffset + entry.Offset + entry.Size) > stream.Length)
                    throw new InvalidDataException($"PKG素材越界：{entry.Name}");
            Entries = entries.AsReadOnly();
        }
        catch
        {
            if (!leaveOpen) stream.Dispose();
            throw;
        }
    }

    public static PackageReader Open(string path) => new(File.OpenRead(path));

    public byte[] Read(string name, int maxSize = 16 * 1024 * 1024)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!lookup.TryGetValue(ArchivePaths.CaseFold(name), out PackageEntry? entry))
            throw new InvalidDataException($"PKG中没有素材：{name}");
        if (entry.Size > maxSize) throw new InvalidDataException("素材超过读取上限");
        lock (sync)
        {
            stream.Position = checked(dataOffset + entry.Offset);
            byte[] data = new byte[checked((int)entry.Size)];
            stream.ReadExactly(data);
            return data;
        }
    }

    public void CopyTo(string name, Stream destination, CancellationToken cancellation = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!lookup.TryGetValue(ArchivePaths.CaseFold(name), out PackageEntry? entry))
                throw new InvalidDataException($"PKG中没有素材：{name}");
            stream.Position = checked(dataOffset + entry.Offset);
            long remaining = entry.Size;
            byte[] buffer = new byte[64 * 1024];
            while (remaining > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0) throw new EndOfStreamException("解包素材被截断");
                destination.Write(buffer, 0, count);
                remaining -= count;
            }
        }
    }

    public byte[] ReadTail(string name, int maximum)
    {
        if (maximum is < 1 or > 2 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximum));
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!lookup.TryGetValue(ArchivePaths.CaseFold(name), out var entry)) throw new InvalidDataException("PKG中没有素材：" + name);
            int length = (int)Math.Min(entry.Size, maximum); stream.Position = checked(dataOffset + entry.Offset + entry.Size - length);
            byte[] data = new byte[length]; stream.ReadExactly(data); return data;
        }
    }

    public (byte[] Data, long Length) ReadRange(string name, long offset, int maximum)
    {
        if (offset < 0 || maximum is < 1 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(offset));
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!lookup.TryGetValue(ArchivePaths.CaseFold(name), out var entry)) throw new InvalidDataException("PKG中没有素材：" + name);
            if (offset > entry.Size) throw new InvalidDataException("素材分段读取越界");
            int length = (int)Math.Min(maximum, entry.Size - offset); stream.Position = checked(dataOffset + entry.Offset + offset);
            var data = new byte[length]; stream.ReadExactly(data); return (data, entry.Size);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (!disposed && !leaveOpen) stream.Dispose();
            disposed = true;
        }
    }
}
