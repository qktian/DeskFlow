using System.Windows.Forms;
using DeskFlow.App;
using DeskFlow.Shell;
using DeskFlow.Core.Services;
using System.Diagnostics;
using System.IO;

namespace DeskFlow.App.Services;

/// <summary>系统托盘图标与右键菜单。</summary>
public sealed class TrayController : IDisposable
{
    private readonly BoxManager _manager;
    private readonly TodoManager _todoManager;
    private readonly PomodoroManager _pomodoro;
    private readonly ToolStripMenuItem _pomodoroToggle = new("开始专注");
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _restoreSnapshotItem = new("恢复快照");
    private readonly ToolStripMenuItem _undoOrganizeItem = new("撤销上次整理");
    private bool _disposed;
    private bool _pomodoroBalloon;
    private string _lastTip = "";

    public TrayController(BoxManager manager, TodoManager todoManager, PomodoroManager pomodoro)
    {
        _manager = manager;
        _todoManager = todoManager;
        _pomodoro = pomodoro;

        var menu = BuildMenu();
        menu.Opening += (_, _) => { RebuildRestoreSnapshotMenu(); RefreshPomodoroMenu(); _undoOrganizeItem.Enabled = _manager.CanUndoOrganize; };

        _notifyIcon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "DeskFlow 桌面整理",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.MouseClick += OnMouseClick;
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();
        _notifyIcon.BalloonTipClicked += (_, _) => { if (_pomodoroBalloon) _pomodoro.Show(); };
        _pomodoro.Refreshed += RefreshPomodoroTooltip;
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler? ExitRequested;
    public event EventHandler? RestoreDesktopRequested;

    /// <summary>托盘菜单请求打开「待办历史」窗口。</summary>
    public event EventHandler? HistoryRequested;
    public event EventHandler? SearchRequested;

    /// <summary>托盘切换系统桌面图标显隐后，请求宿主重新应用策略（含后台守护）。</summary>
    public event EventHandler? SystemIconsVisibilityChanged;

    public void ShowBalloon(string title, string message)
    {
        _pomodoroBalloon = false;
        _notifyIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
    }

    public void ShowPomodoroBalloon(string title, string message)
    {
        _pomodoroBalloon = true;
        _notifyIcon.ShowBalloonTip(5000, title, message + " 点击打开番茄钟。", ToolTipIcon.Info);
    }

    private void RefreshPomodoroTooltip()
    {
        if (_disposed) return;
        var session = _pomodoro.Engine.Session;
        var seconds = (int)Math.Ceiling(session.RemainingSeconds);
        var state = session.Status == DeskFlow.Core.Models.PomodoroStatus.Running ? "进行中" :
            session.Status == DeskFlow.Core.Models.PomodoroStatus.Paused ? "已暂停" : "待开始";
        var tip = $"DeskFlow · {PomodoroEngine.Label(session.Phase)} {seconds / 60:00}:{seconds % 60:00} · {state}";
        if (tip == _lastTip) return;
        _lastTip = tip; _notifyIcon.Text = tip;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(CreateItem("显示 / 隐藏盒子", (_, _) => _manager.ToggleVisible()));
        menu.Items.Add(CreateItem("显示 / 隐藏系统桌面图标", (_, _) =>
        {
            var general = _manager.Config.General;
            general.HideSystemIcons = !general.HideSystemIcons;
            _manager.Save();
            SystemIconsVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }));
        menu.Items.Add(CreateItem("一键整理桌面", (_, _) => _manager.OrganizeAll()));
        _undoOrganizeItem.Click += (_, _) => _manager.UndoOrganize();
        menu.Items.Add(_undoOrganizeItem);
        menu.Items.Add(CreateItem("搜索已收纳条目…", (_, _) => SearchRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateItem("新建盒子…", (_, _) =>
        {
            var dialog = new InputDialog("新建盒子", "盒子名称", "新建盒子");
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                _manager.AddBox(dialog.InputText.Trim());
            }
        }));
        menu.Items.Add(CreateItem("显示 / 隐藏待办组件", (_, _) => _todoManager.ToggleVisible()));
        menu.Items.Add(CreateItem("新建待办组件…", (_, _) =>
        {
            var dialog = new InputDialog("新建待办组件", "清单名称", "待办事项");
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                _todoManager.AddBoard(dialog.InputText.Trim());
            }
        }));

        menu.Items.Add(CreateItem("番茄钟…", (_, _) => _pomodoro.Show()));
        _pomodoroToggle.Click += (_, _) => _pomodoro.Toggle();
        menu.Items.Add(_pomodoroToggle);
        menu.Items.Add(CreateItem("待办历史…", (_, _) => HistoryRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateItem("清空回收站…", (_, _) => RecycleBinActions.ShowEmptyRecycleBinDialog()));

        menu.Items.Add(CreateItem("保存桌面快照…", (_, _) =>
        {
            var dialog = new InputDialog("保存快照", "快照名称", $"快照 {DateTime.Now:yyyy-MM-dd HH:mm}");
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                _manager.CreateSnapshot(dialog.InputText.Trim());
            }
        }));
        menu.Items.Add(_restoreSnapshotItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("恢复系统桌面图标", (_, _) => RestoreDesktopRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateItem("打开诊断日志", (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(DiagnosticLog.Current.DirectoryPath);
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add(DiagnosticLog.Current.DirectoryPath);
                Process.Start(start);
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("Diagnostics", "Cannot open log directory", error);
                ShowBalloon("无法打开日志目录", DiagnosticLog.Current.DirectoryPath);
            }
        }));
        menu.Items.Add(CreateItem("设置…", (_, _) => OpenSettings()));
        menu.Items.Add(CreateItem("退出", (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        return menu;
    }

    private void RefreshPomodoroMenu()
    {
        var session = _pomodoro.Engine.Session;
        var seconds = (int)Math.Ceiling(session.RemainingSeconds);
        var action = session.Status switch
        {
            DeskFlow.Core.Models.PomodoroStatus.Running => "暂停",
            DeskFlow.Core.Models.PomodoroStatus.Paused => "继续", _ => "开始"
        };
        _pomodoroToggle.Text = $"{action}{PomodoroEngine.Label(session.Phase)} · {seconds / 60:00}:{seconds % 60:00}";
    }

    private static ToolStripMenuItem CreateItem(string text, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += onClick;
        return item;
    }

    /// <summary>打开托盘菜单时重建「恢复快照」子菜单，反映最新快照列表。</summary>
    private void RebuildRestoreSnapshotMenu()
    {
        _restoreSnapshotItem.DropDownItems.Clear();

        var snapshots = _manager.Config.Snapshots
            .OrderByDescending(s => s.CreatedAt)
            .ToList();

        if (snapshots.Count == 0)
        {
            _restoreSnapshotItem.DropDownItems.Add(new ToolStripMenuItem("（暂无快照）") { Enabled = false });
            return;
        }

        foreach (var snapshot in snapshots)
        {
            var snap = snapshot;
            var item = new ToolStripMenuItem($"{snap.Name}（{snap.CreatedAt:MM-dd HH:mm}）");
            item.Click += (_, _) =>
            {
                var (restored, skipped) = _manager.RestoreSnapshot(snap);
                _notifyIcon.ShowBalloonTip(3000, "快照已恢复",
                    $"还原 {restored} 项，忽略 {skipped} 个已不存在的条目。", ToolTipIcon.Info);
            };
            _restoreSnapshotItem.DropDownItems.Add(item);
        }
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _manager.ToggleVisible();
        }
    }

    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _pomodoro.Refreshed -= RefreshPomodoroTooltip;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _disposed = true;
    }
}
