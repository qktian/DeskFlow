using System.IO;
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
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace DeskFlow.App;

public partial class BoxWindow : Window
{
    private const double TitleBarHeight = 30d;
    private const double EdgeRollThreshold = 8d;
    private const string ItemDataFormat = "DeskFlow.ItemPath";
    private const double DragThreshold = 5d;

    private readonly BoxModel _model;
    private readonly BoxManager _manager;

    private string? _selectedPath;
    private string? _selectionAnchor;
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private Point? _dragStartPoint;
    private readonly PanelInteraction _interaction;
    private bool _suppressModelUpdate;

    private System.Windows.Threading.DispatcherTimer? _dpiDebounce;
    private double _appliedDpiScale = -1;
    private int _itemsGeneration;
    private List<FileItemViewModel> _viewItems = new();
    private double _itemsViewportWidth;

    public BoxWindow(BoxModel model, BoxManager manager)
    {
        InitializeComponent();

        _model = model;
        _manager = manager;
        _interaction = new PanelInteraction(this, () => _manager.Config.General);
        TitleBar.LostMouseCapture += (_, _) => FinishMove();

        SourceInitialized += OnSourceInitialized;
        Deactivated += OnDeactivated;
        LocationChanged += OnLocationOrSizeChanged;
        SizeChanged += OnLocationOrSizeChanged;
        DpiChanged += OnWindowDpiChanged;

        ApplyModelToWindow();
        LoadItems();
    }

    public BoxModel Model => _model;

    /// <summary>
    /// 条目图标边长（DIP），来自全局设置，所有盒子统一。
    /// 会对齐到 Windows 标准图标尺寸，让提取时能命中 .ico 原生图层。
    /// </summary>
    public int IconSize => IconCache.SnapToStandardSize(_manager.Config.General.IconSize);

    /// <summary>
    /// 窗口所在屏幕的 DPI 缩放（1.0 = 100%）。
    /// 图标必须按 边长 × 缩放 的物理像素提取，否则 WPF 会把小位图拉伸到物理像素而发虚。
    /// </summary>
    private double IconScale
    {
        get
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                if (dpi.DpiScaleX > 0)
                {
                    return dpi.DpiScaleX;
                }
            }
            catch
            {
                // 窗口尚未挂上显示设备时取不到 DPI，按 100% 处理
            }

