using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskFlow.App.Services;
using DeskFlow.Core.Models;
using DeskFlow.Core.Services;
using DeskFlow.Shell;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace DeskFlow.App;

public partial class PomodoroWindow : Window
{
    private readonly PomodoroManager _manager;
    private PomodoroEngine Engine => _manager.Engine;
    private bool _ready;
    private bool _loadingTasks;
    private int _historyCount = -1;
    private DateTime _statsDate;
    private int _todayCount;
    private double _todayMinutes;
    public bool AppClosing { get; set; }
    private sealed record TaskChoice(string? Id, string Title);
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(202, 98, 89));
    private static readonly Brush Neutral = new SolidColorBrush(Color.FromRgb(48, 59, 75));

    public PomodoroWindow(PomodoroManager manager)
    {
        _manager = manager;
        InitializeComponent();
        _ready = true;
        InitializeStatistics();
        LoadSettings(); LoadTasks(); ApplyCompact();
        _manager.Refreshed += Refresh;
        Loaded += (_, _) => { EnsureOnScreen(); Refresh(); };
        Closed += (_, _) =>
        {
            _manager.Refreshed -= Refresh;
            if (!AppClosing) Engine.Data.ShowPanel = false;
            _manager.SaveAndRefresh();
        };
        Refresh();
    }

    public void EnsureOnScreen()
    {
        var data = Engine.Data;
        var monitor = ScreenHelper.ForId(string.IsNullOrWhiteSpace(data.ScreenId) ? _manager.Config.General.PreferredMonitorId : data.ScreenId);
        Height = data.Compact ? 150 : Math.Min(560, monitor.WorkArea.Height);
        Width = Math.Min(data.Compact ? 300 : 380, monitor.WorkArea.Width);
        var rect = ScreenHelper.ClampToMonitor(new Rect(monitor.WorkArea.Left + data.X, monitor.WorkArea.Top + data.Y, Width, Height), monitor);
        Left = rect.Left; Top = rect.Top;
        RememberPosition();
    }

    private void RememberPosition()
    {
        var monitor = ScreenHelper.FromWindow(this);
        Engine.Data.ScreenId = monitor.Id;
        Engine.Data.X = Left - monitor.WorkArea.Left;
        Engine.Data.Y = Top - monitor.WorkArea.Top;
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Button events must not start a window drag.
        if (e.OriginalSource is DependencyObject node)
            for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
                if (current is Button) return;
        if (e.ClickCount == 2) { Compact_OnClick(sender, e); return; }
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove(); RememberPosition(); EnsureOnScreen(); _manager.SaveAndRefresh();
        }
    }

    private void Refresh()
    {
        if (!_ready) return;
        var s = Engine.Session;
        var remaining = (int)Math.Ceiling(s.RemainingSeconds);
        var time = $"{remaining / 60:00}:{remaining % 60:00}";
        TimeText.Text = CompactTime.Text = time;
        var status = s.Status switch { PomodoroStatus.Running => "进行中", PomodoroStatus.Paused => "已暂停", _ => "准备开始" };
        PhaseText.Text = CompactPhase.Text = $"{PomodoroEngine.Label(s.Phase)} · {status}";
        Title = $"{time} · {PomodoroEngine.Label(s.Phase)} · DeskFlow";
        Progress.Value = Math.Clamp(1 - s.RemainingSeconds / s.DurationSeconds, 0, 1);
        CycleText.Text = $"本轮完成 {s.CompletedInCycle} / {Engine.Data.Settings.LongBreakEvery} 个 · 随后长休息";
        StartButton.Content = CompactStart.Content = s.Status switch
        {
            PomodoroStatus.Running => "暂停", PomodoroStatus.Paused => "继续", _ => $"开始{PomodoroEngine.Label(s.Phase)}"
        };
        FocusButton.Background = s.Phase == PomodoroPhase.Focus ? Accent : Neutral;
        ShortButton.Background = s.Phase == PomodoroPhase.ShortBreak ? Accent : Neutral;
        LongButton.Background = s.Phase == PomodoroPhase.LongBreak ? Accent : Neutral;
        TaskBox.IsEnabled = FreeTitleBox.IsEnabled = s.Status == PomodoroStatus.Ready && s.StartedAt is null;
        FreeTitleBox.Visibility = s.TaskId is null ? Visibility.Visible : Visibility.Collapsed;
        CompactTask.Text = s.TaskTitle;
        NoticeText.Text = string.IsNullOrEmpty(Engine.Notice)
            ? "关闭面板后计时继续。可从托盘打开，或在待办右键菜单中选择关联任务。" : Engine.Notice;
        Topmost = Engine.Data.Settings.AlwaysOnTop;
        PinButton.Content = Topmost ? "已置顶" : "置顶";
        if (_historyCount != Engine.Data.History.Count || _statsDate != DateTime.Today)
        {
            _historyCount = Engine.Data.History.Count; _statsDate = DateTime.Today;
            _todayCount = Engine.Data.History.Count(r => r.Completed && r.EndedAt.LocalDateTime.Date == DateTime.Today);
            _todayMinutes = Engine.Data.History.Where(r => r.EndedAt.LocalDateTime.Date == DateTime.Today).Sum(r => r.FocusSeconds) / 60;
            RefreshStats(); RefreshHistory();
            _statisticsDirty = true;
        }
        // A changed daily goal must update even when no record was added.
        var today = _todayCount;
        var minutes = _todayMinutes;
        TodayText.Text = $"今日 {today} / {Engine.Data.Settings.DailyGoal} 个番茄 · {minutes:0.#} 分钟";
        GoalProgress.Maximum = Engine.Data.Settings.DailyGoal;
        GoalProgress.Value = Math.Min(today, Engine.Data.Settings.DailyGoal);
        if (MainTabs.SelectedItem == StatisticsTab && (_statisticsDirty || _statisticsGoal != Engine.Data.Settings.DailyGoal)) RefreshStatistics();
    }

    private void LoadTasks()
    {
        _loadingTasks = true;
        var choices = new List<TaskChoice> { new(null, "自由专注（不关联待办）") };
        foreach (var board in _manager.Config.TodoBoards)
            foreach (var task in board.Items.Where(t => t.Kind == TodoKind.Normal ? !t.Completed : t.ClosedAt is null))
                choices.Add(new(task.Id, $"{board.Name} / {task.Title}"));
        var s = Engine.Session;
        if (s.TaskId is not null && choices.All(c => c.Id != s.TaskId))
            choices.Add(new(s.TaskId, $"{s.TaskTitle}（已完成或移除）"));
        TaskBox.ItemsSource = choices;
        TaskBox.SelectedItem = choices.First(c => c.Id == s.TaskId);
        if (!FreeTitleBox.IsKeyboardFocusWithin) FreeTitleBox.Text = s.TaskId is null ? s.TaskTitle : "自由专注";
        _loadingTasks = false;
    }

    private void TaskBox_OnDropDownOpened(object? sender, EventArgs e) => LoadTasks();
    private void TaskBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loadingTasks || !TaskBox.IsEnabled || TaskBox.SelectedItem is not TaskChoice choice) return;
        Engine.SelectTask(choice.Id, choice.Id is null ? FreeTitleBox.Text : choice.Title);
        Refresh();
    }
    private void SaveFreeTitle()
    {
        if (!_ready || Engine.Session.TaskId is not null || !FreeTitleBox.IsEnabled) return;
        Engine.SelectTask(null, FreeTitleBox.Text);
    }
    private void FreeTitle_OnLostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveFreeTitle();
    private void FreeTitle_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SaveFreeTitle(); Keyboard.ClearFocus(); e.Handled = true; }
    }

    public void UseTask(TodoItemModel task)
    {
        Engine.Data.Compact = false; ApplyCompact(); EnsureOnScreen();
        if (Engine.Session.TaskId == task.Id && Engine.Session.Phase == PomodoroPhase.Focus)
        { MainTabs.SelectedIndex = 0; return; }
        if (!ConfirmInterruption("切换待办")) return;
        Engine.SwitchTo(PomodoroPhase.Focus);
        var board = _manager.Config.TodoBoards.FirstOrDefault(b => b.Items.Any(t => t.Id == task.Id));
        Engine.SelectTask(task.Id, board is null ? task.Title : $"{board.Name} / {task.Title}");
        MainTabs.SelectedIndex = 0;
        LoadTasks(); Refresh();
    }

    private bool ConfirmInterruption(string action)
    {
        if (Engine.Session.StartedAt is null) return true;
        return MessageBox.Show(this, $"{action}会结束当前这一段。\n已用专注时间会记为中断，不增加番茄数。是否继续？", "番茄钟", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
    private void Start_OnClick(object sender, RoutedEventArgs e) { SaveFreeTitle(); _manager.Toggle(); }
    private void Reset_OnClick(object sender, RoutedEventArgs e)
    { if (ConfirmInterruption("重置")) Engine.Reset(); }
    private void Skip_OnClick(object sender, RoutedEventArgs e)
    { if (ConfirmInterruption("跳过")) Engine.Skip(); }
    private void Phase_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<PomodoroPhase>(tag, out var phase) || phase == Engine.Session.Phase) return;
        if (ConfirmInterruption("切换阶段")) Engine.SwitchTo(phase);
    }
    private void ResetCycle_OnClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "结束当前计时并清零本轮进度？历史记录保留。", "重新开始一轮", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Engine.Reset(true); MainTabs.SelectedIndex = 0;
    }
    private void Hide_OnClick(object sender, RoutedEventArgs e) => Close();
    private void Pin_OnClick(object sender, RoutedEventArgs e)
    { Engine.Data.Settings.AlwaysOnTop = !Engine.Data.Settings.AlwaysOnTop; _manager.SaveAndRefresh(); }
    private void Compact_OnClick(object sender, RoutedEventArgs e)
    { Engine.Data.Compact = !Engine.Data.Compact; ApplyCompact(); EnsureOnScreen(); _manager.SaveAndRefresh(); }
    private void ApplyCompact()
    {
        MainTabs.Visibility = Engine.Data.Compact ? Visibility.Collapsed : Visibility.Visible;
        CompactPanel.Visibility = Engine.Data.Compact ? Visibility.Visible : Visibility.Collapsed;
        CompactButton.Content = Engine.Data.Compact ? "展开" : "精简";
    }

    private void LoadSettings()
    {
        var s = Engine.Data.Settings;
        FocusInput.Text = s.FocusMinutes.ToString(); ShortInput.Text = s.ShortBreakMinutes.ToString();
        LongInput.Text = s.LongBreakMinutes.ToString(); EveryInput.Text = s.LongBreakEvery.ToString(); GoalInput.Text = s.DailyGoal.ToString();
        AutoBreakCheck.IsChecked = s.AutoStartBreak; AutoFocusCheck.IsChecked = s.AutoStartFocus;
        SoundCheck.IsChecked = s.SoundEnabled; NotifyCheck.IsChecked = s.NotificationEnabled;
    }
    private void SaveSettings_OnClick(object sender, RoutedEventArgs e)
    {
        var fields = new[] { (FocusInput, 1, 180, "专注时长"), (ShortInput, 1, 60, "短休息时长"),
            (LongInput, 1, 120, "长休息时长"), (EveryInput, 2, 12, "长休息间隔"), (GoalInput, 1, 30, "每日目标") };
        foreach (var (box, min, max, name) in fields)
            if (!int.TryParse(box.Text, out var value) || value < min || value > max)
            { SettingsMessage.Text = $"{name}请输入 {min}–{max} 的整数。"; box.Focus(); box.SelectAll(); return; }
        Engine.ApplySettings(new PomodoroSettings
        {
            FocusMinutes = int.Parse(FocusInput.Text), ShortBreakMinutes = int.Parse(ShortInput.Text),
            LongBreakMinutes = int.Parse(LongInput.Text), LongBreakEvery = int.Parse(EveryInput.Text), DailyGoal = int.Parse(GoalInput.Text),
            AutoStartBreak = AutoBreakCheck.IsChecked == true, AutoStartFocus = AutoFocusCheck.IsChecked == true,
            SoundEnabled = SoundCheck.IsChecked == true, NotificationEnabled = NotifyCheck.IsChecked == true,
            AlwaysOnTop = Engine.Data.Settings.AlwaysOnTop
        });
        SettingsMessage.Text = "设置已应用。当前已开始的计时保持原时长。";
    }
    private void Tabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || e.Source != MainTabs) return;
        if (MainTabs.SelectedItem == TimerTab) LoadTasks();
        if (MainTabs.SelectedItem == HistoryTab) { RefreshStats(); RefreshHistory(); }
        if (MainTabs.SelectedItem == StatisticsTab) RefreshStatistics();
    }
    private void HistoryFilter_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (_ready) RefreshHistory(); }
    private void RefreshStats()
    {
        var all = Engine.Data.History;
        var week = all.Where(r => r.EndedAt.LocalDateTime.Date >= DateTime.Today.AddDays(-6) && r.EndedAt.LocalDateTime.Date <= DateTime.Today).ToList();
        StatsText.Text = $"近 7 天  {week.Count(r => r.Completed)} 个番茄 · {week.Sum(r => r.FocusSeconds) / 60:0.#} 分钟\n累计      {all.Count(r => r.Completed)} 个番茄 · {all.Sum(r => r.FocusSeconds) / 60:0.#} 分钟";
    }
    private void RefreshHistory()
    {
        var cutoff = HistoryFilter.SelectedIndex switch { 0 => DateTime.Today, 1 => DateTime.Today.AddDays(-6), _ => DateTime.MinValue };
        var rows = Engine.Data.History.Where(r => r.EndedAt.LocalDateTime.Date >= cutoff).OrderByDescending(r => r.EndedAt).Select(r => new
        {
            Title = r.TaskTitle,
            Detail = $"{r.EndedAt.LocalDateTime:MM-dd HH:mm} · {(r.Completed ? "已完成" : "中断")} · {r.FocusSeconds / 60:0.#} 分钟"
        }).ToList();
        HistoryList.ItemsSource = rows;
        HistoryHint.Text = (rows.Count == 0 ? "暂无记录。\n" : "") + "只将完整专注计入番茄数；分钟含已记录的中断专注，按结束日期统计。";
    }
    private void Export_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "导出番茄钟记录", Filter = "CSV 文件|*.csv", FileName = $"DeskFlow-专注记录-{DateTime.Today:yyyyMMdd}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            static string Cell(string value)
            {
                if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            var lines = new List<string> { "开始时间,结束时间,事项,专注分钟,状态" };
            lines.AddRange(Engine.Data.History.OrderBy(r => r.EndedAt).Select(r => string.Join(",",
                Cell(r.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")), Cell(r.EndedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")),
                Cell(r.TaskTitle), (r.FocusSeconds / 60).ToString("0.##", CultureInfo.InvariantCulture), r.Completed ? "已完成" : "中断")));
            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
            MessageBox.Show(this, "全部专注记录已导出。", "番茄钟");
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("Pomodoro", "CSV export failed", error);
            MessageBox.Show(this, $"导出失败：{error.Message}", "番茄钟", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
