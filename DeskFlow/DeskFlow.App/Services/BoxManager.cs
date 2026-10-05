using System.IO;
using System.Windows;
using DeskFlow.Core.Models;
using DeskFlow.Core.Services;
using DeskFlow.Shell;

namespace DeskFlow.App.Services;

/// <summary>
/// 盒子生命周期与归类调度的中枢：持有配置、管理全部盒子窗口、执行一键整理与实时归类。
/// </summary>
public sealed class BoxManager
{
    public const int SnapshotLimit = 100;
    public const long SnapshotSizeLimitBytes = 32L * 1024 * 1024;
    public int LastSnapshotPrunedCount { get; private set; }
    private readonly AppConfig _config;
    private readonly Func<AppConfig, bool> _saveConfig;
    private readonly IconCache _icons = new();
    private readonly List<BoxWindow> _windows = new();
    private bool _visible = true;
    private int _scanVersion;
    private bool _organizing;
    private bool _rescanAfterOrganize;
    private BoxSnapshot? _undoOrganize;
    public bool CanUndoOrganize => !_organizing && _undoOrganize is not null;

    public BoxManager(AppConfig config, Func<AppConfig, bool>? saveConfig = null)
    {
        _config = config;
        _saveConfig = saveConfig ?? (value => ConfigService.Save(value));
    }

    public AppConfig Config => _config;

    public IconCache Icons => _icons;

    public bool IsVisible => _visible;

    public IReadOnlyList<BoxWindow> Windows => _windows;

    /// <summary>盒子菜单请求打开设置中心。</summary>
    public event EventHandler? SettingsRequested;
    public event EventHandler? LayoutRestored;

    public void RequestSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>盒子菜单请求新建一个待办组件。</summary>
    public event EventHandler? TodoCreateRequested;

    public void RequestTodoCreate() => TodoCreateRequested?.Invoke(this, EventArgs.Empty);

    public void Initialize()
    {
        if (!_config.General.LayoutInitialized)
        {
            AutoLayout();
            _config.General.LayoutInitialized = true;
        }

        Rebuild();
        QueueAssignNewItems();
        Save();
    }

    public void Rebuild()
    {
        foreach (var window in _windows.ToList())
        {
            window.Close();
        }

        _windows.Clear();

        foreach (var box in _config.Boxes.OrderBy(b => b.Order).ThenBy(b => b.Name))
        {
            var window = new BoxWindow(box, this);
            window.Show();
            _windows.Add(window);
        }

        ApplyVisibility();
    }

    /// <summary>首次启动时把默认盒子排布到主屏右侧。</summary>
    public void AutoLayout()
    {
        var primary = ScreenHelper.ForId(_config.General.PreferredMonitorId);
        var area = primary.WorkArea;

        const double boxWidth = 240;
        const double boxHeight = 300;
        const double gap = 12;

        var rowsPerColumn = Math.Max(1, (int)((area.Height - 24) / (boxHeight + gap)));
        var columns = (int)Math.Ceiling(_config.Boxes.Count / (double)rowsPerColumn);
        var startX = Math.Max(area.Left + 12, area.Right - 12 - (columns * (boxWidth + gap)));

        var index = 0;
        foreach (var box in _config.Boxes.OrderBy(b => b.Kind == BoxKind.Unclassified).ThenBy(b => b.Name))
        {
            var column = index / rowsPerColumn;
            var row = index % rowsPerColumn;

            box.Width = boxWidth;
            box.Height = box.Kind == BoxKind.Unclassified ? Math.Min(boxHeight, 220) : boxHeight;
            box.X = startX - area.Left + (column * (boxWidth + gap));
            box.Y = 12 + (row * (box.Height + gap));
            box.ScreenId = primary.Id;
            index++;
        }
    }

    public void ToggleVisible() => SetVisible(!_visible);

    public void SetVisible(bool visible)
    {
        _visible = visible;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        foreach (var window in _windows)
        {
            window.Visibility = _visible ? Visibility.Visible : Visibility.Hidden;
        }
    }

