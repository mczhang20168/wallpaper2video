using Wallpaper2Video.Core;

namespace Wallpaper2Video.Runtime.Windows;

public static class SafeFiles
{
    public static string Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Path.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("无法解析素材链接");
            }
        }
        return Path.GetFullPath(current);
    }

    public static bool Contains(string root, string path)
    {
        string parent = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(parent)) parent += Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    public static string Member(string root, string relative)
    {
        string path = Resolve(Path.Combine(root, ArchivePaths.Normalize(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!Contains(Resolve(root), path)) throw new InvalidDataException("素材引用指向目录之外");
        return path;
    }

    public static string NewWorkDirectory(string parent)
    {
        Directory.CreateDirectory(parent);
        string path = Path.Combine(Path.GetFullPath(parent), ".w2v-cs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteWorkDirectory(string path, string parent)
    {
        if (!Directory.Exists(path)) return;
        string resolved = Resolve(path);
        string expected = Path.GetFullPath(path);
        if (!Contains(Resolve(parent), resolved) || !string.Equals(resolved, expected, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith(".w2v-cs-", StringComparison.Ordinal))
            throw new IOException("拒绝清理不属于本次任务的目录");
        Directory.Delete(resolved, recursive: true);
    }
}
