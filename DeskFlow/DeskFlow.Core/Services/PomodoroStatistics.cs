using System.Globalization;
using System.Text;
using DeskFlow.Core.Models;

namespace DeskFlow.Core.Services;

public sealed record PomodoroDayStatistics(DateTime Date, int Completed, int Interrupted,
    double CompletedSeconds, double InterruptedSeconds)
{
    public double FocusSeconds => CompletedSeconds + InterruptedSeconds;
}

public sealed record PomodoroTaskStatistics(string? TaskId, string Title, int Completed, int Interrupted,
    double CompletedSeconds, double InterruptedSeconds, double Share)
{
    public double FocusSeconds => CompletedSeconds + InterruptedSeconds;
}

public sealed class PomodoroStatisticsResult
{
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public required IReadOnlyList<PomodoroDayStatistics> Days { get; init; }
    public required IReadOnlyList<PomodoroTaskStatistics> Tasks { get; init; }
    public int Completed => Days.Sum(d => d.Completed);
    public int Interrupted => Days.Sum(d => d.Interrupted);
    public double FocusSeconds => Days.Sum(d => d.FocusSeconds);
    public double? CompletionRate => Completed + Interrupted == 0 ? null : (double)Completed / (Completed + Interrupted);
    public int ActiveDays => Days.Count(d => d.FocusSeconds > 0);
    public double AverageDailySeconds => FocusSeconds / Days.Count;
    public int TodayCompleted { get; init; }
    public int DailyGoal { get; init; }
    // Independent of the selected range; a not-yet-complete today does not break yesterday's streak.
    public int CurrentStreak { get; init; }
    public int LongestStreak { get; init; }
}

/// <summary>All ranges are inclusive calendar dates in the supplied timezone.
/// A focus session belongs to its end date; paused/offline time is never reconstructed.</summary>
public static class PomodoroStatistics
{
    public static PomodoroStatisticsResult Calculate(IEnumerable<PomodoroRecord> history,
        DateTime start, DateTime end, DateTime today, int dailyGoal, TimeZoneInfo? zone = null)
    {
        start = start.Date; end = end.Date; today = today.Date; zone ??= TimeZoneInfo.Local;
        var dayCount = (end - start).Days + 1;
        if (start > end || dayCount > 366 || end > today)
            throw new ArgumentException("请选择不晚于今天、起止顺序正确且不超过 366 天的日期范围。");
        var all = history.Where(r => r is not null && double.IsFinite(r.FocusSeconds) && r.FocusSeconds >= 0)
            .Select(r => (Record: r, Date: TimeZoneInfo.ConvertTime(r.EndedAt, zone).Date))
            .Where(r => r.Date <= today).ToList();
        var selected = all.Where(r => r.Date >= start && r.Date <= end).ToList();
        var groups = selected.GroupBy(r => r.Date).ToDictionary(g => g.Key, g => g.Select(v => v.Record).ToList());
        var days = Enumerable.Range(0, dayCount).Select(offset =>
        {
            var date = start.AddDays(offset);
            var records = groups.GetValueOrDefault(date) ?? new List<PomodoroRecord>();
            return new PomodoroDayStatistics(date, records.Count(r => r.Completed), records.Count(r => !r.Completed),
                records.Where(r => r.Completed).Sum(r => r.FocusSeconds), records.Where(r => !r.Completed).Sum(r => r.FocusSeconds));
        }).ToList();
        var totalSeconds = days.Sum(d => d.FocusSeconds);
        // Task ID survives renames; free-focus items are grouped by their saved title.
        var tasks = selected.Select(r => r.Record).GroupBy(r =>
            (Linked: !string.IsNullOrWhiteSpace(r.TaskId), Key: !string.IsNullOrWhiteSpace(r.TaskId) ? r.TaskId! : Title(r)))
            .Select(g =>
            {
                var latest = g.OrderByDescending(r => r.EndedAt).First();
                return new PomodoroTaskStatistics(g.Key.Linked ? g.Key.Key : null, Title(latest),
                    g.Count(r => r.Completed), g.Count(r => !r.Completed),
                    g.Where(r => r.Completed).Sum(r => r.FocusSeconds), g.Where(r => !r.Completed).Sum(r => r.FocusSeconds),
                    totalSeconds == 0 ? 0 : g.Sum(r => r.FocusSeconds) / totalSeconds);
            }).OrderByDescending(t => t.FocusSeconds).ThenBy(t => t.Title, StringComparer.Ordinal).ToList();
        var completedDates = all.Where(r => r.Record.Completed).Select(r => r.Date).Distinct().Order().ToList();
        var set = completedDates.ToHashSet();
        var current = 0;
        var cursor = set.Contains(today) ? today : today > DateTime.MinValue ? today.AddDays(-1) : today;
        while (set.Contains(cursor))
        {
            current++;
            if (cursor == DateTime.MinValue) break;
            cursor = cursor.AddDays(-1);
        }
        var longest = 0; var run = 0; DateTime? previous = null;
        foreach (var date in completedDates)
        {
            run = previous.HasValue && (date - previous.Value).Days == 1 ? run + 1 : 1;
            longest = Math.Max(longest, run); previous = date;
        }
        return new PomodoroStatisticsResult
        {
            Start = start, End = end, Days = days, Tasks = tasks,
            DailyGoal = Math.Clamp(dailyGoal, 1, 30),
            TodayCompleted = all.Count(r => r.Date == today && r.Record.Completed),
            CurrentStreak = current, LongestStreak = longest
        };
    }

    private static string Title(PomodoroRecord record) => string.IsNullOrWhiteSpace(record.TaskTitle) ? "自由专注" : record.TaskTitle.Trim();

    public static string ToCsv(PomodoroStatisticsResult result)
    {
        static string Cell(string value)
        {
            // Prevent spreadsheet formulas while preserving commas, quotes and newlines.
            if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        var csv = new StringBuilder("类型,日期或事项,完成番茄,中断次数,专注分钟,完成专注分钟,中断专注分钟,时长占比\r\n");
        void Row(string kind, string name, int complete, int interrupted, double good, double partial, string share = "") =>
            csv.AppendLine(string.Join(",", Cell(kind), Cell(name), complete, interrupted,
                Number((good + partial) / 60), Number(good / 60), Number(partial / 60), share));
        Row("汇总", $"{result.Start:yyyy-MM-dd} 至 {result.End:yyyy-MM-dd}", result.Completed, result.Interrupted,
            result.Days.Sum(d => d.CompletedSeconds), result.Days.Sum(d => d.InterruptedSeconds));
        foreach (var d in result.Days) Row("每日", d.Date.ToString("yyyy-MM-dd"), d.Completed, d.Interrupted, d.CompletedSeconds, d.InterruptedSeconds);
        foreach (var t in result.Tasks) Row("事项", t.Title, t.Completed, t.Interrupted, t.CompletedSeconds, t.InterruptedSeconds, Number(t.Share * 100) + "%");
        return csv.ToString();
    }
}
