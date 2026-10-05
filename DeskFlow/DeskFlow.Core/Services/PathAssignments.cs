using DeskFlow.Core.Models;

namespace DeskFlow.Core.Services;

public static class PathAssignments
{
    /// <summary>Keep live assignments when a file or a containing folder is renamed.</summary>
    public static void Rename(AppConfig config, string oldPath, string newPath)
    {
        var prefix = oldPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string Map(string path) => path.Equals(oldPath, StringComparison.OrdinalIgnoreCase) ? newPath
            : path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(newPath, path[prefix.Length..]) : path;
        foreach (var box in config.Boxes)
        {
            box.Items = box.Items.Select(Map).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            box.ManualItems = box.ManualItems.Select(Map).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        config.ExcludedPaths = config.ExcludedPaths.Select(Map).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
