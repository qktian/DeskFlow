namespace DeskFlow.Core.Models;

/// <summary>
/// 桌面条目（文件 / 文件夹 / 快捷方式）。唯一标识使用完整路径，
/// 不使用索引序号，避免桌面新增或删除文件导致条目错位。
/// </summary>
public sealed class FileItem
{
    /// <summary>快捷方式扩展名：.lnk 为程序/文件快捷方式，.url 为 Internet 快捷方式（Steam 游戏链接等）。</summary>
    private static readonly string[] ShortcutExtensions = { "lnk", "url" };

    public string Path { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>不含点的扩展名，小写；文件夹为空字符串。</summary>
    public string Extension { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public bool IsShortcut { get; set; }

    public long Size { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ModifiedAt { get; set; }

    public FileItemKind Kind { get; set; } = FileItemKind.Other;

    public static bool IsShortcutPath(string path)
    {
        var extension = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ShortcutExtensions.Contains(extension);
    }

    /// <summary>
    /// 条目在盒子中的显示名：快捷方式去掉 .lnk / .url 后缀（与资源管理器一致），
    /// 普通文件保持完整文件名。
    /// </summary>
    public static string GetDisplayName(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (name.Length == 0)
        {
            return path;
        }

        return IsShortcutPath(path) ? System.IO.Path.GetFileNameWithoutExtension(path) : name;
    }

    public static FileItem FromPath(string path)
    {
        var isDirectory = Directory.Exists(path);
        var extension = isDirectory ? string.Empty : System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var info = isDirectory ? new DirectoryInfo(path) : (FileSystemInfo)new FileInfo(path);

        var item = new FileItem
        {
            Path = path,
            Name = System.IO.Path.GetFileName(path),
            Extension = extension,
            IsDirectory = isDirectory,
            IsShortcut = IsShortcutPath(path),
            CreatedAt = SafeGet(info, i => i.CreationTime),
            ModifiedAt = SafeGet(info, i => i.LastWriteTime),
            Size = isDirectory ? 0L : SafeGet(info, i => ((FileInfo)i).Length)
        };

        item.Kind = Classify(item);
        return item;
    }

    private static T SafeGet<T>(FileSystemInfo info, Func<FileSystemInfo, T> getter)
    {
        try
        {
            return getter(info);
        }
        catch
        {
            return default!;
        }
    }

    private static FileItemKind Classify(FileItem item)
    {
        if (item.IsDirectory)
        {
            return FileItemKind.Folder;
        }

        if (item.IsShortcut)
        {
            return FileItemKind.Shortcut;
        }

        return item.Extension switch
        {
            "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "pdf" or "txt" or "md" or "csv" or "rtf" or "odt"
                => FileItemKind.Document,
            "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "svg" or "ico" or "psd" or "ai" or "heic"
                => FileItemKind.Image,
            "mp4" or "mkv" or "avi" or "mov" or "flv" or "wmv" or "webm" or "m4v"
                => FileItemKind.Video,
            "mp3" or "wav" or "flac" or "aac" or "ogg" or "m4a"
                => FileItemKind.Audio,
            "zip" or "rar" or "7z" or "tar" or "gz" or "bz2" or "xz"
                => FileItemKind.Archive,
            "exe" or "msi" or "appx" or "msix"
                => FileItemKind.Executable,
            _ => FileItemKind.Other
        };
    }
}

public enum FileItemKind
{
    Folder,
    Shortcut,
    Document,
    Image,
    Video,
    Audio,
    Archive,
    Executable,
    Other
}
