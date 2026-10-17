using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DeskFlow.App.Services;
using DeskFlow.Core.Models;
using DeskFlow.Core.Services;
using DeskFlow.Shell;
using Microsoft.Win32;

namespace DeskFlow.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "DeskFlow.SingleInstance.Mutex";
    private const string ExitSignalName = "DeskFlow.ExitSignal";
    private const string RestoreSignalName = "DeskFlow.RestoreDesktopSignal";
    private EventWaitHandle? _restoreSignal;
    private RegisteredWaitHandle? _restoreRegistration;
    private DesktopRecovery? _recovery;
    private bool _recoveryWarningShown;
    private bool _shuttingDown;
    private bool _iconWarningShown;

    private Mutex? _mutex;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitSignalRegistration;
    private bool _initialized;
    private AppConfig _config = null!;
    private BoxManager _manager = null!;
    private TodoManager _todoManager = null!;
    private PomodoroManager _pomodoro = null!;
    private SearchWindow? _searchWindow;
    private TrayController _tray = null!;
    private DesktopWatcher _watcher = null!;
    private DesktopDoubleClickHook _desktopHook = null!;
    private GlobalHotkeyService _hotkeys = null!;
    private SettingsWindow? _settingsWindow;
    private TodoHistoryWindow? _historyWindow;
    private DispatcherTimer? _iconGuardTimer;
    private CancellationTokenSource? _hideIconsCancellation;
    private volatile bool _restoreIconsPending;
    private bool _saveWarningShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 2 && e.Args[0] == DesktopRecovery.WatchArgument)
        {
            ShutdownWithCheckpoint(DesktopRecovery.RunGuardian(e.Args[1]));
            return;
        }

        var isRecoveryRequest = e.Args.Any(a => a.Equals("--recover-desktop", StringComparison.OrdinalIgnoreCase));

        var isExitRequest = e.Args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // 已有实例在运行：--exit 用于通知它优雅退出（更新 / 卸载场景）
            if (isExitRequest || isRecoveryRequest)
            {
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(isExitRequest ? ExitSignalName : RestoreSignalName);
                    signal.Set();
                }
                catch (Exception error)
                {
                    DiagnosticLog.Error("Control", "Could not signal the running instance; retry when startup completes.", error);
                }
            }

            ShutdownWithCheckpoint();
            return;
        }

        if (isExitRequest)
        {
            ShutdownWithCheckpoint();
            return;
        }

        if (isRecoveryRequest)
        {
            using var recovery = new DesktopRecovery();
            var restored = recovery.Restore();
            DiagnosticLog.Info("Recovery", $"Manual recovery result: {restored}");
            if (!restored) MessageBox.Show("Explorer 尚未就绪。请等待资源管理器恢复后，再运行 --recover-desktop。", "DeskFlow");
            ShutdownWithCheckpoint(restored ? 0 : 1);
            return;
        }

        DiagnosticLog.Info("Startup", $"Starting {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}; OS={Environment.OSVersion}; runtime={Environment.Version}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagnosticLog.Error("Fatal", "Unhandled process exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticLog.Error("Task", "Unobserved background exception", args.Exception);
            args.SetObserved();
        };
        ConfigService.SaveFailed += OnConfigSaveFailed;
        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticLog.Error("Fatal", "Unhandled UI exception; shutting down safely", args.Exception);
            StopIconGuard();
            _recovery?.Restore();
            MessageBox.Show(
                $"DeskFlow 遇到问题，将安全退出：\n{args.Exception.Message}\n\n诊断日志：{DiagnosticLog.Current.DirectoryPath}\n可重新启动程序；系统图标可通过 --recover-desktop 恢复。",
                "DeskFlow",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
            ShutdownWithCheckpoint(1);
        };

        _config = ConfigService.Load();
        ScreenHelper.MigrateLegacyCoordinates(_config);
        DiagnosticLog.Info("Config", $"Loaded configuration schema v{_config.Version}; displays={ScreenHelper.GetMonitors().Count}");
        _initialized = true;

        // 亚克力效果在 WPF 分层窗口上会渲染为纯黑不透明，V1.0 已移除该特性；
        // 这里把旧配置里遗留的开关强制归零，避免任何路径再触发它。
        _config.General.UseAcrylic = false;

        if (_config.General.AutoStart)
        {
            AutoStartService.SetEnabled(true);
        }

        _manager = new BoxManager(_config);
        _manager.SettingsRequested += (_, _) => OpenSettings();
        _manager.TodoCreateRequested += (_, _) => _todoManager.AddBoard();
        _manager.Initialize();
        _manager.EnsureOnScreen();

        _todoManager = new TodoManager(_config);
        _todoManager.HistoryRequested += (_, _) => OpenHistory();
        _todoManager.Initialize();
        _manager.LayoutRestored += (_, _) => _todoManager.RebuildFromConfig();

        _pomodoro = new PomodoroManager(_config);
        _todoManager.PomodoroRequested += task => _pomodoro.Show(task);
        _tray = new TrayController(_manager, _todoManager, _pomodoro);
        _pomodoro.Notification += (title, message) => _tray.ShowPomodoroBalloon(title, message);
        _pomodoro.Initialize();
        _tray.SettingsRequested += (_, _) => OpenSettings();
        _tray.HistoryRequested += (_, _) => OpenHistory();
        _tray.SearchRequested += (_, _) => OpenSearch();
        _tray.ExitRequested += (_, _) => ShutdownWithCheckpoint();
        _tray.SystemIconsVisibilityChanged += (_, _) => ApplyDesktopIcons();
        _tray.RestoreDesktopRequested += (_, _) => RestoreDesktopManually();
        _recovery = new DesktopRecovery();
        ApplyDesktopIcons();

        _watcher = new DesktopWatcher();
        _watcher.DesktopChanged += OnDesktopChanged;
        _watcher.Start();

        _desktopHook = new DesktopDoubleClickHook();
        _desktopHook.DesktopDoubleClicked += (_, _) => _manager.ToggleVisible();
        ApplyDesktopHook();

        _hotkeys = new GlobalHotkeyService();
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        ApplyHotkeys();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        _exitSignal = new EventWaitHandle(false, EventResetMode.ManualReset, ExitSignalName);
        _exitSignalRegistration = ThreadPool.RegisterWaitForSingleObject(
            _exitSignal,
            (_, _) => Dispatcher.InvokeAsync(() => ShutdownWithCheckpoint()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        _restoreSignal = new EventWaitHandle(false, EventResetMode.AutoReset, RestoreSignalName);
        _restoreRegistration = ThreadPool.RegisterWaitForSingleObject(_restoreSignal,
            (_, _) => Dispatcher.InvokeAsync(RestoreDesktopManually), null, Timeout.Infinite, false);

        if (e.Args.Any(a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ShutdownWithCheckpoint(0);
            };
            timer.Start();
        }
    }

    private void ShutdownWithCheckpoint(int exitCode = 0)
    {
        _pomodoro?.PrepareForExit();
        Shutdown(exitCode);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _pomodoro?.PrepareForExit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        ConfigService.SaveFailed -= OnConfigSaveFailed;
        if (_initialized)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            ReliableCleanup.Run(
                ("Stop icon guard", StopIconGuard),
                ("Restore desktop", () => _recovery?.Restore()),
                ("Pomodoro", () => _pomodoro?.Dispose()),
                ("Save configuration", () => _manager?.Save()),
                ("Mouse hook", () => _desktopHook?.Dispose()),
                ("Hotkeys", () => _hotkeys?.Dispose()),
                ("Desktop watcher", () => _watcher?.Dispose()),
                ("Tray", () => _tray?.Dispose()),
                ("Todos", () => _todoManager?.Dispose()),
                ("Icons", () => _manager?.Icons.Dispose()),
                ("Guardian handle", () => _recovery?.Dispose()));
            DiagnosticLog.Info("Shutdown", $"Exit code {e.ApplicationExitCode}");
        }
        _restoreRegistration?.Unregister(null);
        _restoreSignal?.Dispose();
        _exitSignalRegistration?.Unregister(null);
        _exitSignal?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDesktopChanged(object? sender, DesktopChangedEventArgs e)
    {
        foreach (var rename in e.Renames) _manager.RenameItem(rename.OldPath, rename.NewPath);
        // 文件被替换后图标可能已变化，先让缓存失效，再重新归类
        foreach (var path in e.Paths)
        {
            _manager.Icons.Remove(path);
            FileMetadataCache.Invalidate(path);
        }

        if (e.FullRescan) _manager.QueueAssignNewItems();
        else _manager.UpdateChangedItems(e.Paths);
    }

    private void OnHotkeyPressed(object? sender, int id)
    {
        switch (id)
        {
            case GlobalHotkeyService.ToggleBoxesId:
                _manager.ToggleVisible();
                break;
            case GlobalHotkeyService.OrganizeId:
                _manager.OrganizeAll();
                break;
        }
    }

    private void ApplyHotkeys()
    {
        var error = TryApplyHotkeys(_config.General.Hotkeys.ToggleBoxes, _config.General.Hotkeys.Organize);
        if (error is not null)
        {
            // At startup, a conflict for one action should not disable an unrelated free key.
            if (_hotkeys.Active.Count == 0)
            {
                _hotkeys.Register(GlobalHotkeyService.ToggleBoxesId, _config.General.Hotkeys.ToggleBoxes);
                _hotkeys.Register(GlobalHotkeyService.OrganizeId, _config.General.Hotkeys.Organize);
            }
            _tray.ShowBalloon("快捷键未生效：请在设置中更换", error);
        }
    }

    private string? TryApplyHotkeys(string toggle, string organize) => _hotkeys.TryApply(
        new Dictionary<int, string> { [GlobalHotkeyService.ToggleBoxesId] = toggle, [GlobalHotkeyService.OrganizeId] = organize },
        out var error) ? null : error;

    private void ApplyDesktopHook()
    {
        if (_config.General.DoubleClickToHide)
        {
            _desktopHook.Start();
        }
        else
        {
            _desktopHook.Stop();
        }
    }

    /// <summary>
    /// 应用「运行时隐藏系统桌面图标、退出时恢复」策略。
    /// 隐藏在后台重试（开机自启时 explorer 可能还没创建桌面窗口），
    /// 并启动一个低频守护，在 explorer 重启后把图标重新收编。
    /// </summary>
    private void ApplyDesktopIcons()
    {
        if (_shuttingDown) return;
        if (_config.General.HideSystemIcons)
        {
            if (_recovery?.EnsureArmed() != true)
            {
                StopIconGuard();
                if (!_recoveryWarningShown)
                    _tray?.ShowBalloon("桌面恢复保护未启动", "已暂停隐藏系统图标。请重启 DeskFlow，或从托盘打开诊断日志。");
                _recoveryWarningShown = true;
                return;
            }
            HideSystemIconsInBackground();
            StartIconGuard();
        }
        else
        {
            StopIconGuard();
            if (_recovery?.Restore() != true) WarnDesktopUnavailable();
            else _iconWarningShown = false;
        }
    }

    private async void HideSystemIconsInBackground()
    {
        _hideIconsCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _hideIconsCancellation = cancellation;
        _restoreIconsPending = false;
        try
        {
            for (var i = 0; i < 12 && !_restoreIconsPending && _config.General.HideSystemIcons; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    if (DesktopIconService.TrySetIconsVisible(false))
                    {
                        _iconWarningShown = false;
                        return;
                    }
                }
                catch (Exception error)
                {
                    DiagnosticLog.Error("DesktopIcons", "Hide attempt failed", error);
                }

                // Resume on the UI dispatcher so show/hide/exit cannot race each other.
                await Task.Delay(1500, cancellation.Token);
            }
            if (!_restoreIconsPending && !cancellation.IsCancellationRequested) WarnDesktopUnavailable();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_hideIconsCancellation, cancellation)) _hideIconsCancellation = null;
            cancellation.Dispose();
        }
    }

    private void StartIconGuard()
    {
        if (_iconGuardTimer is not null)
        {
            _iconGuardTimer.Start();
            return;
        }

        _iconGuardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _iconGuardTimer.Tick += (_, _) =>
        {
            if (_restoreIconsPending || !_config.General.HideSystemIcons)
            {
                StopIconGuard();
                return;
            }

            // 状态已符合时不会发送切换命令，因此不会造成闪烁
            if (_recovery?.EnsureArmed() == true)
            {
                if (DesktopIconService.TrySetIconsVisible(false)) _iconWarningShown = false;
                else WarnDesktopUnavailable();
            }
            else ApplyDesktopIcons();
        };

        _iconGuardTimer.Start();
    }

    private void StopIconGuard()
    {
        _restoreIconsPending = true;
        _hideIconsCancellation?.Cancel();
        _iconGuardTimer?.Stop();
    }

    private void WarnDesktopUnavailable()
    {
        if (_iconWarningShown || _shuttingDown) return;
        _iconWarningShown = true;
        DiagnosticLog.Error("DesktopIcons", "Explorer unavailable or unresponsive; desktop icon operation not completed.");
        _tray?.ShowBalloon("桌面图标操作尚未完成", "Explorer 尚未就绪或无响应。程序会继续尝试；也可在资源管理器恢复后，从托盘选择恢复系统桌面图标。");
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_config, _manager);
        _settingsWindow.ValidateHotkeys = TryApplyHotkeys;
        _settingsWindow.RebuildTodos = _todoManager.RebuildFromConfig;
        _settingsWindow.OpenPomodoro = () => _pomodoro.Show();
        _settingsWindow.ReloadPomodoro = _pomodoro.ReloadFromConfig;
        _settingsWindow.SettingsApplied += (_, _) =>
        {
            ApplyHotkeys();
            ApplyDesktopHook();
            ApplyDesktopIcons();
            _manager.ApplyStyles();
            _todoManager.RefreshAll();
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>打开「待办历史」：跨清单汇总所有普通 / 循环任务。</summary>
    private void OpenHistory()
    {
        if (_historyWindow is not null)
        {
            _historyWindow.Activate();
            return;
        }

        _historyWindow = new TodoHistoryWindow(_config, _todoManager);
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
    }

    private void OpenSearch()
    {
        if (_searchWindow is not null) { _searchWindow.Activate(); return; }
        _searchWindow = new SearchWindow(_manager);
        _searchWindow.Closed += (_, _) => _searchWindow = null;
        _searchWindow.Show();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _manager.EnsureOnScreen();
            _todoManager.EnsureOnScreen();
            _pomodoro.EnsureOnScreen();
            _manager.Save();
        });
    }

    private void OnConfigSaveFailed(Exception error)
    {
        if (_saveWarningShown) return;
        _saveWarningShown = true;
        Dispatcher.BeginInvoke(() => MessageBox.Show(
            $"配置未能保存，当前修改可能在退出后丢失。请检查配置目录的权限和磁盘空间。\n\n{ConfigService.ConfigDirectory}\n{error.Message}",
            "DeskFlow 保存失败", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        _restoreIconsPending = true;
        try
        {
            // SystemEvents may run off the dispatcher; bound the wait if UI is occupied.
            Dispatcher.Invoke(() => ReliableCleanup.Run(
                ("Stop icon guard", StopIconGuard),
                ("Session desktop restore", () => _recovery?.Restore()),
                ("Pomodoro checkpoint", () => _pomodoro?.CheckpointForExit()),
                ("Session save", () => _manager?.Save())),
                DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(3));
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("SessionEnding", "UI cleanup unavailable; restoring desktop directly", error);
            _recovery?.Restore();
        }
    }

    private void RestoreDesktopManually()
    {
        _config.General.HideSystemIcons = false;
        _settingsWindow?.KeepDesktopIconsVisible();
        StopIconGuard();
        var restored = _recovery?.Restore() == true;
        _manager.Save();
        _tray.ShowBalloon(restored ? "系统桌面图标已恢复" : "恢复尚未完成",
            restored ? "已关闭运行时隐藏系统图标。" : "Explorer 尚未就绪，请稍后从托盘重试恢复，或运行 --recover-desktop。");
    }
}