    /// <summary>一键整理：按规则全量重排（保留手动归类，除非开启强制归类）。</summary>
    public async void OrganizeAll()
    {
        if (_organizing) return;
        _organizing = true;
        var version = ++_scanVersion;
        List<FileItem> items;
        try
        {
            items = await Task.Run(DesktopScanner.Scan);
            if (version != _scanVersion) return;
            _undoOrganize = CaptureAssignmentState();
            var manual = BuildManualAssignments();
            var excluded = BuildExcludedSet();

            foreach (var box in _config.Boxes) box.Items.Clear();

            var pending = items.Where(i => !manual.ContainsKey(i.Path) && !excluded.Contains(i.Path)).ToList();
            ApplyMapping(RuleEngine.Classify(pending, _config.Rules, _config.Boxes));

            foreach (var (path, boxId) in manual)
            {
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                _config.Boxes.FirstOrDefault(b => b.Id == boxId)?.Items.Add(path);
            }

            Prune(); Refresh(); Save();
        }
        catch (Exception error) { DiagnosticLog.Error("Scan", "Organize scan failed", error); }
        finally
        {
            _organizing = false;
            if (_rescanAfterOrganize) { _rescanAfterOrganize = false; QueueAssignNewItems(); }
        }
    }

    public void UndoOrganize()
    {
        if (_organizing || _undoOrganize is not { } state) return;
        _undoOrganize = null;
        foreach (var box in _config.Boxes)
        {
            var previous = state.Boxes.FirstOrDefault(b => b.BoxId == box.Id);
            if (previous is null) continue;
            box.Items = new List<string>(previous.Items);
            box.ManualItems = new List<string>(previous.ManualItems);
        }
        _config.ExcludedPaths = new List<string>(state.ExcludedPaths);
        Prune(); Refresh(); Save(); QueueAssignNewItems();
    }

    private BoxSnapshot CaptureAssignmentState() => new()
    {
        Boxes = _config.Boxes.Select(b => new BoxItemsSnapshot
        {
            BoxId = b.Id, BoxName = b.Name,
            Items = new List<string>(b.Items), ManualItems = new List<string>(b.ManualItems)
        }).ToList(),
        ExcludedPaths = new List<string>(_config.ExcludedPaths)
    };

    public async void QueueAssignNewItems()
    {
        if (_organizing) { _rescanAfterOrganize = true; return; }
        var version = ++_scanVersion;
        try
        {
            var items = await Task.Run(DesktopScanner.Scan);
            if (version != _scanVersion) return;
            AssignScannedItems(items);
        }
        catch (Exception error) { DiagnosticLog.Error("Scan", "Background scan failed", error); }
    }

    /// <summary>实时归类：只处理尚未归入任何盒子的新条目。</summary>
    public void AssignNewItems(bool save = true)
    {
        AssignScannedItems(DesktopScanner.Scan(), save);
    }

    private void AssignScannedItems(List<FileItem> items, bool save = true)
    {
        var assigned = new HashSet<string>(
            _config.Boxes.SelectMany(b => b.Items),
            StringComparer.OrdinalIgnoreCase);
        var manual = BuildManualAssignments();
        var excluded = BuildExcludedSet();

        var pending = items
            .Where(i => !assigned.Contains(i.Path) && !manual.ContainsKey(i.Path) && !excluded.Contains(i.Path))
            .ToList();

        if (pending.Count == 0)
        {
            Prune();
            Refresh();
            if (save)
            {
                Save();
            }

            return;
        }

        ApplyMapping(RuleEngine.Classify(pending, _config.Rules, _config.Boxes));
        Prune();
        Refresh();

        if (save)
        {
            Save();
        }
    }

    public void Refresh()
    {
        foreach (var window in _windows)
        {
            window.LoadItems();
        }
    }

