namespace DeskFlow.Core.Models;

/// <summary>
/// 手动创建的桌面归属快照：记录当时每个盒子装了哪些条目。
/// 恢复时按快照重建归属；文件已不存在的条目直接忽略。
/// </summary>
public sealed class BoxSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>当时每个盒子的条目归属。</summary>
    public List<BoxItemsSnapshot> Boxes { get; set; } = new();

    /// <summary>当时的排除列表（恢复时过滤掉已不存在的路径）。</summary>
    public List<string> ExcludedPaths { get; set; } = new();

    /// <summary>Todo panel layout at capture time. Older snapshots omit this.</summary>
    public List<TodoLayoutSnapshot> TodoLayouts { get; set; } = new();
}

/// <summary>单个盒子在快照时刻的条目列表。</summary>
public sealed class BoxItemsSnapshot
{
    public string BoxId { get; set; } = string.Empty;

    public string BoxName { get; set; } = string.Empty;

    public List<string> Items { get; set; } = new();

    public List<string> ManualItems { get; set; } = new();

    /// <summary>Optional for backward compatibility with assignment-only snapshots.</summary>
    public PanelLayoutSnapshot? Layout { get; set; }
}

public sealed class TodoLayoutSnapshot
{
    public string BoardId { get; set; } = string.Empty;
    public PanelLayoutSnapshot Layout { get; set; } = new();
}

public sealed class PanelLayoutSnapshot
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string ScreenId { get; set; } = string.Empty;
    public bool Collapsed { get; set; }
    public bool Pinned { get; set; }
    public bool AutoHeight { get; set; }
    public int IconSize { get; set; }
    public SortMode SortBy { get; set; }
    public BoxStyle Style { get; set; } = new();
}
