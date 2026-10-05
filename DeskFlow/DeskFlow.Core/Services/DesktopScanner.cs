using DeskFlow.Core.Models;

namespace DeskFlow.Core.Services;

public static class DesktopScanner
{
    /// <summary>系统桌面真实路径：用户桌面 + 公共桌面。</summary>
    public static IReadOnlyList<string> GetDesktopDirectories()
    {
        var dirs = new List<string>();

        var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(userDesktop) && Directory.Exists(userDesktop))
        {
            dirs.Add(userDesktop);
        }

        var publicDir = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrEmpty(publicDir))
        {
            var publicDesktop = Path.Combine(publicDir, "Desktop");
            if (Directory.Exists(publicDesktop) && !dirs.Contains(publicDesktop, StringComparer.OrdinalIgnoreCase))
            {
                dirs.Add(publicDesktop);
            }
        }

        return dirs;
    }

    /// <summary>扫描桌面全部条目（含文件夹与快捷方式），跳过隐藏文件与系统文件。</summary>
    public static List<FileItem> Scan()
    {
        var items = new List<FileItem>();

        foreach (var dir in GetDesktopDirectories())
        {
            items.AddRange(ScanDirectory(dir));
        }

        return items
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public static List<FileItem> ScanDirectory(string dir)
    {
        var items = new List<FileItem>();
        if (!Directory.Exists(dir))
        {
            return items;
        }

        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                try
                {
                    var name = Path.GetFileName(path);
                    if (name.StartsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                    {
                        continue;
                    }

                    items.Add(FileMetadataCache.Get(path));
                }
                catch
                {
                    // 单个条目读取失败不影响整体扫描
                }
            }
        }
        catch
        {
            // 目录无访问权限时跳过
        }

        return items;
    }
}
