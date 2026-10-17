using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskFlow.Core.Services;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace DeskFlow.App;

public partial class PomodoroWindow
{
    private bool _statisticsDirty = true;
    private int _statisticsGoal = -1;
    private DateTime _customStatisticsStart;
    private DateTime _customStatisticsEnd;
    private PomodoroStatisticsResult? _statistics;
    private static readonly Brush CompleteChartBrush = new SolidColorBrush(Color.FromRgb(113, 187, 163));
    private static readonly Brush InterruptedChartBrush = new SolidColorBrush(Color.FromRgb(239, 128, 117));
    private static readonly Brush ChartLabelBrush = new SolidColorBrush(Color.FromRgb(170, 180, 194));

    private void InitializeStatistics()
    {
        _customStatisticsStart = DateTime.Today.AddDays(-6); _customStatisticsEnd = DateTime.Today;
        StatisticsStart.SelectedDate = _customStatisticsStart; StatisticsEnd.SelectedDate = _customStatisticsEnd;
        StatisticsStart.DisplayDateEnd = StatisticsEnd.DisplayDateEnd = DateTime.Today;
    }

    private void StatisticsRange_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var custom = StatisticsRange.SelectedIndex == 3;
        CustomStatisticsRange.Visibility = ApplyStatisticsRange.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        StatisticsError.Text = ""; RefreshStatistics();
    }

    private void ApplyStatisticsRange_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Also validate typed (not only calendar-picked) input before using SelectedDate.
            if (!DateTime.TryParse(StatisticsStart.Text, out var start) || !DateTime.TryParse(StatisticsEnd.Text, out var end))
                throw new ArgumentException("请填写有效的开始日期和结束日期。");
            if (start.Date > end.Date || end.Date > DateTime.Today || (end.Date - start.Date).Days >= 366)
                throw new ArgumentException("请选择不晚于今天、开始不晚于结束且不超过 366 天的日期范围。");
            _customStatisticsStart = start.Date; _customStatisticsEnd = end.Date;
            StatisticsError.Text = ""; RefreshStatistics();
        }
        catch (ArgumentException error) { StatisticsError.Text = error.Message + " 当前仍显示上次应用的范围。"; }
    }

    private void StatisticsMetric_OnChanged(object sender, SelectionChangedEventArgs e)
    { if (_ready && _statistics is not null) DrawStatisticsChart(); }

    private static string Duration(double seconds)
    {
        if (seconds > 0 && seconds < 60) return $"{seconds:0.#} 秒";
        var minutes = seconds / 60;
        if (minutes < 60) return $"{minutes:0.#} 分钟";
        if (minutes >= 6000) return $"{minutes / 60:0.#} 小时";
        var rounded = (int)Math.Round(minutes);
        return $"{rounded / 60} 小时 {rounded % 60} 分";
    }

    private void RefreshStatistics()
    {
        if (!_ready) return;
        var end = DateTime.Today;
        var start = StatisticsRange.SelectedIndex switch { 0 => end, 2 => end.AddDays(-29), _ => end.AddDays(-6) };
        if (StatisticsRange.SelectedIndex == 3) { start = _customStatisticsStart; end = _customStatisticsEnd; }
        try
        {
            _statistics = PomodoroStatistics.Calculate(Engine.Data.History, start, end, DateTime.Today, Engine.Data.Settings.DailyGoal);
        }
        catch (ArgumentException error) { StatisticsError.Text = error.Message; return; }
        var stats = _statistics;
        _statisticsDirty = false; _statisticsGoal = stats.DailyGoal;
        StatisticsStart.DisplayDateEnd = StatisticsEnd.DisplayDateEnd = DateTime.Today;
        StatisticsRangeLabel.Text = $"{stats.Start:yyyy.MM.dd} — {stats.End:yyyy.MM.dd} · {stats.Days.Count} 天";
        StatisticsDuration.Text = Duration(stats.FocusSeconds);
        StatisticsCompleted.Text = $"{stats.Completed} 个";
        StatisticsRate.Text = stats.CompletionRate is { } rate ? $"{rate:P0}" : "—";
        StatisticsInterrupted.Text = $"{stats.Interrupted} 次";
        StatisticsActivity.Text = $"有专注记录 {stats.ActiveDays} 天 · 日均 {Duration(stats.AverageDailySeconds)}（含无记录日期）";
        StatisticsTodayGoal.Text = $"今日目标  {stats.TodayCompleted} / {stats.DailyGoal} 个" +
            (stats.TodayCompleted >= stats.DailyGoal ? " · 已达成" : $" · 还差 {stats.DailyGoal - stats.TodayCompleted} 个");
        StatisticsGoalProgress.Maximum = stats.DailyGoal;
        StatisticsGoalProgress.Value = Math.Min(stats.TodayCompleted, stats.DailyGoal);
        StatisticsStreak.Text = $"当前连续专注 {stats.CurrentStreak} 天 · 历史最长 {stats.LongestStreak} 天";
        StatisticsTasks.ItemsSource = stats.Tasks.Select(t => new
        {
            t.Title, SharePercent = t.Share * 100,
            Detail = $"{Duration(t.FocusSeconds)} · 占比 {t.Share:P1} · 完成 {t.Completed} / 中断 {t.Interrupted}"
        }).ToList();
        StatisticsEmpty.Visibility = stats.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DrawStatisticsChart();
    }

    private void DrawStatisticsChart()
    {
        if (_statistics is not { } stats) return;
        StatisticsChart.Children.Clear();
        var countMode = StatisticsMetric.SelectedIndex == 1;
        var maximum = stats.Days.Max(d => countMode ? d.Completed : d.FocusSeconds / 60);
        var scale = maximum > 0 ? maximum : 1;
        var width = Math.Max(330, stats.Days.Count * 40 + 16);
        StatisticsChart.Width = width;
        var slot = (width - 16d) / stats.Days.Count;
        const double baseline = 146, plotHeight = 112;
        foreach (var y in new[] { baseline - plotHeight, baseline - plotHeight / 2, baseline })
            StatisticsChart.Children.Add(new Line { X1 = 8, X2 = width - 8, Y1 = y, Y2 = y, Stroke = Neutral, StrokeThickness = 1 });
        var maxLabel = new TextBlock { Text = $"最高 {maximum:0.#} {(countMode ? "个" : "分钟")}", Foreground = ChartLabelBrush, FontSize = 11 };
        Canvas.SetLeft(maxLabel, 8); Canvas.SetTop(maxLabel, 6); StatisticsChart.Children.Add(maxLabel);
        StatisticsChartLegend.Text = countMode ? "绿色：完整完成的番茄" : "绿色：完成专注时长   红色：中断已用时长";
        StatisticsChartHint.Text = "横向滚动查看更多日期；悬停或点击柱形查看当天数据。";
        for (var i = 0; i < stats.Days.Count; i++)
        {
            var day = stats.Days[i];
            var good = countMode ? day.Completed : day.CompletedSeconds / 60;
            var partial = countMode ? 0 : day.InterruptedSeconds / 60;
            var detail = $"{day.Date:yyyy-MM-dd}：{Duration(day.FocusSeconds)}，完成 {day.Completed} 个，中断 {day.Interrupted} 次";
            var canvas = new Canvas { Width = slot, Height = 172 };
            var hit = new Border { Width = slot, Height = 172, Background = Brushes.Transparent, Child = canvas, ToolTip = detail, Cursor = Cursors.Hand };
            hit.MouseLeftButtonDown += (_, _) => StatisticsChartHint.Text = detail;
            var barWidth = Math.Min(24, slot - 8);
            void Segment(double value, double offset, Brush brush)
            {
                if (value <= 0) return;
                var height = value / scale * plotHeight;
                var bar = new Rectangle { Width = barWidth, Height = height, Fill = brush };
                Canvas.SetLeft(bar, (slot - barWidth) / 2); Canvas.SetTop(bar, baseline - offset / scale * plotHeight - height);
                canvas.Children.Add(bar);
            }
            Segment(good, 0, CompleteChartBrush); Segment(partial, good, InterruptedChartBrush);
            var label = new TextBlock { Text = day.Date.ToString("MM/dd"), Width = slot, TextAlignment = TextAlignment.Center, Foreground = ChartLabelBrush, FontSize = 10 };
            Canvas.SetTop(label, baseline + 7); canvas.Children.Add(label);
            Canvas.SetLeft(hit, 8 + i * slot); StatisticsChart.Children.Add(hit);
        }
    }

    private void ExportStatistics_OnClick(object sender, RoutedEventArgs e)
    {
        RefreshStatistics();
        if (_statistics is not { } stats) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出当前范围统计", Filter = "CSV 文件|*.csv",
            FileName = $"DeskFlow-专注统计-{stats.Start:yyyyMMdd}-{stats.End:yyyyMMdd}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, PomodoroStatistics.ToCsv(stats), new UTF8Encoding(true));
            MessageBox.Show(this, "统计已导出，包含汇总、每日数据和全部事项分布。", "番茄钟统计");
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("Pomodoro", "Statistics export failed", error);
            MessageBox.Show(this, $"导出失败：{error.Message}", "番茄钟统计", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
