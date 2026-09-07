using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskFlow.App.Services;
using DeskFlow.Core.Models;
using DeskFlow.Core.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace DeskFlow.App;

/// <summary>
/// 待办历史：跨清单汇总所有任务。
/// 普通任务按「未完成 / 已完成」划分，循环任务按「进行中 / 已关闭」划分。
/// </summary>
public partial class TodoHistoryWindow : Window
{
    private readonly AppConfig _config;
    private readonly TodoManager _manager;

    public TodoHistoryWindow(AppConfig config, TodoManager manager)
    {
        InitializeComponent();

        _config = config;
        _manager = manager;

        Reload();
    }

    private void Reload()
    {
        var keyword = SearchBox.Text?.Trim() ?? string.Empty;

        var rows = new List<TodoHistoryRow>();
        foreach (var board in _config.TodoBoards)
        {
            foreach (var item in board.Items)
            {
                var row = new TodoHistoryRow(item, board, DepthOf(board, item));
                if (keyword.Length == 0 || row.MatchKeyword(keyword))
                {
                    rows.Add(row);
                }
            }
        }

        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var pending = rows.Where(r => r.IsNormal && !r.Model.Completed)
            .OrderBy(r => PriorityRank(r.Model.Priority))
            .ThenBy(r => r.Model.DueDate ?? DateTime.MaxValue)
            .ThenBy(r => r.BoardName, comparer)
            .ToList();
        var done = rows.Where(r => r.IsNormal && r.Model.Completed)
            .OrderByDescending(r => r.Model.CompletedAt ?? DateTime.MinValue)
            .ThenBy(r => r.BoardName, comparer)
            .ToList();
        var active = rows.Where(r => !r.IsNormal && r.Model.ClosedAt is null)
            .OrderBy(r => r.BoardName, comparer)
            .ThenBy(r => r.Title, comparer)
            .ToList();
        var closed = rows.Where(r => !r.IsNormal && r.Model.ClosedAt is not null)
            .OrderByDescending(r => r.Model.ClosedAt ?? DateTime.MinValue)
            .ThenBy(r => r.BoardName, comparer)
            .ToList();

        PendingHost.ItemsSource = pending;
        DoneHost.ItemsSource = done;
        ActiveHost.ItemsSource = active;
        ClosedHost.ItemsSource = closed;

        PendingTitle.Text = $"未完成 · {pending.Count}";
        DoneTitle.Text = $"已完成 · {done.Count}";
        ActiveTitle.Text = $"进行中 · {active.Count}";
        ClosedTitle.Text = $"已关闭 · {closed.Count}";

        PendingEmpty.Visibility = pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DoneEmpty.Visibility = done.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActiveEmpty.Visibility = active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClosedEmpty.Visibility = closed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SummaryText.Text = $"共 {rows.Count} 条 · {_config.TodoBoards.Count} 个清单";
    }

    /// <summary>子任务在历史里同样缩进展示，深度沿父链累加（最多 5 层，防止异常数据成环）。</summary>
    private static int DepthOf(TodoBoardModel board, TodoItemModel item)
    {
        var depth = 0;
        var current = item;
        while (!string.IsNullOrEmpty(current.ParentId) && depth < 5)
        {
            var parent = board.Items.FirstOrDefault(t => t.Id == current.ParentId);
            if (parent is null)
            {
                break;
            }

            depth++;
            current = parent;
        }

        return depth;
    }

    private static int PriorityRank(TodoPriority p) => p switch
    {
        TodoPriority.ImportantUrgent => 0,
        TodoPriority.Important => 1,
        TodoPriority.Urgent => 2,
        _ => 3
    };

    // ---------- 行交互 ----------

    private void Row_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || sender is not FrameworkElement { Tag: TodoHistoryRow row })
        {
            return;
        }

        EditRow(row);
        e.Handled = true;
    }

    private void Row_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not TodoHistoryRow row)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = element };
        AddMenu(menu, "编辑…", () => EditRow(row));

        if (row.Model.Kind == TodoKind.Normal)
        {
            AddMenu(menu, row.Model.Completed ? "标记为未完成" : "标记为已完成", () => ToggleDone(row));
        }
        else
        {
            AddMenu(menu, "本月打卡…", () => ShowCalendar(row));
            AddMenu(menu, row.Model.ClosedAt is null ? "关闭此循环任务" : "重新开启", () => ToggleClosed(row));
        }

        menu.Items.Add(new Separator());
        AddMenu(menu, "删除", () => DeleteRow(row));

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void EditRow(TodoHistoryRow row)
    {
        var editor = new TodoTaskEditorWindow(row.Model) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not TodoItemModel edited)
        {
            return;
        }

        // 编辑器直接改写传入的模型实例，这里只需归一化并存盘
        edited.Normalize();
        ApplyChanged();
    }

    private void ToggleDone(TodoHistoryRow row)
    {
        row.Model.Completed = !row.Model.Completed;
        row.Model.CompletedAt = row.Model.Completed ? DateTime.Now : null;
        ApplyChanged();
    }

    private void ShowCalendar(TodoHistoryRow row)
    {
        var calendar = new TodoCalendarWindow(row.Model, _manager) { Owner = this };
        calendar.ShowDialog();
        ApplyChanged();
    }

    /// <summary>关闭 = 停止循环并归档（仅保留在历史中）；重新开启则重新回到面板。</summary>
    private void ToggleClosed(TodoHistoryRow row)
    {
        row.Model.ClosedAt = row.Model.ClosedAt is null ? DateTime.Now : null;
        ApplyChanged();
    }

    private void DeleteRow(TodoHistoryRow row)
    {
        var hasChild = row.Board.Items.Any(t => t.ParentId == row.Model.Id);
        var tip = hasChild ? "\n（子任务会升级一级，不会被删除）" : string.Empty;
        if (MessageBox.Show($"删除「{row.Model.Title}」？{tip}", "DeskFlow",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var child in row.Board.Items.Where(t => t.ParentId == row.Model.Id).ToList())
        {
            child.ParentId = row.Model.ParentId;
        }

        row.Board.Items.Remove(row.Model);
        ApplyChanged();
    }

    /// <summary>落盘 + 刷新桌面上的待办面板 + 刷新本窗口。</summary>
    private void ApplyChanged()
    {
        _manager.Save();
        _manager.RefreshAll();
        Reload();
    }

    // ---------- 工具栏 ----------

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e) => Reload();

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e) => Reload();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private static void AddMenu(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}

