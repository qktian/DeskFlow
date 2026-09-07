using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskFlow.App.Services;
using DeskFlow.Core.Models;
using DeskFlow.Core.Services;
using DeskFlow.Shell;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace DeskFlow.App;

public partial class TodoBoardWindow : Window
{
    private readonly TodoBoardModel _model;
    private readonly TodoManager _manager;
    private Point? _dragStart;
    private Point? _dragOffset;

    public TodoBoardWindow(TodoBoardModel model, TodoManager manager)
    {
        InitializeComponent();
        _model = model;
        _manager = manager;

        SourceInitialized += OnSourceInitialized;
        Deactivated += (_, _) => SetBottomMost();
        LocationChanged += (_, _) => SyncToModel();
        SizeChanged += (_, _) => SyncToModel();
        Loaded += (_, _) => ScheduleAutoHeight();

        ApplyModel();
        Reload();
    }

    public TodoBoardModel Model => _model;

    public void ApplyModel()
    {
        Left = _model.X;
        Top = _model.Y;
        Width = Math.Clamp(_model.Width, 220, 700);
        TitleText.Text = _model.Name;
        SetCollapsedUi(_model.Collapsed);
    }

    /// <summary>应用面板外观（背景色 / 透明度 / 圆角 / 文字色），与普通盒子同源。</summary>
    public void ApplyStyle()
    {
        var style = _model.Style ?? new BoxStyle();
        var r = Math.Clamp(style.CornerRadius, 0, 24);

        Frame.Background = new SolidColorBrush(ParseColor(style.BackgroundColor, Color.FromArgb(0x33, 0, 0, 0)));
        Frame.CornerRadius = new CornerRadius(r);
        Opacity = Math.Clamp(style.Opacity, 0.2, 1.0);
        TitleText.Foreground = new SolidColorBrush(ParseColor(style.ForegroundColor, Colors.White));

        TitleBar.CornerRadius = _model.Collapsed
            ? new CornerRadius(r)
            : new CornerRadius(r, r, 0, 0);
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return (Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
            }
        }
        catch
        {
            // 颜色非法时用回退色
        }

