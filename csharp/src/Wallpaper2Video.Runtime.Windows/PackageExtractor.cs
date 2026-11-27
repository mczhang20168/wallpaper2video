using Wallpaper2Video.Core;

namespace Wallpaper2Video.Runtime.Windows;

public static class PackageExtractor
{
    public static int Extract(string source, string destination, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        string target = SafeFiles.Resolve(destination);
        if (Path.Exists(target)) throw new IOException("解包目标必须是新目录");
        using var package = PackageReader.Open(source);
        var names = package.Entries.Select(e => (Entry: e, Relative: ArchivePaths.Normalize(e.Name))).ToArray();
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in names)
            if (!files.Add(ArchivePaths.CaseFold(item.Relative))) throw new InvalidDataException("解包路径重复");
        foreach (var item in names)
        {
            string[] parts = item.Relative.Split('/');
            for (int i = 1; i < parts.Length; i++)
                if (files.Contains(ArchivePaths.CaseFold(string.Join('/', parts.Take(i)))))
                    throw new InvalidDataException("文件与目录路径冲突");
        }
        string parent = Path.GetDirectoryName(target)!;
        string stage = SafeFiles.NewWorkDirectory(parent);
        try
        {
            foreach (var item in names)
            {
                cancellation.ThrowIfCancellationRequested();
                string path = SafeFiles.Member(stage, item.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                package.CopyTo(item.Entry.Name, output, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
            return names.Length;
        }
        finally { SafeFiles.DeleteWorkDirectory(stage, parent); }
    }
}
