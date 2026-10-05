namespace DeskFlow.Core.Services;

public static class FileNamePolicy
{
    /// <summary>A rename accepts a leaf name, never a relative or absolute destination.</summary>
    public static string RenameTarget(string path, string newName, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName is "." or ".." ||
            newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            newName.Contains('\\') || newName.Contains('/') || newName.EndsWith('.') || newName.EndsWith(' '))
            throw new ArgumentException("请输入有效的文件名，不能包含路径、特殊字符或以句点/空格结尾。", nameof(newName));

        var stem = newName.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
             "123456789¹²³".Contains(stem[3])))
            throw new ArgumentException("不能使用 Windows 保留的设备名称。", nameof(newName));

        var source = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(source) ?? throw new ArgumentException("不能重命名根目录。", nameof(path));
        var extension = isDirectory ? string.Empty : Path.GetExtension(source);
        if (!newName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) newName += extension;
        return Path.Combine(directory, newName);
    }
}
