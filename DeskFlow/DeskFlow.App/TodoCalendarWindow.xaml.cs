using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskFlow.App.Services;
using DeskFlow.Core.Models;
using DeskFlow.Core.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace DeskFlow.App;

/// <summary>循环待办的「当月日历打卡」窗口：点选应做日切换完成 / 取消完成。</summary>
public partial class TodoCalendarWindow : Window
{
    private readonly TodoItemModel _task;
    private readonly TodoManager _manager;
    private readonly SolidColorBrush _textHigh = new(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _textFaint = new(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _red = new(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private readonly SolidColorBrush _green = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private readonly SolidColorBrush _blue = new(Color.FromRgb(0x38, 0x9F, 0xE0));
    private readonly SolidColorBrush _greyDot = new(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
    private int _year;
    private int _month;

    public TodoCalendarWindow(TodoItemModel task, TodoManager manager)
    {
        InitializeComponent();
        _task = task;
        _manager = manager;

        Title = "打卡日历";
        TaskTitle.Text = $"{task.Title}（{TodoVisual.PeriodText(task.Period)}）";

        var now = DateTime.Today;
        _year = now.Year;
        _month = now.Month;
        Render();
    }

    private void PrevMonth_OnClick(object sender, RoutedEventArgs e)
    {
        _month--;
        if (_month < 1)
        {
            _month = 12;
            _year--;
        }

        Render();
    }

    private void NextMonth_OnClick(object sender, RoutedEventArgs e)
    {
        _month++;
        if (_month > 12)
        {
            _month = 1;
            _year++;
        }

        Render();
    }

    private void Render()
    {
        MonthTitle.Text = $"{_year} 年 {_month} 月";

        CalendarBody.Children.Clear();
        CalendarBody.RowDefinitions.Clear();

        var today = DateTime.Today;
        var first = new DateTime(_year, _month, 1);
        var days = DateTime.DaysInMonth(_year, _month);
        var leading = ((int)first.DayOfWeek + 6) % 7; // 周一开头

        var occurrenceDates = TodoRecurrence
            .OccurrencesInMonth(_task, _year, _month)
            .ToHashSet();

        var row = 0;
        for (var day = 1; day <= days; day++)
        {
            var col = (leading + day - 1) % 7;
            if (col == 0 && day > 1)
            {
                row++;
                AddRow(row);
            }
            else if (day == 1)
            {
                AddRow(0);
            }

            var date = new DateTime(_year, _month, day);
            var due = occurrenceDates.Contains(date);
            var key = TodoRecurrence.OccurrenceKey(date);
            var done = due && _task.CheckIns.TryGetValue(key, out var v) && v;

            var cell = BuildCell(date, due, done, date.Date < today.Date, date.Date == today);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            CalendarBody.Children.Add(cell);
        }
    }

    private void AddRow(int index)
    {
        while (CalendarBody.RowDefinitions.Count <= index)
        {
            CalendarBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        }
    }

    private Border BuildCell(DateTime date, bool due, bool done, bool isPast, bool isToday)
    {
        var cell = new Border
        {
            Height = 32,
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Background = Brushes.Transparent,
            Cursor = due ? Cursors.Hand : null
        };

        if (done)
        {
            cell.Background = _green;
            cell.Child = new TextBlock
            {
                Text = "✓",
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var number = new TextBlock
            {
                Text = date.Day.ToString(),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            stack.Children.Add(number);

            if (due)
            {
                if (isToday)
                {
                    cell.BorderBrush = _blue;
                    cell.BorderThickness = new Thickness(2);
                    number.Foreground = _textHigh;
                }
                else if (isPast)
                {
                    // 已过期的应做日未完成 = 未打卡（红）
                    number.Foreground = _red;
                    number.FontWeight = FontWeights.SemiBold;
                    AddDot(stack, _red);
                }
                else
                {
                    // 未来应做日 = 计划
                    number.Foreground = _textFaint;
                    AddDot(stack, _greyDot);
                }
            }
            else
            {
                number.Foreground = _textFaint;
                cell.Cursor = null;
            }

            cell.Child = stack;
        }

        if (due && date.Date <= DateTime.Today)
        {
            var captured = date;
            cell.MouseLeftButtonUp += (_, _) => Toggle(captured);
        }

        return cell;
    }

    private static void AddDot(StackPanel stack, Brush color)
    {
        stack.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Margin = new Thickness(0, 1, 0, 0),
            Fill = color,
            HorizontalAlignment = HorizontalAlignment.Center
        });
    }

    private void Toggle(DateTime date)
    {
        if (date.Date > DateTime.Today)
        {
            return;
        }

        var key = TodoRecurrence.OccurrenceKey(date);
        _task.CheckIns.TryGetValue(key, out var done);
        _task.CheckIns[key] = !done;
        _manager.Save();
        Render();
        if (!done) MissionCompleteEffect.Play(this);
    }
}
