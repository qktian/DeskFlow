using DeskFlow.Core.Models;

namespace DeskFlow.Core.Services;

/// <summary>
/// 循环待办的「应做日」推导与打卡工具。
/// 模型约定：
/// - 一个循环任务只有一个实体，所有周期共用；
/// - 打卡 key 为「应做日」日期（yyyy-MM-dd），存在且为 true 表示该周期完成；
/// - 某周期没有记录 = 未完成，进入下一周期后自然出现新的空记录（“下周期替代”语义）。
/// </summary>
public static class TodoRecurrence
{
    public static string OccurrenceKey(DateTime date) => date.ToString("yyyy-MM-dd");

    /// <summary>某日期是否是该任务的应做日（早于基准日一律不算）。</summary>
    public static bool IsDue(TodoItemModel task, DateTime date)
    {
        var start = task.StartDate.Date;
        var d = date.Date;
        if (d < start) return false;

        return task.Period switch
        {
            RecurrencePeriod.Daily => true,
            RecurrencePeriod.Weekly => d.DayOfWeek == start.DayOfWeek,
            RecurrencePeriod.Biweekly => ((d - start).Days % 14) == 0,
            RecurrencePeriod.Monthly => SameMonthDayOrClamped(start, d),
            _ => false
        };
    }

    /// <summary>该任务在当前处于哪个周期（截止 asOf 日期的最近一个应做日）；若无则返回基准日。</summary>
    public static DateTime CurrentCycle(DateTime start, RecurrencePeriod period, DateTime asOf)
    {
        var s = start.Date;
        var a = asOf.Date;
        if (a <= s) return s;

        return period switch
        {
            RecurrencePeriod.Daily => a,
            RecurrencePeriod.Weekly => PreviousCycleDay(s, a, 7),
            RecurrencePeriod.Biweekly => PreviousCycleDay(s, a, 14),
            RecurrencePeriod.Monthly => CurrentOrPreviousMonthly(s, a),
            _ => a
        };
    }

    /// <summary>当前周期完成后，下一个应做日。</summary>
    public static DateTime NextCycle(DateTime start, RecurrencePeriod period, DateTime from)
    {
        var f = from.Date;
        return period switch
        {
            RecurrencePeriod.Daily => f.AddDays(1),
            RecurrencePeriod.Weekly => f.AddDays(7),
            RecurrencePeriod.Biweekly => f.AddDays(14),
            RecurrencePeriod.Monthly => NextMonthlyDay(start, f),
            _ => f.AddDays(1)
        };
    }

    /// <summary>循环任务「今天是否已完成打卡」：取当前周期应做日查 CheckIns。</summary>
    public static bool IsCompletedForCycle(TodoItemModel task, DateTime asOf)
    {
        var cycle = CurrentCycle(task.StartDate, task.Period, asOf);
        return task.CheckIns.TryGetValue(OccurrenceKey(cycle), out var done) && done;
    }

    /// <summary>某月内的全部应做日（含未来，供日历绘制计划点）。</summary>
    public static List<DateTime> OccurrencesInMonth(TodoItemModel task, int year, int month)
    {
        var result = new List<DateTime>();
        var days = DateTime.DaysInMonth(year, month);
        for (var d = 1; d <= days; d++)
        {
            var date = new DateTime(year, month, d);
            if (IsDue(task, date))
            {
                result.Add(date);
            }
        }

        return result;
    }

    // ---------- 内部实现 ----------

    private static bool SameMonthDayOrClamped(DateTime start, DateTime d)
    {
        var targetDay = Math.Min(start.Day, DateTime.DaysInMonth(d.Year, d.Month));
        return d.Day == targetDay;
    }

    private static DateTime PreviousCycleDay(DateTime start, DateTime asOf, int step)
    {
        var days = (asOf - start).Days;
        var back = days % step;
        return asOf.AddDays(-back);
    }

    /// <summary>截止 asOf 的最近「每月应做日」（支持 31 日回落到月末，仅当 asOf 已过该月应做日才取本月，否则取上月）。</summary>
    private static DateTime CurrentOrPreviousMonthly(DateTime start, DateTime asOf)
    {
        // 先看本月应做日是否已到（<= asOf）
        var thisMonthDue = MonthlyDueIn(start, asOf.Year, asOf.Month);
        if (thisMonthDue <= asOf && asOf.Month == thisMonthDue.Month)
        {
            return thisMonthDue;
        }

        // 否则回退到上月的应做日
        var prev = asOf.AddMonths(-1);
        return MonthlyDueIn(start, prev.Year, prev.Month);
    }

    private static DateTime NextMonthlyDay(DateTime start, DateTime from)
    {
        // from 属于本月应做日已过，则找下月应做日
        var monthAfter = from.AddMonths(1);
        return MonthlyDueIn(start, monthAfter.Year, monthAfter.Month);
    }

    private static DateTime MonthlyDueIn(DateTime start, int year, int month)
    {
        var day = Math.Min(start.Day, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day);
    }
}
