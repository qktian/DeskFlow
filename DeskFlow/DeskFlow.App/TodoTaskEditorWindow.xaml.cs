using System.Windows;
using System.Windows.Controls;
using DeskFlow.Core.Models;

namespace DeskFlow.App;

public partial class TodoTaskEditorWindow : Window
{
    private readonly TodoItemModel? _edit;

    public TodoTaskEditorWindow(TodoItemModel? edit)
    {
        InitializeComponent();
        _edit = edit;

        KindBox.ItemsSource = new[]
        {
            new ComboItem<TodoKind>("普通待办", TodoKind.Normal),
            new ComboItem<TodoKind>("循环待办", TodoKind.Recurring)
        };
        KindBox.DisplayMemberPath = nameof(ComboItem<TodoKind>.Label);
        KindBox.SelectedValuePath = nameof(ComboItem<TodoKind>.Value);

        PriorityBox.ItemsSource = new[]
        {
            new ComboItem<TodoPriority>("重要且紧急", TodoPriority.ImportantUrgent),
            new ComboItem<TodoPriority>("重要不紧急", TodoPriority.Important),
            new ComboItem<TodoPriority>("紧急不重要", TodoPriority.Urgent),
            new ComboItem<TodoPriority>("不重要不紧急", TodoPriority.Normal)
        };
        PriorityBox.DisplayMemberPath = nameof(ComboItem<TodoPriority>.Label);
        PriorityBox.SelectedValuePath = nameof(ComboItem<TodoPriority>.Value);

        PeriodBox.ItemsSource = new[]
        {
            new ComboItem<RecurrencePeriod>("每日", RecurrencePeriod.Daily),
            new ComboItem<RecurrencePeriod>("每周", RecurrencePeriod.Weekly),
            new ComboItem<RecurrencePeriod>("每两周", RecurrencePeriod.Biweekly),
            new ComboItem<RecurrencePeriod>("每月", RecurrencePeriod.Monthly)
        };
        PeriodBox.DisplayMemberPath = nameof(ComboItem<RecurrencePeriod>.Label);
        PeriodBox.SelectedValuePath = nameof(ComboItem<RecurrencePeriod>.Value);

        Title = edit is null ? "新建任务" : "编辑任务";

        if (edit is null)
        {
            KindBox.SelectedIndex = 0;
            PriorityBox.SelectedValue = TodoPriority.Normal;
            // 默认截止日期为当日 23:00
            DueDateBox.SelectedDate = DateTime.Today;
            DueTimeBox.Text = "23:00";
            PeriodBox.SelectedValue = RecurrencePeriod.Daily;
            StartDateBox.SelectedDate = DateTime.Today;
            SaveButton.IsEnabled = false;
            UpdateVisibility();
            return;
        }

        TitleBox.Text = edit.Title;
        KindBox.SelectedValue = edit.Kind;
        PriorityBox.SelectedValue = edit.Priority;
        DoneBox.IsChecked = edit.Completed;
        PeriodBox.SelectedValue = edit.Period;
        StartDateBox.SelectedDate = edit.StartDate == default ? DateTime.Today : edit.StartDate;
        if (edit.DueDate is { } due)
        {
            DueDateBox.SelectedDate = due.Date;
            DueTimeBox.Text = due.ToString("HH:mm");
        }

        UpdateVisibility();
    }

    public TodoItemModel? Result { get; private set; }

    private void KindBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateVisibility();

    private void UpdateVisibility()
    {
        var isRecurring = KindBox.SelectedValue is TodoKind.Recurring;
        DuePanel.Visibility = isRecurring ? Visibility.Collapsed : Visibility.Visible;
        CyclePanel.Visibility = isRecurring ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TitleBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (SaveButton is not null)
        {
            SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(TitleBox.Text);
        }
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return;
        }

        var model = _edit ?? new TodoItemModel();
        model.Title = title;
        model.Kind = KindBox.SelectedValue is TodoKind k ? k : TodoKind.Normal;
        model.Priority = PriorityBox.SelectedValue is TodoPriority p ? p : TodoPriority.Normal;
        model.Completed = DoneBox.IsChecked == true;
        model.CompletedAt = model.Completed ? DateTime.Now : null;

        if (model.Kind == TodoKind.Recurring)
        {
            model.Period = PeriodBox.SelectedValue is RecurrencePeriod rp ? rp : RecurrencePeriod.Daily;
            model.StartDate = StartDateBox.SelectedDate ?? DateTime.Today;
            // 循环任务沿用截止字段：留空表示每个周期当天 23:59 截止（展示层不强调）
        }
        else
        {
            DateTime? due = null;
            if (DueDateBox.SelectedDate is { } d)
            {
                due = d;
                if (TimeSpan.TryParse(DueTimeBox.Text?.Trim(), out var t))
                {
                    due = d.Add(t);
                }
            }

            model.DueDate = due;
        }

        model.Normalize();
        Result = model;
        DialogResult = true;
    }
}
