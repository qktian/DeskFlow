using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskFlow.App.Services;
using DeskFlow.Core.Models;
using DeskFlow.Shell;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;

namespace DeskFlow.App;

public partial class BoxWindow : Window
{
    private const double TitleBarHeight = 30d;
    private const double SnapThreshold = 14d;
    private const double EdgeRollThreshold = 8d;
    private const string ItemDataFormat = "DeskFlow.ItemPath";
    private const double DragThreshold = 5d;
    private const double BoxSnapThreshold = 10d;

    private readonly BoxModel _model;
    private readonly BoxManager _manager;

    private Border? _selectedElement;
    private string? _selectedPath;
    private Point? _dragStartPoint;
    private Point? _dragOffset;
    private bool _suppressModelUpdate;

    private System.Windows.Threading.DispatcherTimer? _dpiDebounce;
    private double _appliedDpiScale = -1;

    // 缩放吸附状态（粘性吸附用）：记录当前吸附到的参考边坐标，拖到一半不因小抖动脱开
    private double? _resizeSnapTop;
    private double? _resizeSnapBottom;
    private double? _resizeSnapLeft;
    private double? _resizeSnapRight;

    public BoxWindow(BoxModel model, BoxManager manager)
    {
        InitializeComponent();

        _model = model;
        _manager = manager;

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

    /// <summary>条目外框宽 = 图标 + 左右留白。</summary>
    public double ItemWidth => IconSize + 36;

    /// <summary>条目外框高 = 图标 + 名称预留。</summary>
    public double ItemHeight => IconSize + 44;

    // ---------- 模型与窗口同步 ----------

    public void ApplyModelToWindow()
    {
        _suppressModelUpdate = true;
        try
        {
            Left = _model.X;
            Top = _model.Y;
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

        _model.X = Left;
        _model.Y = Top;
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
            _suppressModelUpdate = false;
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
        var items = new List<FileItemViewModel>();
        foreach (var path in _model.Items)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            _manager.Icons.TryGet(path, IconSize, out var icon, IconScale);
            var vm = new FileItemViewModel(path, FileItem.GetDisplayName(path), icon);
            items.Add(vm);

            if (icon is null)
            {
                RequestIcon(path);
            }
        }

        IEnumerable<FileItemViewModel> ordered = _model.View.SortBy switch
        {
            SortMode.Type => items
                .OrderBy(v => Path.GetExtension(v.Path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
            SortMode.ModifiedAt => items
                .OrderByDescending(v => SafeTime(v.Path))
                .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
            SortMode.Size => items
                .OrderByDescending(v => SafeSize(v.Path))
                .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => items.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
        };

        var view = ordered.ToList();
        ItemsHost.ItemsSource = null;
        ItemsHost.ItemsSource = view;

        TitleText.Text = _model.Name;
        EmptyHint.Visibility = view.Count == 0 && !_model.Collapsed ? Visibility.Visible : Visibility.Collapsed;

        _selectedElement = null;
        _selectedPath = null;
    }

    /// <summary>向后台图标线程排队提取，完成后切回 UI 线程回填。</summary>
    private void RequestIcon(string path)
    {
        _manager.Icons.GetAsync(path, IconSize, source =>
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() => ApplyIcon(path, source)));
            }
            catch
            {
                // 窗口已关闭时调度器不可用，忽略
            }
        }, IconScale);
    }

    private void ApplyIcon(string path, ImageSource source)
    {
        if (ItemsHost.ItemsSource is not IEnumerable<FileItemViewModel> view)
        {
            return;
        }

        foreach (var vm in view)
        {
            if (vm.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                vm.Icon = source;
                return;
            }
        }
    }

    private static DateTime SafeTime(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.GetLastWriteTime(path)
                : File.GetLastWriteTime(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static long SafeSize(string path)
    {
        try
        {
            return Directory.Exists(path) ? 0L : new FileInfo(path).Length;
        }
        catch
        {
            return 0L;
        }
    }

    // ---------- 拖动与缩放 ----------

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1 || IsTitleBarButton(e))
        {
            return;
        }

        _dragOffset = e.GetPosition(this);
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
        (x, y) = ApplyBoxSnap(x, y);
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
        OnDragCompleted();
        e.Handled = true;
    }

    private static bool IsTitleBarButton(MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null)
        {
            if (current is Button)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    /// <summary>拖动时检测与相邻盒子的左/右/中、上/下/中参考线，距离在阈值内则贴齐。</summary>
    private (double x, double y) ApplyBoxSnap(double x, double y)
    {
        if (!_manager.Config.General.SnapBoxes)
        {
            return (x, y);
        }

        var width = Width;
        var height = Height;
        var bestDx = BoxSnapThreshold + 1;
        var bestDy = BoxSnapThreshold + 1;
        var dx = 0d;
        var dy = 0d;

        foreach (var other in _manager.Windows)
        {
            if (ReferenceEquals(other, this))
            {
                continue;
            }

            var ox = other.Left;
            var oy = other.Top;
            var ow = other.Width;
            var oh = other.Height;

            foreach (var cur in new[] { x, x + (width / 2), x + width })
            {
                foreach (var target in new[] { ox, ox + (ow / 2), ox + ow })
                {
                    var diff = Math.Abs(cur - target);
                    if (diff < bestDx)
                    {
                        bestDx = diff;
                        dx = target - cur;
                    }
                }
            }

            foreach (var cur in new[] { y, y + (height / 2), y + height })
            {
                foreach (var target in new[] { oy, oy + (oh / 2), oy + oh })
                {
                    var diff = Math.Abs(cur - target);
                    if (diff < bestDy)
                    {
                        bestDy = diff;
                        dy = target - cur;
                    }
                }
            }
        }

        return (bestDx <= BoxSnapThreshold ? x + dx : x,
                bestDy <= BoxSnapThreshold ? y + dy : y);
    }

    private void OnDragCompleted()
    {
        var center = new Point(Left + (Width / 2), Top + (Height / 2));
        var monitor = ScreenHelper.FromPoint(center);
        _model.ScreenId = monitor.Id;
        var area = monitor.WorkArea;

        if (_manager.Config.General.SnapToScreenEdge)
        {
            if (Math.Abs(Left - area.Left) <= SnapThreshold)
            {
                Left = area.Left;
            }

            if (Math.Abs(Top - area.Top) <= SnapThreshold)
            {
                Top = area.Top;
            }

            if (Math.Abs(Left + Width - area.Right) <= SnapThreshold)
            {
                Left = area.Right - Width;
            }

            if (Math.Abs(Top + Height - area.Bottom) <= SnapThreshold)
            {
                Top = area.Bottom - Height;
            }
        }

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

    private void ThumbTop_OnDragDelta(object sender, DragDeltaEventArgs e) => Resize(0, -e.VerticalChange, false, true);

    private void ThumbBottom_OnDragDelta(object sender, DragDeltaEventArgs e) => Resize(0, e.VerticalChange, false, false);

    private void ThumbLeft_OnDragDelta(object sender, DragDeltaEventArgs e) => Resize(-e.HorizontalChange, 0, true, false);

    private void ThumbRight_OnDragDelta(object sender, DragDeltaEventArgs e) => Resize(e.HorizontalChange, 0, false, false);

    private void ThumbBottomRight_OnDragDelta(object sender, DragDeltaEventArgs e) => Resize(e.HorizontalChange, e.VerticalChange, false, false);

    private void Thumb_OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _resizeSnapTop = null;
        _resizeSnapBottom = null;
        _resizeSnapLeft = null;
        _resizeSnapRight = null;
        SyncToModel();
        _manager.Save();
    }

    /// <summary>
    /// 调整盒子尺寸，吸附逻辑为「边缘对齐」：
    /// - 拖动的是上/下边 → 该边与其它所有盒子的上边缘 / 下边缘对齐；
    /// - 拖动的是左/右边 → 该边与其它所有盒子的左边缘 / 右边缘对齐；
    /// - 采用粘性吸附：一旦吸附到某条参考边，小幅继续拖动不会脱开（需偏离超过 阈值×2.5 才解除），
    ///   避免因鼠标微小抖动导致刚吸附就弹开、感觉不明显。
    /// </summary>
    private void Resize(double deltaWidth, double deltaHeight, bool moveLeft, bool moveTop)
    {
        if (_model.Collapsed)
        {
            return;
        }

        var newHeight = Math.Clamp(Height + deltaHeight, 120, 1000);
        var newWidth = Math.Clamp(Width + deltaWidth, 160, 1200);

        double top, bottom, height;
        if (moveTop)
        {
            // 下边缘固定不动，上边缘随鼠标移动并吸附到其它盒子的上/下边缘
            bottom = Top + Height;
            top = bottom - newHeight;
            top = SnapEdgeVertical(top, ref _resizeSnapTop);
            height = Math.Clamp(bottom - top, 120, 1000);
            top = bottom - height;
        }
        else
        {
            // 上边缘固定不动，下边缘随鼠标移动并吸附
            top = Top;
            bottom = top + newHeight;
            bottom = SnapEdgeVertical(bottom, ref _resizeSnapBottom);
            height = Math.Clamp(bottom - top, 120, 1000);
            bottom = top + height;
        }

        double left, right, width;
        if (moveLeft)
        {
            // 右边缘固定不动，左边缘随鼠标移动并吸附
            right = Left + Width;
            left = right - newWidth;
            left = SnapEdgeHorizontal(left, ref _resizeSnapLeft);
            width = Math.Clamp(right - left, 160, 1200);
            left = right - width;
        }
        else
        {
            // 左边缘固定不动，右边缘随鼠标移动并吸附
            left = Left;
            right = left + newWidth;
            right = SnapEdgeHorizontal(right, ref _resizeSnapRight);
            width = Math.Clamp(right - left, 160, 1200);
            right = left + width;
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    /// <summary>开始拖缩放手柄：重置吸附状态，避免上一次会话的吸附目标残留。</summary>
    private void Thumb_OnDragStarted(object sender, DragStartedEventArgs e)
    {
        _resizeSnapTop = null;
        _resizeSnapBottom = null;
        _resizeSnapLeft = null;
        _resizeSnapRight = null;
    }

    /// <summary>把竖直方向的边吸附到其它盒子的上/下边缘（粘性）。</summary>
    private double SnapEdgeVertical(double y, ref double? snapTarget)
    {
        if (snapTarget is double held && Math.Abs(y - held) < BoxSnapThreshold * 2.5)
        {
            return held;
        }

        snapTarget = null;
        double? best = null;
        var bestDiff = BoxSnapThreshold;
        foreach (var other in _manager.Windows)
        {
            if (ReferenceEquals(other, this))
            {
                continue;
            }

            Consider(other.Top);              // 其它盒子的上边缘
            Consider(other.Top + other.Height); // 其它盒子的下边缘
        }

        if (best is double b)
        {
            snapTarget = b;
            return b;
        }

        return y;

        void Consider(double candidate)
        {
            var d = Math.Abs(y - candidate);
            if (d < bestDiff)
            {
                bestDiff = d;
                best = candidate;
            }
        }
    }

    /// <summary>把水平方向的边吸附到其它盒子的左/右边缘（粘性）。</summary>
    private double SnapEdgeHorizontal(double x, ref double? snapTarget)
    {
        if (snapTarget is double held && Math.Abs(x - held) < BoxSnapThreshold * 2.5)
        {
            return held;
        }

        snapTarget = null;
        double? best = null;
        var bestDiff = BoxSnapThreshold;
        foreach (var other in _manager.Windows)
        {
            if (ReferenceEquals(other, this))
            {
                continue;
            }

            Consider(other.Left);               // 其它盒子的左边缘
            Consider(other.Left + other.Width); // 其它盒子的右边缘
        }

        if (best is double b)
        {
            snapTarget = b;
            return b;
        }

        return x;

        void Consider(double candidate)
        {
            var d = Math.Abs(x - candidate);
            if (d < bestDiff)
            {
                bestDiff = d;
                best = candidate;
            }
        }
    }

    // ---------- 条目交互 ----------

    private void Item_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border element || element.Tag is not FileItemViewModel vm)
        {
            return;
        }

        if (_selectedElement is not null)
        {
            _selectedElement.Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
            if (_selectedElement.Tag is FileItemViewModel oldVm)
            {
                oldVm.IsNameVisible = false;
            }
        }

        _selectedElement = element;
        _selectedPath = vm.Path;
        _dragStartPoint = e.GetPosition(element);
        element.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        vm.IsNameVisible = true;

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
        data.SetData(ItemDataFormat, _selectedPath);
        data.SetData(DataFormats.FileDrop, new[] { _selectedPath });

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
            !string.Equals(vm.Path, _selectedPath, StringComparison.OrdinalIgnoreCase))
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

        var menu = new ContextMenu { PlacementTarget = element };

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

            FileOperationService.MoveToRecycleBin(vm.Path);
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

        if (!FileOperationService.Rename(path, newName))
        {
            return;
        }

        _manager.RemoveItem(path);
        _manager.AssignNewItems();
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
        if (e.Data.GetDataPresent(ItemDataFormat) && e.Data.GetData(ItemDataFormat) is string internalPath)
        {
            _manager.AddItemToBox(_model.Id, internalPath, manual: true);
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files)
            {
                _manager.AddItemToBox(_model.Id, file, manual: true);
            }

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
