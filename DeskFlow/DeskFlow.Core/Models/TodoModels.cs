namespace DeskFlow.Core.Models;

/// <summary>待办类型。</summary>
public enum TodoKind
{
    /// <summary>普通待办：描述 + 截止时间 + 优先级 + 完成状态。</summary>
    Normal,

    /// <summary>循环待办：按周期重复，通过日历打卡记录每个周期的完成情况。</summary>
    Recurring
}

/// <summary>
/// 紧急重要程度（四象限，共四种状态）。
/// </summary>
public enum TodoPriority
{
    /// <summary>重要且紧急（P1，红色）。</summary>
    ImportantUrgent,

    /// <summary>重要但不紧急（P2，橙色）。</summary>
    Important,

    /// <summary>紧急但不重要（P3，蓝色）。</summary>
    Urgent,

    /// <summary>不重要也不紧急（P4，灰色）。</summary>
    Normal
}

/// <summary>循环周期。</summary>
public enum RecurrencePeriod
{
    Daily,
    Weekly,
    Biweekly,
    Monthly
}

/// <summary>
/// 一条待办任务。普通任务用 Completed 布尔；循环任务用 CheckIns 按「周期应做日」逐日打卡，
/// 当前周期未完成时记录为空，下一周期自然重新出现（无需复制任务实体）。
/// </summary>
public sealed class TodoItemModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = string.Empty;

    /// <summary>父任务 Id。null 表示顶层任务；非空表示该任务为父任务的子任务。</summary>
    public string? ParentId { get; set; }

    /// <summary>该任务的子任务是否处于折叠状态（仅当其有子任务时有意义）。</summary>
    public bool ChildrenCollapsed { get; set; }

    public TodoKind Kind { get; set; } = TodoKind.Normal;

    /// <summary>紧急重要程度（四象限）。</summary>
    public TodoPriority Priority { get; set; } = TodoPriority.Normal;

    // ---------- 普通待办 ----------
    /// <summary>截止时间。超过该时间且未完成即为「已超期」。</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>普通任务是否已完成。</summary>
    public bool Completed { get; set; }

    /// <summary>最近一次完成时间。</summary>
    public DateTime? CompletedAt { get; set; }

    // ---------- 循环待办 ----------
    /// <summary>循环任务基准日期（从此日起按周期重复）。</summary>
    public DateTime StartDate { get; set; }

    /// <summary>循环周期。</summary>
    public RecurrencePeriod Period { get; set; } = RecurrencePeriod.Daily;

    /// <summary>
    /// 打卡记录：key 为周期应做日（yyyy-MM-dd），value 为当天是否完成。
    /// 缺省的 key 表示该周期未完成（下周期自动替代）。
    /// </summary>
    public Dictionary<string, bool> CheckIns { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 循环任务的关闭（归档）时间：非空表示该任务已停止循环。
    /// 已关闭的循环任务不再出现在待办面板，只在「待办历史」中保留，可随时重新开启。
    /// 普通任务不使用该字段（其终态是 Completed）。
    /// </summary>
    public DateTime? ClosedAt { get; set; }

    public void Normalize()
    {
        Title = (Title ?? string.Empty).Trim();
        CheckIns ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        if (Kind == TodoKind.Recurring && StartDate == default)
        {
            StartDate = DateTime.Today;
        }
    }
}

/// <summary>
/// 「待办组件」模型：一个面板 = 一个清单，可包含多条普通 + 循环任务。
/// 位置 / 尺寸 / 折叠状态与盒子一致，可在桌面任意摆放多个。
/// </summary>
public sealed class TodoBoardModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "待办事项";

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 280;

    public double Height { get; set; } = 380;

    public string ScreenId { get; set; } = string.Empty;

    public bool Collapsed { get; set; }

    /// <summary>
    /// 高度随任务数量自适应：开启后窗口高度 = 标题栏 + 所有可见任务行的实际高度，内容不再需要滚动。
    /// 用户手动拖拽上下边缘调整高度时会自动关闭该项（把控制权交回用户）。
    /// </summary>
    public bool AutoHeight { get; set; } = true;

    /// <summary>面板外观（背景色 / 透明度 / 圆角 / 文字色），与普通盒子一致，可单独调整。</summary>
    public BoxStyle Style { get; set; } = new()
    {
        BackgroundColor = "#33000000",
        Opacity = 0.92,
        CornerRadius = 12,
        ForegroundColor = "#FFFFFF",
        ShowTitle = true
    };

    public List<TodoItemModel> Items { get; set; } = new();

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "待办事项" : Name.Trim();
        Width = Math.Clamp(Width, 220, 700);
        // 自适应模式下高度由任务行数决定，允许比手动模式的 200 更矮（76 = 标题栏 32 + 空列表提示 44）
        Height = Math.Clamp(Height, AutoHeight ? 76 : 200, 1000);
        Style ??= new BoxStyle();
        Style.Opacity = Math.Clamp(Style.Opacity, 0.2, 1.0);
        Style.CornerRadius = Math.Clamp(Style.CornerRadius, 0, 24);
        Items ??= new List<TodoItemModel>();
        Items.RemoveAll(item => item is null);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id))
            {
                item.Id = Guid.NewGuid().ToString("N");
                ids.Add(item.Id);
            }
            item.Normalize();
        }
        // Malformed imported JSON must not create cycles or unbounded UI recursion.
        var byId = Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var item in Items)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { item.Id };
            var parentId = item.ParentId;
            while (!string.IsNullOrEmpty(parentId))
            {
                if (!visited.Add(parentId) || visited.Count > 64 || !byId.TryGetValue(parentId, out var parent))
                {
                    item.ParentId = null;
                    break;
                }
                parentId = parent.ParentId;
            }
        }
    }
}