            return 1.0;
        }
    }

    /// <summary>条目外框的基准宽度；实际宽度由整行等宽列分配。</summary>
    public double ItemWidth => IconSize + 36;

    /// <summary>条目的四周间距，同时用于列数计算，避免与 XAML 不一致。</summary>
    public Thickness ItemMargin => new(3);

    public static readonly DependencyProperty ItemColumnsProperty = DependencyProperty.Register(
        nameof(ItemColumns), typeof(int), typeof(BoxWindow), new PropertyMetadata(1));

    /// <summary>每行共享的列数，末行不足一行时也保留相同列宽。</summary>
    public int ItemColumns
    {
        get => (int)GetValue(ItemColumnsProperty);
        private set => SetValue(ItemColumnsProperty, value);
    }

    /// <summary>条目外框高 = 图标 + 名称预留。</summary>
    public double ItemHeight => IconSize + 44;

    // ---------- 模型与窗口同步 ----------

    public void ApplyModelToWindow()
    {
        _suppressModelUpdate = true;
        try
        {
            var monitor = ScreenHelper.ForId(_model.ScreenId);
            var desktop = monitor.ToDesktop(new Point(_model.X, _model.Y));
            Left = desktop.X;
            Top = desktop.Y;
            Width = Math.Max(160, _model.Width);
            Height = _model.Collapsed ? TitleBarHeight : Math.Max(120, _model.Height);
            ApplyStyle();
            ApplyCollapsedState();
        }
        finally
        {
            _suppressModelUpdate = false;
        }

        SyncToModel();
    }

    public void SyncToModel()
    {
        if (_suppressModelUpdate || !IsLoaded)
        {
            return;
        }

        if (!double.IsFinite(Left) || !double.IsFinite(Top))
        {
            return;
        }

        var monitor = ScreenHelper.FromWindow(this);
        _model.ScreenId = monitor.Id;
        var local = monitor.ToLocal(new Point(Left, Top));
        _model.X = local.X;
        _model.Y = local.Y;
        _model.Width = Width;
        if (!_model.Collapsed)
        {
            _model.Height = Height;
        }
    }

    private void ApplyStyle()
    {
        var radius = Math.Clamp(_model.Style.CornerRadius, 0, 24);
        Frame.CornerRadius = new CornerRadius(radius);
        Frame.Background = new SolidColorBrush(ParseColor(_model.Style.BackgroundColor, Color.FromArgb(0x33, 0, 0, 0)));
        Opacity = Math.Clamp(_model.Style.Opacity, 0.2, 1.0);
        TitleText.Foreground = new SolidColorBrush(ParseColor(_model.Style.ForegroundColor, Colors.White));
        TitleText.Visibility = _model.Style.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
        // 盒子窗口永远不置顶（区别于 Pinned 状态 —— 即使 Pinned 也在底层，方便其他窗口随时覆盖）
        Topmost = false;

        TitleBar.CornerRadius = _model.Collapsed
            ? new CornerRadius(radius)
            : new CornerRadius(radius, radius, 0, 0);
    }

    private void ApplyCollapsedState()
    {
        var wasSuppressed = _suppressModelUpdate;
        _suppressModelUpdate = true;
        try
        {
            ContentRow.Height = _model.Collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            ItemsScroll.Visibility = _model.Collapsed ? Visibility.Collapsed : Visibility.Visible;
            EmptyHint.Visibility = Visibility.Collapsed;
            MinHeight = _model.Collapsed ? TitleBarHeight : 120;
            Height = _model.Collapsed ? TitleBarHeight : Math.Max(120, _model.Height);
        }
        finally
        {
            _suppressModelUpdate = wasSuppressed;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.MakeToolWindow(hwnd);
        // 启动后立即把窗口放到 Z 序最底（用户点其他窗口时不会再被推到更前）
        SetBottomMost();
    }

    /// <summary>
    /// 盒子失焦时回到 Z 序最底。这样：
    /// 1. 其它窗口（资源管理器、浏览器等）始终能盖在盒子上方，盒子不会抢视觉；
    /// 2. 用户点开盒子操作时临时激活到前面、操作完自动回到底层，符合「永远在最底」的语义。
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        SetBottomMost();
    }

    /// <summary>
    /// 取消置顶 + 把窗口送到 HWND_BOTTOM。NOACTIVATE 避免抢焦点，NOSIZE/NOMOVE 保留原位置尺寸。
    /// </summary>
    private void SetBottomMost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_BOTTOM,
            0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
    }

    private void OnLocationOrSizeChanged(object? sender, EventArgs e) => SyncToModel();

    /// <summary>
    /// DPI 变化（拖动到不同缩放率的显示器、系统缩放变更）：
    /// 旧位图的物理像素已不匹配，需要清空缓存让所有盒子按新缩放重新提取。
    /// WPF 在窗口创建/移动期间可能高频触发多次 DpiChanged（实测启动时可达上千次），
    /// 若每次同步 Clear + Refresh，会与「图标回调 → 布局 → 再触发 DpiChanged」互相放大，
    /// 造成清缓存风暴与反复提取。因此这里只做防抖合并，且只在缩放值真实变化后才重建。
    /// </summary>
    private void OnWindowDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        if (_dpiDebounce is null)
        {
            _dpiDebounce = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _dpiDebounce.Tick += (_, _) =>
            {
                _dpiDebounce.Stop();

                var scale = IconScale;
                if (Math.Abs(scale - _appliedDpiScale) < 0.01)
                {
                    return;
                }

                _appliedDpiScale = scale;
                _manager.Icons.Clear();
                _manager.Refresh();
            };
        }

        _dpiDebounce.Stop();
        _dpiDebounce.Start();
    }

    // ---------- 条目渲染 ----------

    public void LoadItems()
    {
        _itemsGeneration++;
        var items = new List<FileItemViewModel>();
        foreach (var path in _model.Items)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            _manager.Icons.TryGet(path, IconSize, out var icon, IconScale);
            var vm = new FileItemViewModel(path, FileItem.GetDisplayName(path), icon)
            { IsSelected = _selectedPaths.Contains(path) };
            items.Add(vm);

        }

        IEnumerable<FileItemViewModel> ordered = _model.View.SortBy switch
        {
            SortMode.Type => items
                .OrderBy(v => Path.GetExtension(v.Path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
            SortMode.ModifiedAt => items
                .OrderByDescending(v => FileMetadataCache.Get(v.Path).ModifiedAt)
                .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
            SortMode.Size => items
                .OrderByDescending(v => FileMetadataCache.Get(v.Path).Size)
                .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => items.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
        };

        var view = ordered.ToList();
        _viewItems = view;
        RebuildVisibleRows();

        TitleText.Text = _model.Name;
        EmptyHint.Visibility = view.Count == 0 && !_model.Collapsed ? Visibility.Visible : Visibility.Collapsed;

        _selectedPaths.IntersectWith(view.Select(v => v.Path));
        _selectedPath = null;
    }

    // Virtualize rows instead of allocating an icon control for every desktop item.
    // Only realized rows request shell icons; resizing changes the number of columns.
    private void RebuildVisibleRows()
    {
        // 首次布局前按明确的边框/边距估算；布局后使用 ScrollViewer 的真实视口。
        // 隐藏的滚动条不占宽度，不能再额外扣除 16 DIP。
        var width = _itemsViewportWidth > 0 ? _itemsViewportWidth : Math.Max(0,
            Width - Frame.BorderThickness.Left - Frame.BorderThickness.Right
            - ItemsScroll.Margin.Left - ItemsScroll.Margin.Right
            - ItemsScroll.Padding.Left - ItemsScroll.Padding.Right);
        ItemColumns = CalculateItemColumns(width);
        ItemsScroll.ItemsSource = _viewItems.Chunk(ItemColumns).ToList();
    }

    private int CalculateItemColumns(double viewportWidth)
    {
        var cellWidth = ItemWidth + ItemMargin.Left + ItemMargin.Right;
        return BoxItemLayout.CalculateColumns(viewportWidth, cellWidth);
    }

    private void ItemsScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer || !double.IsFinite(e.ViewportWidth) || e.ViewportWidth <= 0)
            return;

        _itemsViewportWidth = e.ViewportWidth;
        // 同一列数内仅由布局系统均分宽度，不重建条目或重新触发图标加载。
        if (CalculateItemColumns(_itemsViewportWidth) != ItemColumns) RebuildVisibleRows();
    }

    private void Item_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FileItemViewModel vm } || vm.Icon is not null) return;
        _manager.Icons.TryGet(vm.Path, IconSize, out var image, IconScale);
        if (image is not null) vm.Icon = image;
        else RequestIcon(vm.Path);
    }

    /// <summary>向后台图标线程排队提取，完成后切回 UI 线程回填。</summary>
    private void RequestIcon(string path)
    {
        var generation = _itemsGeneration;
        _manager.Icons.GetAsync(path, IconSize, source =>
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation == _itemsGeneration) ApplyIcon(path, source);
                }));
            }
            catch
            {
                // 窗口已关闭时调度器不可用，忽略
            }
        }, IconScale);
    }

    private void ApplyIcon(string path, ImageSource source)
    {
        foreach (var vm in _viewItems)
        {
            if (vm.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                vm.Icon = source;
                return;
            }
        }
    }

    // ---------- 拖动与缩放 ----------

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1 || PanelInteraction.IsTitleButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _interaction.Begin(resize: false);
        TitleBar.CaptureMouse();
        e.Handled = true;
    }

    private void TitleBar_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_interaction.Moving || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        ApplyInteractionBounds(_interaction.Move());
        e.Handled = true;
    }

    private void TitleBar_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        FinishMove();
        e.Handled = true;
    }

    private void FinishMove()
    {
        if (!_interaction.Moving) return;
        _interaction.End();
        TitleBar.ReleaseMouseCapture();
        OnDragCompleted();
    }

    private void ApplyInteractionBounds(Rect bounds)
    {
        _suppressModelUpdate = true;
        try
        {
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
        }
        finally { _suppressModelUpdate = false; }
        SyncToModel();
    }

    private void OnDragCompleted()
    {
        var center = new Point(Left + (Width / 2), Top + (Height / 2));
        var monitor = ScreenHelper.FromWindow(this);
        _model.ScreenId = monitor.Id;
        var area = monitor.WorkArea;

        if (_manager.Config.General.EdgeRoll && !_model.Collapsed)
        {
            var atLeft = Math.Abs(Left - area.Left) <= EdgeRollThreshold;
            var atRight = Math.Abs(Left + Width - area.Right) <= EdgeRollThreshold;
            if (atLeft || atRight)
            {
                _model.EdgeRolled = true;
                SetCollapsed(true);
            }
        }

        SyncToModel();
        _manager.Save();
    }

    private void Frame_OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (_model.Collapsed && _model.EdgeRolled)
        {
            _model.EdgeRolled = false;
            SetCollapsed(false);
            SyncToModel();
            _manager.Save();
        }
    }

    private void SetCollapsed(bool collapsed)
    {
        _model.Collapsed = collapsed;
        ApplyCollapsedState();
        LoadItems();
    }

    private void ThumbTop_OnDragDelta(object sender, DragDeltaEventArgs e) => ResizePanel(ResizeEdges.Top);
    private void ThumbBottom_OnDragDelta(object sender, DragDeltaEventArgs e) => ResizePanel(ResizeEdges.Bottom);
    private void ThumbLeft_OnDragDelta(object sender, DragDeltaEventArgs e) => ResizePanel(ResizeEdges.Left);
    private void ThumbRight_OnDragDelta(object sender, DragDeltaEventArgs e) => ResizePanel(ResizeEdges.Right);
    private void ThumbBottomRight_OnDragDelta(object sender, DragDeltaEventArgs e) => ResizePanel(ResizeEdges.Bottom | ResizeEdges.Right);

    private void Thumb_OnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (!_model.Collapsed) _interaction.Begin(resize: true);
    }

    private void Thumb_OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _interaction.End();
        SyncToModel();
        _manager.Save();
    }

    private void ResizePanel(ResizeEdges edges)
    {
        if (_model.Collapsed || !_interaction.Resizing) return;
        ApplyInteractionBounds(_interaction.Resize(edges, new Size(160, 120), new Size(1200, 1000)));
    }

    // ---------- 条目交互 ----------

    private void Item_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border element || element.Tag is not FileItemViewModel vm)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Shift) && _selectionAnchor is not null)
        {
            var from = _viewItems.FindIndex(v => v.Path.Equals(_selectionAnchor, StringComparison.OrdinalIgnoreCase));
            var to = _viewItems.FindIndex(v => v.Path.Equals(vm.Path, StringComparison.OrdinalIgnoreCase));
            if (from >= 0 && to >= 0)
            {
                if (!modifiers.HasFlag(ModifierKeys.Control)) _selectedPaths.Clear();
                foreach (var row in _viewItems.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1)) _selectedPaths.Add(row.Path);
            }
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!_selectedPaths.Add(vm.Path)) _selectedPaths.Remove(vm.Path);
            _selectionAnchor = vm.Path;
        }
        else { _selectedPaths.Clear(); _selectedPaths.Add(vm.Path); _selectionAnchor = vm.Path; }
        foreach (var row in _viewItems) { row.IsSelected = _selectedPaths.Contains(row.Path); row.IsNameVisible = row.IsSelected; }

        _selectedPath = _selectedPaths.Contains(vm.Path) ? vm.Path : null;
        _dragStartPoint = e.GetPosition(element);

        if (e.ClickCount == 2)
        {
            _dragStartPoint = null;
            FileOperationService.Open(vm.Path);
        }
    }

    private void Item_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _selectedPath is null || sender is not FrameworkElement element)
        {
            return;
        }

        // 双击时手指/鼠标的微小位移不应被识别为拖拽，否则第二击会被拖放吞掉
        if (_dragStartPoint is null)
        {
            return;
        }

        var current = e.GetPosition(element);
        if (Math.Abs(current.X - _dragStartPoint.Value.X) < DragThreshold &&
            Math.Abs(current.Y - _dragStartPoint.Value.Y) < DragThreshold)
        {
            return;
        }

        _dragStartPoint = null;

        var data = new DataObject();
        var dragged = _selectedPaths.Count > 0 ? _selectedPaths.ToArray() : new[] { _selectedPath };
        data.SetData(ItemDataFormat, dragged);
        data.SetData(DataFormats.FileDrop, dragged);

        DragDrop.DoDragDrop(element, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void Item_OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FileItemViewModel vm })
        {
            vm.IsNameVisible = true;
        }
    }

    private void Item_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FileItemViewModel vm } &&
            !vm.IsSelected)
        {
            vm.IsNameVisible = false;
        }
    }

    private void Item_OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not FileItemViewModel vm)
        {
            return;
        }

        if (!_selectedPaths.Contains(vm.Path))
        {
            _selectedPaths.Clear(); _selectedPaths.Add(vm.Path);
            foreach (var row in _viewItems) row.IsSelected = row.Path.Equals(vm.Path, StringComparison.OrdinalIgnoreCase);
        }

        var menu = new ContextMenu { PlacementTarget = element };

        AddMenuItem(menu, "为何归入此盒子…", () => MessageBox.Show(_manager.ExplainItem(_model, vm.Path), "归类依据"));
        if (_selectedPaths.Count > 1)
        {
            var paths = _selectedPaths.ToArray();
            var move = new MenuItem { Header = $"移动选中 {paths.Length} 项到" };
            foreach (var box in _manager.Config.Boxes.Where(b => b.Id != _model.Id && b.Kind != BoxKind.Portal))
            {
                var target = box;
                var item = new MenuItem { Header = target.Name };
                item.Click += (_, _) => _manager.AddItemsToBox(target.Id, paths, manual: true);
                move.Items.Add(item);
            }
            menu.Items.Add(move);
            AddMenuItem(menu, $"移出选中 {paths.Length} 项", () => _manager.RemoveItemsFromBox(_model.Id, paths));
            menu.Items.Add(new Separator());
        }

        // 回收站快捷方式：把系统桌面原有的「清空回收站」加回来
        if (FileOperationService.IsRecycleBinShortcut(vm.Path))
        {
            AddMenuItem(menu, "清空回收站…", () => RecycleBinActions.ShowEmptyRecycleBinDialog());
            menu.Items.Add(new Separator());
        }

        AddMenuItem(menu, "打开", () => FileOperationService.Open(vm.Path));
        AddMenuItem(menu, "在资源管理器中显示", () => FileOperationService.ShowInExplorer(vm.Path));
        AddMenuItem(menu, "复制路径", () => FileOperationService.CopyToClipboard(vm.Path));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "重命名…", () => RenameItem(vm.Path));
        AddMenuItem(menu, "移出盒子（不再自动归类）", () => _manager.RemoveItemFromBox(_model.Id, vm.Path));
        AddMenuItem(menu, "删除到回收站", () =>
        {
            if (MessageBox.Show($"确定把「{vm.Name}」移入回收站吗？", "DeskFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            if (!FileOperationService.MoveToRecycleBin(vm.Path)) return;
            _manager.RemoveItem(vm.Path);
            _manager.AssignNewItems();
        });

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void RenameItem(string path)
    {
        var current = Path.GetFileName(path);
        var dialog = new InputDialog("重命名", "请输入新名称", current) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var newName = dialog.InputText?.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName.Equals(current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var renamedPath = FileOperationService.Rename(path, newName);
        if (renamedPath is null)
        {
            return;
        }

        _manager.RenameItem(path, renamedPath);
        _manager.Refresh();
        _manager.Save();
    }

    // ---------- 拖放 ----------

    private void ItemsScroll_OnDragOver(object sender, DragEventArgs e)
    {
        var supported = e.Data.GetDataPresent(ItemDataFormat) || e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ItemsScroll_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(ItemDataFormat) && e.Data.GetData(ItemDataFormat) is string[] internalPaths)
        {
            _manager.AddItemsToBox(_model.Id, internalPaths, manual: true);
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _manager.AddItemsToBox(_model.Id, files, manual: true);

            e.Handled = true;
        }
    }

    // ---------- 盒子菜单 ----------

    private void TitleBar_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowBoxMenu(TitleBar);
        e.Handled = true;
    }

    private void MoreButton_OnClick(object sender, MouseButtonEventArgs e)
    {
        ShowBoxMenu(MoreButton);
        e.Handled = true;
    }

    private void CollapseButton_OnClick(object sender, MouseButtonEventArgs e)
    {
        _model.EdgeRolled = false;
        SetCollapsed(!_model.Collapsed);
        SyncToModel();
        _manager.Save();
        e.Handled = true;
    }

    private void ShowBoxMenu(FrameworkElement target)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        AddMenuItem(menu, "重命名盒子…", () =>
        {
            var dialog = new InputDialog("重命名盒子", "盒子名称", _model.Name) { Owner = this };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                _model.Name = dialog.InputText.Trim();
                ApplyStyle();
                LoadItems();
                _manager.Save();
            }
        });

        AddMenuItem(menu, _model.Collapsed ? "展开盒子" : "折叠盒子", () =>
        {
            _model.EdgeRolled = false;
            SetCollapsed(!_model.Collapsed);
            SyncToModel();
            _manager.Save();
        });

        AddMenuItem(menu, _model.Pinned ? "取消置顶" : "置顶显示", () =>
        {
            _model.Pinned = !_model.Pinned;
            ApplyStyle();
            _manager.Save();
        });

        menu.Items.Add(new Separator());

        AddMenuItem(menu, "按规则整理全部盒子", () => _manager.OrganizeAll());
        AddMenuItem(menu, "撤销上次整理", () => _manager.UndoOrganize());
        AddMenuItem(menu, "全选盒内条目", () =>
        {
            _selectedPaths.Clear();
            foreach (var vm in _viewItems) { _selectedPaths.Add(vm.Path); vm.IsSelected = true; }
        });
        AddMenuItem(menu, "清空回收站…", () => RecycleBinActions.ShowEmptyRecycleBinDialog());
        AddMenuItem(menu, "新建盒子…", () =>
        {
            var dialog = new InputDialog("新建盒子", "盒子名称", "新建盒子") { Owner = this };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                _manager.AddBox(dialog.InputText.Trim());
            }
        });
        AddMenuItem(menu, "新建待办组件…", () => _manager.RequestTodoCreate());
        AddMenuItem(menu, "保存快照…", () =>
        {
            var dialog = new InputDialog("保存快照", "快照名称", $"快照 {DateTime.Now:yyyy-MM-dd HH:mm}") { Owner = this };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                _manager.CreateSnapshot(dialog.InputText.Trim());
            }
        });

        var snapshotMenu = new MenuItem { Header = "恢复快照" };
        var snapshots = _manager.Config.Snapshots.OrderByDescending(s => s.CreatedAt).ToList();
        foreach (var snapshot in snapshots)
        {
            var snap = snapshot;
            var item = new MenuItem { Header = $"{snap.Name}（{snap.CreatedAt:MM-dd HH:mm}）" };
            item.Click += (_, _) =>
            {
                var (restored, skipped) = _manager.RestoreSnapshot(snap);
                MessageBox.Show(
                    $"已恢复「{snap.Name}」：还原 {restored} 项，忽略 {skipped} 个已不存在的条目。",
                    "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            };
            snapshotMenu.Items.Add(item);
        }

        if (snapshots.Count == 0)
        {
            snapshotMenu.Items.Add(new MenuItem { Header = "（暂无快照）", IsEnabled = false });
        }

        menu.Items.Add(snapshotMenu);

        menu.Items.Add(new Separator());

        AddMenuItem(menu, "删除盒子", () =>
        {
            if (_model.Kind == BoxKind.Unclassified)
            {
                MessageBox.Show("未分类盒子是兜底容器，不能删除。", "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show($"删除盒子「{_model.Name}」？盒内文件不会被删除。", "DeskFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            _manager.RemoveBox(_model);
        });

        menu.Items.Add(new Separator());
        AddMenuItem(menu, "设置…", () => _manager.RequestSettings());
        AddMenuItem(menu, "退出 DeskFlow", () => Application.Current.Shutdown());

        menu.IsOpen = true;
    }

    // ---------- 工具方法 ----------

    private static void AddMenuItem(ItemsControl menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return (Color)ColorConverter.ConvertFromString(value);
            }
        }
        catch
        {
            // 颜色格式非法时使用回退色
        }

        return fallback;
    }
}