/// <summary>历史行视图模型：标题 / 清单 / 状态描述 / 配色。</summary>
internal sealed class TodoHistoryRow
{
    public TodoHistoryRow(TodoItemModel model, TodoBoardModel board, int level)
    {
        Model = model;
        Board = board;
        Level = level;
        BoardName = board.Name;
        Title = model.Title;
        DisplayTitle = level > 0 ? $"└ {model.Title}" : model.Title;

        PriorityBrush = TodoVisual.PriorityColor(model.Priority);
        PriorityTooltip = TodoVisual.PriorityText(model.Priority);

        ComputeStatus();
    }

    public TodoItemModel Model { get; }

    public TodoBoardModel Board { get; }

    public int Level { get; }

    public string BoardName { get; }

    public string Title { get; }

    public string DisplayTitle { get; }

    public bool IsNormal => Model.Kind == TodoKind.Normal;

    public Thickness IndentMargin => new(Level * 16, 0, 0, 0);

    public Brush PriorityBrush { get; }

    public string PriorityTooltip { get; }

    public string StatusText { get; private set; } = string.Empty;

    public Brush StatusBrush { get; private set; } = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x86));

    public string MetaText { get; private set; } = string.Empty;

    public bool MatchKeyword(string keyword) =>
        Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
        BoardName.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);

    private void ComputeStatus()
    {
        var source = $"清单「{BoardName}」";

        if (IsNormal)
        {
            if (!Model.Completed)
            {
                if (Model.DueDate is not { } due)
                {
                    StatusText = "待办";
                    StatusBrush = GrayBrush();
                    MetaText = $"{source} · 无截止时间";
                    return;
                }

                if (due < DateTime.Now)
                {
                    var days = (int)Math.Floor((DateTime.Now - due).TotalDays);
                    StatusText = "已超期";
                    StatusBrush = RedBrush();
                    MetaText = days <= 0
                        ? $"{source} · 已超期"
                        : $"{source} · 已超期 {days} 天（截止 {due:MM-dd HH:mm}）";
                    return;
                }

                StatusText = "进行中";
                StatusBrush = BlueBrush();
                MetaText = $"{source} · 截止 {due:MM-dd HH:mm}";
                return;
            }

            StatusText = "已完成";
            StatusBrush = GreenBrush();
            MetaText = Model.CompletedAt is { } at
                ? $"{source} · 完成于 {at:MM-dd HH:mm}"
                : $"{source} · 已完成";
            return;
        }

        // 循环任务
        var period = TodoVisual.PeriodText(Model.Period);
        var total = Model.CheckIns.Values.Count(v => v);
        var cycle = TodoRecurrence.CurrentCycle(Model.StartDate, Model.Period, DateTime.Today);
        var doneThisCycle = Model.CheckIns.TryGetValue(TodoRecurrence.OccurrenceKey(cycle), out var ok) && ok;

        if (Model.ClosedAt is { } closedAt)
        {
            StatusText = "已关闭";
            StatusBrush = GrayBrush();
            MetaText = $"{source} · {period} · 累计打卡 {total} 次 · 关闭于 {closedAt:MM-dd HH:mm}";
            return;
        }

        StatusText = doneThisCycle ? "本期已打卡" : "本期待打卡";
        StatusBrush = doneThisCycle ? GreenBrush() : RedBrush();
        MetaText = $"{source} · {period} · 自 {Model.StartDate:yyyy-MM-dd} 起累计打卡 {total} 次";
    }

    private static SolidColorBrush GrayBrush() => new(Color.FromRgb(0x8A, 0x8A, 0x86));

    private static SolidColorBrush RedBrush() => new(Color.FromRgb(0xD1, 0x4B, 0x4B));

    private static SolidColorBrush GreenBrush() => new(Color.FromRgb(0x2F, 0xA3, 0x6B));

    private static SolidColorBrush BlueBrush() => new(Color.FromRgb(0x38, 0x9F, 0xE0));
}