    /// <summary>Process only changed paths; unchanged boxes retain their selection and visuals.</summary>
    public void UpdateChangedItems(IReadOnlyList<string> paths)
    {
        var affected = new HashSet<string>();
        var assigned = new HashSet<string>(_config.Boxes.SelectMany(b => b.Items), StringComparer.OrdinalIgnoreCase);
        var excluded = BuildExcludedSet();
        var pending = new List<FileItem>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var box in _config.Boxes.Where(b => b.Items.Contains(path, StringComparer.OrdinalIgnoreCase)))
                affected.Add(box.Id);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                RemoveItem(path);
                _config.ExcludedPaths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
                continue;
            }
            if (assigned.Contains(path) || excluded.Contains(path)) continue;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.Hidden) != 0 ||
                    Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Add(FileItem.FromPath(path));
            }
            catch (IOException) { /* A subsequent watcher event will retry a changing file. */ }
            catch (UnauthorizedAccessException) { }
        }
        var mapping = RuleEngine.Classify(pending, _config.Rules, _config.Boxes);
        ApplyMapping(mapping);
        affected.UnionWith(mapping.Keys);
        foreach (var window in _windows.Where(w => affected.Contains(w.Model.Id))) window.LoadItems();
        Save();
    }

    /// <summary>设置变更后重新应用盒子样式并刷新条目。</summary>
    public void ApplyStyles()
    {
        foreach (var window in _windows)
        {
            window.ApplyModelToWindow();
            window.LoadItems();
        }
    }

    public bool Save()
    {
        foreach (var window in _windows)
        {
            window.SyncToModel();
        }

        return _saveConfig(_config);
    }

    public void AddItemToBox(string boxId, string path, bool manual)
        => AddItemsToBox(boxId, new[] { path }, manual);

    public void AddItemsToBox(string boxId, IEnumerable<string> paths, bool manual)
    {
        var target = _config.Boxes.FirstOrDefault(b => b.Id == boxId);
        if (target is null)
        {
            return;
        }

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            RemoveItem(path);

            // 手动拖回盒子即视为撤销「移出」，否则下次整理仍会被排除
            _config.ExcludedPaths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));

            target.Items.Add(path);
            if (manual)
            {
                target.ManualItems.Add(path);
            }
        }

        Refresh();
        Save();
    }

    public void RemoveItem(string path)
    {
        foreach (var box in _config.Boxes)
        {
            box.Items.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            box.ManualItems.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void RenameItem(string oldPath, string newPath)
    {
        PathAssignments.Rename(_config, oldPath, newPath);
        _icons.Remove(oldPath);
        _icons.Remove(newPath);
    }

    /// <summary>
    /// 把条目移出盒子，并记入排除列表，使其不再被自动归类重新收纳。
    /// 用户可在设置中心「已排除条目」中恢复。
    /// </summary>
    public void RemoveItemFromBox(string boxId, string path)
        => RemoveItemsFromBox(boxId, new[] { path });

    public void RemoveItemsFromBox(string boxId, IEnumerable<string> paths)
    {
        var box = _config.Boxes.FirstOrDefault(b => b.Id == boxId);
        if (box is null)
        {
            return;
        }

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            box.Items.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            box.ManualItems.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (!IsExcluded(path)) _config.ExcludedPaths.Add(path);
        }

        Refresh();
        Save();
    }

    public string ExplainItem(BoxModel box, string path)
    {
        if (box.ManualItems.Contains(path, StringComparer.OrdinalIgnoreCase))
            return $"{Path.GetFileName(path)}\n当前位于「{box.Name}」：由手动拖放或移动指定。";
        if (IsExcluded(path)) return $"{Path.GetFileName(path)}\n此条目已排除自动归类。";
        var rule = RuleEngine.Match(FileMetadataCache.Get(path), _config.Rules);
        if (rule is null) return $"{Path.GetFileName(path)}\n未命中启用的规则，进入兜底盒子「{box.Name}」。";
        var target = _config.Boxes.FirstOrDefault(b => b.Id == rule.TargetBoxId)?.Name ?? "已删除盒子";
        return $"{Path.GetFileName(path)}\n命中规则「{rule.Name}」：{rule.Describe()}。\n规则目标：{target}；当前盒子：{box.Name}。";
    }

    public bool IsExcluded(string path) =>
        _config.ExcludedPaths.Contains(path, StringComparer.OrdinalIgnoreCase);

    /// <summary>恢复被排除的条目：清除排除标记并重新参与归类。</summary>
    public void RestoreItem(string path)
    {
        _config.ExcludedPaths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        AssignNewItems();
    }

    /// <summary>清空排除列表并重新归类。</summary>
    public void ClearExcluded()
    {
        _config.ExcludedPaths.Clear();
        AssignNewItems();
    }

    public void AddBox(string name)
    {
        var primary = ScreenHelper.Primary;
        var box = new BoxModel
        {
            Name = name,
            Order = _config.Boxes.Count,
            Width = 240,
            Height = 300,
            X = Math.Max(12, 40 + (_config.Boxes.Count % 4) * 252),
            Y = 40,
            ScreenId = primary.Id
        };

        AddBox(box);
    }

    /// <summary>把已有模型加入配置并立即建窗（供设置回滚等场景复用）。</summary>
    public void AddBox(BoxModel box)
    {
        _config.Boxes.Add(box);
        Save();

        var window = new BoxWindow(box, this);
        window.Show();
        window.Visibility = _visible ? Visibility.Visible : Visibility.Hidden;
        _windows.Add(window);
        window.LoadItems();
    }

    public void RemoveBox(BoxModel box, bool save = true)
    {
        var window = _windows.FirstOrDefault(w => ReferenceEquals(w.Model, box));
        if (window is not null)
        {
            _windows.Remove(window);
            window.Close();
        }

        _config.Rules.RemoveAll(r => r.TargetBoxId == box.Id);
        _config.Boxes.Remove(box);

        if (save)
        {
            Save();
        }
    }

    /// <summary>显示器变化时把越界的盒子拉回可视范围。</summary>
    public void EnsureOnScreen()
    {
        var monitors = ScreenHelper.GetMonitors();
        foreach (var box in _config.Boxes)
        {
            var monitor = monitors.FirstOrDefault(m => m.Id == box.ScreenId)
                ?? ScreenHelper.ForId(_config.General.PreferredMonitorId);
            var rect = new Rect(monitor.WorkArea.Left + box.X, monitor.WorkArea.Top + box.Y,
                box.Width, box.Collapsed ? 30 : box.Height);

            var clamped = ScreenHelper.ClampToMonitor(rect, monitor);
            box.X = clamped.X - monitor.WorkArea.Left;
            box.Y = clamped.Y - monitor.WorkArea.Top;
            box.Width = clamped.Width;
            if (!box.Collapsed) box.Height = clamped.Height;
            box.ScreenId = monitor.Id;
        }

        foreach (var window in _windows)
        {
            window.ApplyModelToWindow();
        }
    }

    // ---------- 手动快照 ----------

    /// <summary>把当前所有盒子的条目归属保存为一份快照。</summary>
    public BoxSnapshot CreateSnapshot(string? name = null)
    {
        foreach (var window in _windows) window.SyncToModel();
        var snapshot = new BoxSnapshot
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"快照 {DateTime.Now:yyyy-MM-dd HH:mm}" : name.Trim(),
            CreatedAt = DateTime.Now,
            Boxes = _config.Boxes.Select(b => new BoxItemsSnapshot
            {
                BoxId = b.Id,
                BoxName = b.Name,
                Items = new List<string>(b.Items),
                ManualItems = new List<string>(b.ManualItems),
                Layout = CaptureLayout(b)
            }).ToList(),
            TodoLayouts = _config.TodoBoards.Select(b => new TodoLayoutSnapshot
            {
                BoardId = b.Id, Layout = CaptureLayout(b)
            }).ToList(),
            ExcludedPaths = new List<string>(_config.ExcludedPaths)
        };

        _config.Snapshots.Add(snapshot);
        LastSnapshotPrunedCount = 0;
        while (_config.Snapshots.Count > 1 &&
               (_config.Snapshots.Count > SnapshotLimit ||
                _config.Snapshots.Sum(value => (long)System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value).Length) > SnapshotSizeLimitBytes))
        {
            var oldest = _config.Snapshots.Where(value => value.Id != snapshot.Id).MinBy(value => value.CreatedAt);
            if (oldest is null) break;
            _config.Snapshots.Remove(oldest);
            LastSnapshotPrunedCount++;
        }
        Save();
        return snapshot;
    }

    private static BoxStyle CopyStyle(BoxStyle style) => new()
    {
        BackgroundColor = style.BackgroundColor, ForegroundColor = style.ForegroundColor,
        Opacity = style.Opacity, CornerRadius = style.CornerRadius, ShowTitle = style.ShowTitle
    };

    private static PanelLayoutSnapshot CaptureLayout(BoxModel b) => new()
    {
        X = b.X, Y = b.Y, Width = b.Width, Height = b.Height, ScreenId = b.ScreenId,
        Collapsed = b.Collapsed, Pinned = b.Pinned, IconSize = b.IconSize,
        SortBy = b.View.SortBy, Style = CopyStyle(b.Style)
    };

    private static PanelLayoutSnapshot CaptureLayout(TodoBoardModel b) => new()
    {
        X = b.X, Y = b.Y, Width = b.Width, Height = b.Height, ScreenId = b.ScreenId,
        Collapsed = b.Collapsed, AutoHeight = b.AutoHeight, Style = CopyStyle(b.Style)
    };

    public void DeleteSnapshot(BoxSnapshot snapshot)
    {
        _config.Snapshots.Remove(snapshot);
        Save();
    }

    /// <summary>
    /// 按快照恢复盒子的条目归属：
    /// - 文件 / 目录已不存在的条目直接忽略；
    /// - 快照中的盒子已被删除时，其条目回到未分类盒；
    /// - 恢复完成后，桌面上新增的（快照中没有的）文件会按规则重新自动归类。
    /// </summary>
    public (int Restored, int Skipped) RestoreSnapshot(BoxSnapshot snapshot)
    {
        var unclassified = _config.UnclassifiedBox;

        // 清空现有归属，按快照重建
        foreach (var box in _config.Boxes)
        {
            box.Items.Clear();
            box.ManualItems.Clear();
        }

        int restored = 0;
        int skipped = 0;

        foreach (var snapBox in snapshot.Boxes)
        {
            var target = _config.Boxes.FirstOrDefault(b => b.Id == snapBox.BoxId) ?? unclassified;
            if (target is null)
            {
                skipped += snapBox.Items.Count + snapBox.ManualItems.Count;
                continue;
            }

            foreach (var path in snapBox.Items)
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    skipped++;
                    continue;
                }

                if (!target.Items.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    target.Items.Add(path);
                    restored++;
                }
            }

            foreach (var path in snapBox.ManualItems)
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    skipped++;
                    continue;
                }

                if (!target.ManualItems.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    target.ManualItems.Add(path);
                }
            }
            if (snapBox.Layout is { } layout && target.Id == snapBox.BoxId)
            {
                target.X = layout.X; target.Y = layout.Y;
                target.Width = layout.Width; target.Height = layout.Height;
                target.ScreenId = layout.ScreenId; target.Collapsed = layout.Collapsed;
                target.Pinned = layout.Pinned; target.IconSize = layout.IconSize;
                target.View.SortBy = layout.SortBy; target.Style = CopyStyle(layout.Style);
                target.Normalize();
            }
        }

        foreach (var boardSnapshot in snapshot.TodoLayouts ?? new())
        {
            var board = _config.TodoBoards.FirstOrDefault(b => b.Id == boardSnapshot.BoardId);
            if (board is null || boardSnapshot.Layout is null) continue;
            var layout = boardSnapshot.Layout;
            board.X = layout.X; board.Y = layout.Y;
            board.Width = layout.Width; board.Height = layout.Height;
            board.ScreenId = layout.ScreenId; board.Collapsed = layout.Collapsed;
            board.AutoHeight = layout.AutoHeight; board.Style = CopyStyle(layout.Style);
            board.Normalize();
        }

        // 排除列表恢复（过滤已不存在的路径）
        _config.ExcludedPaths = snapshot.ExcludedPaths
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        EnsureOnScreen();
        LayoutRestored?.Invoke(this, EventArgs.Empty);
        Refresh();
        Save();
        QueueAssignNewItems();
        return (restored, skipped);
    }

    private HashSet<string> BuildExcludedSet() =>
        new(_config.ExcludedPaths, StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, string> BuildManualAssignments()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_config.General.ForceAutoClassify)
        {
            return map;
        }

        foreach (var box in _config.Boxes)
        {
            foreach (var path in box.ManualItems)
            {
                map[path] = box.Id;
            }
        }

        return map;
    }

    private void ApplyMapping(Dictionary<string, List<string>> mapping)
    {
        foreach (var (boxId, paths) in mapping)
        {
            var box = _config.Boxes.FirstOrDefault(b => b.Id == boxId);
            if (box is null || box.Kind == BoxKind.Portal)
            {
                continue;
            }

            box.Items.AddRange(paths);
        }

        foreach (var box in _config.Boxes)
        {
            box.Items = box.Items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>清理已不存在的路径（含排除列表，文件删除后排除标记自然失效）。</summary>
    private void Prune()
    {
        foreach (var box in _config.Boxes)
        {
            box.Items.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
            box.ManualItems.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
        }

        _config.ExcludedPaths.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
        _config.ExcludedPaths = _config.ExcludedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
