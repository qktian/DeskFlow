using System.Windows.Media;
using DeskFlow.Core.Models;
using Color = System.Windows.Media.Color;

namespace DeskFlow.App;

/// <summary>待办相关的共享视觉辅助：文案与颜色（四象限 / 周期）。</summary>
public static class TodoVisual
{
    public static string PriorityText(TodoPriority p) => p switch
    {
        TodoPriority.ImportantUrgent => "重要紧急",
        TodoPriority.Important => "重要",
        TodoPriority.Urgent => "紧急",
        _ => "普通"
    };

    public static string PeriodText(RecurrencePeriod p) => p switch
    {
        RecurrencePeriod.Daily => "每日",
        RecurrencePeriod.Weekly => "每周",
        RecurrencePeriod.Biweekly => "每两周",
        RecurrencePeriod.Monthly => "每月",
        _ => "周期"
    };

    /// <summary>
    /// 优先级四象限配色（用户指定四色）：
    /// 重要紧急 = 红，重要不紧急 = 橙，紧急不重要 = 蓝，不重要不紧急 = 绿。
    /// </summary>
    public static SolidColorBrush PriorityColor(TodoPriority p) => p switch
    {
        TodoPriority.ImportantUrgent => new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E)), // 红
        TodoPriority.Important => new SolidColorBrush(Color.FromRgb(0xED, 0x89, 0x36)),       // 橙
        TodoPriority.Urgent => new SolidColorBrush(Color.FromRgb(0x38, 0x9F, 0xE0)),          // 蓝
        _ => new SolidColorBrush(Color.FromRgb(0x2F, 0xA3, 0x6B))                             // 绿
    };

    public static SolidColorBrush DoneGreen() => new(Color.FromRgb(0x4C, 0xAF, 0x50));

    public static SolidColorBrush OverdueRed() => new(Color.FromRgb(0xFF, 0x6B, 0x6B));

    public static SolidColorBrush TextWhite() => new(Colors.White);

    public static SolidColorBrush TextDim() => new(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));

    public static SolidColorBrush TextFaint() => new(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
}
