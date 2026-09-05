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
}

/// <summary>单个盒子在快照时刻的条目列表。</summary>
public sealed class BoxItemsSnapshot
{
    public string BoxId { get; set; } = string.Empty;

    public string BoxName { get; set; } = string.Empty;

    public List<string> Items { get; set; } = new();

    public List<string> ManualItems { get; set; } = new();
}