        return fallback;
    }

    public void Reload()
    {
        // 顶层任务（不含被父任务引用但父不存在的孤儿，孤儿按顶层处理）
        var ids = _model.Items.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var top = _model.Items
            .Where(t => string.IsNullOrEmpty(t.ParentId) || !ids.Contains(t.ParentId))
            .Where(VisibleNode)
            .ToList();

        // 顶层排序：未完成在前（重要紧急优先、截止早优先），已完成沉底
        IEnumerable<TodoRowViewModel> ordered = top
            .Select(m => new TodoRowViewModel(m))
            .Where(v => !v.IsDone)
            .OrderBy(v => PriorityRank(v.Model.Priority))
            .ThenBy(v => v.Model.DueDate ?? DateTime.MaxValue)
            .Concat(top.Select(m => new TodoRowViewModel(m)).Where(v => v.IsDone));

        // 树形展开：父任务之后紧跟其子任务（每级缩进），子任务按加入顺序排列
        var result = new List<TodoRowViewModel>();
        foreach (var vm in ordered)
        {
            AppendWithChildren(result, vm.Model, 0);
        }

        TodoHost.ItemsSource = result;
        EmptyHint.Visibility = result.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = _model.Items.Count == 0
            ? "点击 ＋ 添加第一条待办"
            : "当前筛选下没有待办";

        ScheduleAutoHeight();
    }

    private void AppendWithChildren(List<TodoRowViewModel> result, TodoItemModel model, int level)
    {
        var children = _model.Items.Where(t => t.ParentId == model.Id).ToList();
        result.Add(new TodoRowViewModel(model) { Level = level, HasChildren = children.Count > 0 });

        // 折叠时不再输出子任务（其下更深层自然也不显示）
        if (model.ChildrenCollapsed)
        {
            return;
        }

        foreach (var child in children.Where(VisibleNode))
        {
            AppendWithChildren(result, child, level + 1);
        }
    }

    // ---------- 显示过滤 ----------

    /// <summary>
    /// 该任务是否应出现在当前面板中。
    /// 显示规则：
    /// - 未完成的普通任务每天都显示（超期标红），与截止时间早晚无关；
    /// - 循环任务在其当前周期内的每一天都显示（每日=当天，每周=本周，每两周=本周期，每月=本月）；
    /// - 已完成任务按全局设置显示 / 隐藏（父任务隐藏时若有未完成子任务则保留）。
    /// </summary>
    private bool VisibleNode(TodoItemModel item)
    {
        // 已关闭（归档）的循环任务只在「待办历史」中保留，不再占用面板
        if (item.Kind == TodoKind.Recurring && item.ClosedAt is not null)
        {
            return false;
        }

        var general = _manager.Config.General;

        if (general.ShowCompletedTodos)
        {
            return true;
        }

        // 隐藏已完成任务；但若其下有未完成子任务则保留，避免子任务随之消失
        if (!ItemDone(item))
        {
            return true;
        }

        return HasPendingDescendant(item);
    }

    private static bool ItemDone(TodoItemModel item)
    {
        return item.Kind == TodoKind.Normal
            ? item.Completed
            : TodoRecurrence.IsCompletedForCycle(item, DateTime.Today);
    }

    private bool HasPendingDescendant(TodoItemModel parent)
    {
        return _model.Items.Any(t => t.ParentId == parent.Id && (!ItemDone(t) || HasPendingDescendant(t)));
    }

    /// <summary>点击折叠按钮：切换该任务的子任务显示状态。</summary>
    private void Expander_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoRowViewModel vm })
        {
            return;
        }

        ToggleChildren(vm.Model);
        e.Handled = true;
    }

    private void ToggleChildren(TodoItemModel item)
    {
        item.ChildrenCollapsed = !item.ChildrenCollapsed;
        Reload();
        _manager.Save();
    }

    private static int PriorityRank(TodoPriority p) => p switch
    {
        TodoPriority.ImportantUrgent => 0,
        TodoPriority.Important => 1,
        TodoPriority.Urgent => 2,
        _ => 3
    };

    // ---------- 折叠 ----------

    private const double TitleBarHeight = 32d;

    // ---------- 高度自适应 ----------

    /// <summary>自适应时列表区的最小高度（空列表提示的占位）。</summary>
    private const double AutoMinContentHeight = 44d;

    /// <summary>自适应窗口最小高度 = 标题栏 + 空列表占位。</summary>
    private const double MinAutoHeight = TitleBarHeight + AutoMinContentHeight;

    /// <summary>列表区上下留白（ScrollViewer Margin 4+4 与底部余量）。</summary>
    private const double AutoContentPadding = 12d;

    /// <summary>单行的上下外边距之和（Margin="0,1,0,3"），ActualHeight 不含 Margin，需要补回。</summary>
    private const double RowSpacing = 4d;

    /// <summary>容器尚未生成时的单行估算高度，避免首帧高度塌成最小值。</summary>
    private const double EstimatedRowHeight = 50d;

    /// <summary>自适应生效条件：开启自适应且面板处于展开状态。</summary>
    private bool AutoHeightEnabled => _model.AutoHeight && !_model.Collapsed;

    /// <summary>展开状态下的目标高度（自适应时先取上次值，随后由任务行实际高度修正）。</summary>
    private double ExpandedHeight() => _model.AutoHeight
        ? Math.Clamp(_model.Height, MinAutoHeight, 1000)
        : Math.Clamp(_model.Height, 200, 1000);

    /// <summary>同步高度下限：自适应模式允许比手动模式更矮。</summary>
    private void ApplyHeightConstraints() =>
        MinHeight = _model.Collapsed ? TitleBarHeight : (_model.AutoHeight ? MinAutoHeight : 200);

    /// <summary>等列表完成一次布局后再量高度，否则量到的是旧行。</summary>
    private void ScheduleAutoHeight()
    {
        if (!AutoHeightEnabled)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            new Action(ApplyAutoHeight),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>按当前可见任务行的实际高度重算窗口高度，并在贴近屏幕下边缘时上移，保证不出屏。</summary>
    private void ApplyAutoHeight()
    {
        if (!AutoHeightEnabled || !IsLoaded)
        {
            return;
        }

        var rows = TodoHost.Items.Count;
        var content = rows == 0
            ? AutoMinContentHeight
            : MeasureRows(rows) + AutoContentPadding;
        var target = Math.Clamp(TitleBarHeight + content, MinAutoHeight, 1000);

        var monitor = ScreenHelper.FromPoint(new Point(Left + (Width / 2), Top + (Height / 2)));
        var area = monitor.WorkArea;

        // 上限：屏幕工作区高度（上下各留 12）
        target = Math.Min(target, Math.Max(MinAutoHeight, area.Height - 24));

        // 向下生长；触底则整体上移，仍放不下时再压缩高度
        var top = Top;
        if (top + target > area.Bottom - 12)
        {
            top = area.Bottom - 12 - target;
        }

        if (top < area.Top + 12)
        {
            top = area.Top + 12;
        }

        if (top + target > area.Bottom - 12)
        {
            target = Math.Max(MinAutoHeight, area.Bottom - 12 - top);
        }

        var changed = Math.Abs(Height - target) > 0.5 || Math.Abs(Top - top) > 0.5;
        Top = top;
        Height = target;
        SyncToModel();

        // 任务增删导致的高度 / 位置变化要落盘，否则下次启动会回到旧尺寸
        if (changed)
        {
            _manager.Save();
        }
    }

    /// <summary>累加各行容器的实际高度（行高随标题 / 副标题有无而不同，必须实测）。</summary>
    private double MeasureRows(int rows)
    {
        TodoHost.UpdateLayout();

        var total = 0d;
        var counted = 0;
        foreach (var item in TodoHost.Items)
        {
            if (TodoHost.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container)
            {
                var height = container.ActualHeight > 0 ? container.ActualHeight : container.DesiredSize.Height;
                total += height + RowSpacing;
                counted++;
            }
        }

        return counted == 0 ? rows * (EstimatedRowHeight + RowSpacing) : total;
    }

    /// <summary>
    /// 切换「高度自适应」：开启时立即按任务数重算；关闭时把当前高度固化并回到手动模式的约束。
    /// </summary>
    private void SetAutoHeight(bool enabled, bool keepCurrentHeight = false)
    {
        if (_model.AutoHeight == enabled)
        {
            return;
        }

        _model.AutoHeight = enabled;
        ApplyHeightConstraints();

        if (enabled)
        {
            ScheduleAutoHeight();
            return;
        }

        // 手动模式下高度下限回到 200：菜单关闭时把当前高度抬到合法区间；
        // 拖拽场景紧接着就会由 Resize 赋新值，这里保持原高度避免先弹一下。
        if (!_model.Collapsed && !keepCurrentHeight)
        {
            Height = Math.Clamp(Height, 200, 1000);
            _model.Height = Height;
        }
    }

    private void SetCollapsedUi(bool collapsed)
    {
        ContentRow.Height = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        TodoScroll.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ApplyHeightConstraints();
        Height = collapsed ? TitleBarHeight : ExpandedHeight();
        ApplyStyle();
    }

    private void CollapseButton_OnClick(object sender, RoutedEventArgs e)
    {
        _model.Collapsed = !_model.Collapsed;
        SetCollapsedUi(_model.Collapsed);
        ScheduleAutoHeight();
        _manager.Save();
    }

    // ---------- 尺寸调整 ----------

    private void ThumbTop_OnDragDelta(object sender, DragDeltaEventArgs e) => Resize(0, -e.VerticalChange, false, true);

    private void ThumbBottom_OnDragDelta(object sender, DragDeltaEventArgs e) => Resize(0, e.VerticalChange, false, false);

    private void ThumbLeft_OnDragDelta(object sender, DragDeltaEventArgs e) => Resize(-e.HorizontalChange, 0, true, false);

    private void ThumbRight_OnDragDelta(object sender, DragDeltaEventArgs e) => Resize(e.HorizontalChange, 0, false, false);

    private void ThumbBottomRight_OnDragDelta(object sender, DragDeltaEventArgs e) => Resize(e.HorizontalChange, e.VerticalChange, false, false);

    private void Thumb_OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        SyncToModel();
        _manager.Save();
    }

    /// <summary>调整面板尺寸（宽 220~700，高 200~1000，与配置归一化范围一致）。</summary>
    private void Resize(double deltaWidth, double deltaHeight, bool moveLeft, bool moveTop)
    {
        if (_model.Collapsed)
        {
            return;
        }

        // 手动调整高度即视为接管高度控制，自动关闭「高度自适应」（只改宽度不受影响）
        if (_model.AutoHeight && Math.Abs(deltaHeight) > 0.5)
        {
            SetAutoHeight(false, keepCurrentHeight: true);
        }

        var newWidth = Math.Clamp(Width + deltaWidth, 220, 700);
        var newHeight = Math.Clamp(Height + deltaHeight, 200, 1000);

        if (moveLeft)
        {
            Left -= newWidth - Width;
        }

        if (moveTop)
        {
            Top -= newHeight - Height;
        }

        Width = newWidth;
        Height = newHeight;
    }

    // ---------- 窗口拖拽 / Z 序 ----------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.MakeToolWindow(hwnd);
        SetBottomMost();
    }

    private void SetBottomMost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1)
        {
            CollapseButton_OnClick(sender, e);
            return;
        }

        if (e.OriginalSource is Button)
        {
            return;
        }

        // 记录「按下瞬间」鼠标相对窗口的偏移与窗口起点。
        // 拖动过程始终用起点计算目标位置（绝对算法），避免增量累加 + 窗口移动
        // 互相反馈导致来回摆动。
        _dragOffset = e.GetPosition(this);
        _dragStart = _dragOffset;
        TitleBar.CaptureMouse();
        e.Handled = true;
    }

    private void TitleBar_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragOffset is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var x = Left + (pos.X - _dragOffset.Value.X);
        var y = Top + (pos.Y - _dragOffset.Value.Y);
        Left = x;
        Top = y;
        e.Handled = true;
    }

    private void TitleBar_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragOffset is null)
        {
            return;
        }

        _dragOffset = null;
        TitleBar.ReleaseMouseCapture();
        SyncToModel();
        _manager.Save();
        e.Handled = true;
    }

    private void SyncToModel()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top)) return;
        _model.X = Left;
        _model.Y = Top;
        _model.Width = Width;
        if (!_model.Collapsed)
        {
            _model.Height = Height;
        }
    }

    // ---------- 任务增删改 ----------

    private void AddButton_OnClick(object sender, RoutedEventArgs e) => AddTask();

    private void AddTask()
    {
        var editor = new TodoTaskEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is TodoItemModel item)
        {
            item.Normalize();
            _model.Items.Add(item);
            Reload();
            _manager.Save();
        }
    }

    private void EditTask(TodoItemModel item)
    {
        var editor = new TodoTaskEditorWindow(item) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is TodoItemModel edited)
        {
            var idx = _model.Items.IndexOf(item);
            if (idx >= 0)
            {
                _model.Items[idx] = edited;
            }

            Reload();
            _manager.Save();
        }
    }

    /// <summary>在指定任务下新增子任务（编辑弹窗保存后自动挂到父任务）。</summary>
    private void AddSubTask(TodoItemModel parent)
    {
        var editor = new TodoTaskEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is TodoItemModel item)
        {
            item.ParentId = parent.Id;
            item.Normalize();
            _model.Items.Add(item);
            Reload();
            _manager.Save();
        }
    }

    private void DeleteTask(TodoItemModel item)
    {
        // 删除父任务时子任务保留，并逐级提升一级（顶层 → 保持原祖级）
        PromoteChildren(item);
        _model.Items.RemoveAll(t => t.Id == item.Id);
        Reload();
        _manager.Save();
    }

    /// <summary>把任务的直接子任务提升到与任务同级，递归处理更深层。</summary>
    private void PromoteChildren(TodoItemModel item)
    {
        foreach (var child in _model.Items.Where(t => t.ParentId == item.Id).ToList())
        {
            child.ParentId = item.ParentId;
            PromoteChildren(child);
        }
    }

    private void ToggleTask(TodoItemModel item)
    {
        if (item.Kind == TodoKind.Normal)
        {
            item.Completed = !item.Completed;
            item.CompletedAt = item.Completed ? DateTime.Now : null;
        }
        else
        {
            // 循环任务：在当前周期内的任何一天都可打卡该周期（key = 周期起始应做日）
            var cycle = TodoRecurrence.CurrentCycle(item.StartDate, item.Period, DateTime.Today);
            var key = TodoRecurrence.OccurrenceKey(cycle);
            item.CheckIns.TryGetValue(key, out var wasDone);
            item.CheckIns[key] = !wasDone;
        }

        Reload();
        _manager.Save();
    }

    /// <summary>打开循环任务的「当月日历打卡」窗口。</summary>
    private void OpenCalendar(TodoItemModel item)
    {
        var calendar = new TodoCalendarWindow(item, _manager) { Owner = this };
        calendar.ShowDialog();
        Reload();
    }

    // ---------- 行事件 ----------

    /// <summary>行内单击完成圈的打卡 / 取消打卡。</summary>
    private void Circle_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoRowViewModel vm })
        {
            return;
        }

        ToggleTask(vm.Model);
        e.Handled = true;
    }

    /// <summary>双击行：循环任务打开本月打卡日历，普通任务打开编辑。</summary>
    private void Todo_OnPreviewLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TodoRowViewModel vm })
        {
            return;
        }

        // 折叠按钮区域不触发"双击编辑/打开打卡"
        if (e.OriginalSource is FrameworkElement marker && Equals(marker.Tag, "ExpanderMarker"))
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            if (vm.Model.Kind == TodoKind.Recurring)
            {
                OpenCalendar(vm.Model);
            }
            else
            {
                EditTask(vm.Model);
            }

            e.Handled = true;
        }
    }

    private void Todo_OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TodoRowViewModel vm })
        {
            return;
        }

        var menu = new ContextMenu();
        AddMenu(menu, vm.Model.Kind == TodoKind.Recurring ? "本月打卡…" : "编辑…", () =>
        {
            if (vm.Model.Kind == TodoKind.Recurring)
            {
                OpenCalendar(vm.Model);
            }
            else
            {
                EditTask(vm.Model);
            }
        });
        if (vm.Model.Kind == TodoKind.Recurring)
        {
            AddMenu(menu, "编辑…", () => EditTask(vm.Model));
        }

        AddMenu(menu, "添加子任务…", () => AddSubTask(vm.Model));
        AddMenu(menu, IsDoneVm(vm) ? "标记为未完成" : "标记为已完成", () => ToggleTask(vm.Model));

        menu.Items.Add(new Separator());
        AddMenu(menu, "删除", () =>
        {
            var hasChild = _model.Items.Any(t => t.ParentId == vm.Model.Id);
            var tip = hasChild ? "（子任务会保留并升级为上级任务）" : string.Empty;
            if (MessageBox.Show($"删除「{vm.Model.Title}」？{tip}", "DeskFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                DeleteTask(vm.Model);
            }
        });

        menu.PlacementTarget = (FrameworkElement)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static bool IsDoneVm(TodoRowViewModel vm) => vm.IsDone;

    private void TitleBar_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TitleBar };
        AddMenu(menu, "新建任务…", AddTask);
        AddMenu(menu, "外观…", () => OpenAppearance());
        AddMenu(menu, "重命名清单…", () =>
        {
            var dialog = new InputDialog("重命名清单", "清单名称", _model.Name) { Owner = this };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                _model.Name = dialog.InputText.Trim();
                TitleText.Text = _model.Name;
                _manager.Save();
            }
        });
        AddMenu(menu, "新建待办组件…", () => _manager.AddBoard());
        AddMenu(menu, "查看历史任务…", () => _manager.RequestHistory());
        AddMenu(menu, _model.Collapsed ? "展开" : "折叠", () => CollapseButton_OnClick(sender, e));
        AddCheckedMenu(menu, "高度自适应（按任务数）", _model.AutoHeight, () =>
        {
            SetAutoHeight(!_model.AutoHeight);
            _manager.Save();
        });
        menu.Items.Add(new Separator());
        AddMenu(menu, "删除此清单", () => RemoveBoard());
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void MoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton };
        AddMenu(menu, "新建任务…", AddTask);
        AddMenu(menu, "外观…", () => OpenAppearance());
        AddCheckedMenu(menu, "高度自适应（按任务数）", _model.AutoHeight, () =>
        {
            SetAutoHeight(!_model.AutoHeight);
            _manager.Save();
        });
        AddMenu(menu, "新建待办组件…", () => _manager.AddBoard());
        AddMenu(menu, "查看历史任务…", () => _manager.RequestHistory());
        menu.Items.Add(new Separator());
        AddMenu(menu, "重命名清单…", () =>
        {
            var dialog = new InputDialog("重命名清单", "清单名称", _model.Name) { Owner = this };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                _model.Name = dialog.InputText.Trim();
                TitleText.Text = _model.Name;
                _manager.Save();
            }
        });
        AddMenu(menu, "清空已完成", () =>
        {
            _model.Items.RemoveAll(t => t.Kind == TodoKind.Normal && t.Completed);
            Reload();
            _manager.Save();
        });
        menu.Items.Add(new Separator());
        AddMenu(menu, "删除此清单", () => RemoveBoard());
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>打开待办面板的外观编辑（背景色 / 透明度 / 圆角 / 文字色）。</summary>
    private void OpenAppearance()
    {
        var dialog = new TodoAppearanceWindow(_model) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ApplyStyle();
            _manager.Save();
        }
    }

    private void RemoveBoard()
    {
        if (MessageBox.Show($"删除待办清单「{_model.Name}」？", "DeskFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _manager.RemoveBoard(_model);
    }

    private static void AddMenu(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static void AddCheckedMenu(ContextMenu menu, string header, bool isChecked, Action action)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}

/// <summary>待办行视图模型：从模型派生展示文本 / 颜色 / 状态。</summary>
internal sealed class TodoRowViewModel
{
    public TodoRowViewModel(TodoItemModel model)
    {
        Model = model;
        Title = model.Title;

        if (model.Kind == TodoKind.Normal)
        {
            IsDone = model.Completed;
        }
        else
        {
            IsDone = TodoRecurrence.IsCompletedForCycle(model, DateTime.Today);
        }

        CircleBrush = IsDone ? TodoVisual.DoneGreen() : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        CircleBorder = IsDone ? TodoVisual.DoneGreen() : new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        CircleCheck = IsDone ? "✓" : string.Empty;
        TitleBrush = IsDone ? TodoVisual.TextDim() : TodoVisual.TextWhite();
        TitleDecoration = IsDone ? TextDecorations.Strikethrough : null;

        PriorityBrush = TodoVisual.PriorityColor(model.Priority);
        PriorityTooltip = TodoVisual.PriorityText(model.Priority);

        ComputeMeta(model);
    }

    public TodoItemModel Model { get; }

    public string Title { get; }

    public bool IsDone { get; }

    public Brush CircleBrush { get; }

    public Brush CircleBorder { get; }

    public string CircleCheck { get; }

    public Brush TitleBrush { get; }

    public TextDecorationCollection? TitleDecoration { get; }

    public Brush PriorityBrush { get; }

    public string PriorityTooltip { get; }

    /// <summary>层级深度：0 = 顶层，1 = 子任务，2 = 孙任务…</summary>
    public int Level { get; set; }

    public Thickness IndentMargin => new(Level * 20, 0, 0, 0);

    /// <summary>是否含有子任务（决定折叠按钮是否显示）。</summary>
    public bool HasChildren { get; set; }

    public Visibility ExpanderVisibility => HasChildren ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>折叠按钮字形：展开 ▾ / 折叠 ▸。</summary>
    public string ExpanderGlyph => !HasChildren ? string.Empty : (Model.ChildrenCollapsed ? "▸" : "▾");

    public string MetaText { get; private set; } = string.Empty;

    public Brush MetaBrush { get; private set; } = TodoVisual.TextFaint();

    private void ComputeMeta(TodoItemModel m)
    {
        if (m.Kind == TodoKind.Normal)
        {
            if (m.Completed)
            {
                MetaText = m.CompletedAt is { } at ? $"已完成 {at:MM-dd HH:mm}" : "已完成";
                MetaBrush = TodoVisual.TextFaint();
                return;
            }

            if (m.DueDate is { } due)
            {
                if (due < DateTime.Now)
                {
                    var days = (int)Math.Floor((DateTime.Now - due).TotalDays);
                    MetaText = days <= 0 ? "已超期" : $"已超期 {days} 天";
                    MetaBrush = TodoVisual.OverdueRed();
                }
                else
                {
                    MetaText = $"截止 {due:MM-dd HH:mm}";
                    MetaBrush = TodoVisual.TextDim();
                }
            }

            return;
        }

        // 循环待办：显示周期 + 当前周期打卡状态（当前周期内的每天都显示该周期任务）
        var period = TodoVisual.PeriodText(m.Period);
        var cycle = TodoRecurrence.CurrentCycle(m.StartDate, m.Period, DateTime.Today);
        var label = m.Period switch
        {
            RecurrencePeriod.Daily => "今日",
            RecurrencePeriod.Weekly => "本周",
            RecurrencePeriod.Biweekly => "本周期",
            RecurrencePeriod.Monthly => "本月",
            _ => "本周期"
        };
        var doneAtCycle = m.CheckIns.TryGetValue(TodoRecurrence.OccurrenceKey(cycle), out var ok) && ok;
        MetaText = doneAtCycle ? $"{period} · {label}已打卡" : $"{period} · {label}待打卡";
        MetaBrush = doneAtCycle ? TodoVisual.TextDim() : TodoVisual.OverdueRed();
    }
}
