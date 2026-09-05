namespace DeskFlow.Core.Models;

/// <summary>
/// 归类规则：条件命中后把条目分配到目标盒子。
/// 多条规则按 Priority 从小到大依次匹配，首个命中生效。
/// </summary>
public sealed class RuleModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public int Priority { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>扩展名列表，不含点、小写；为空表示不限制扩展名。</summary>
    public List<string> Extensions { get; set; } = new();

    /// <summary>文件名关键词，命中任意一个即算命中；为空表示不限制。</summary>
    public List<string> NameKeywords { get; set; } = new();

    /// <summary>按文件类型匹配，为空表示不限制。</summary>
    public FileItemKind? Kind { get; set; }

    public string TargetBoxId { get; set; } = string.Empty;

    public bool IsMatch(FileItem item)
    {
        if (Extensions.Count > 0 && !Extensions.Contains(item.Extension))
        {
            return false;
        }

        if (Kind.HasValue && Kind.Value != item.Kind)
        {
            return false;
        }

        if (NameKeywords.Count > 0)
        {
            var name = item.Name.ToLowerInvariant();
            if (!NameKeywords.Any(k => name.Contains(k.ToLowerInvariant())))
            {
                return false;
            }
        }

        return true;
    }

    public string Describe()
    {
        var parts = new List<string>();
        if (Kind.HasValue)
        {
            parts.Add(Kind.Value switch
            {
                FileItemKind.Folder => "文件夹",
                FileItemKind.Shortcut => "快捷方式",
                FileItemKind.Document => "文档",
                FileItemKind.Image => "图片",
                FileItemKind.Video => "视频",
                FileItemKind.Audio => "音乐",
                FileItemKind.Archive => "压缩包",
                FileItemKind.Executable => "安装包",
                _ => "其他"
            });
        }

        if (Extensions.Count > 0)
        {
            var shown = string.Join("/", Extensions.Take(4));
            parts.Add(Extensions.Count > 4 ? $"{shown} 等 {Extensions.Count} 种" : shown);
        }

        if (NameKeywords.Count > 0)
        {
            parts.Add($"名称含 {string.Join("/", NameKeywords)}");
        }

        return parts.Count == 0 ? "任意条目" : string.Join(" · ", parts);
    }
}
