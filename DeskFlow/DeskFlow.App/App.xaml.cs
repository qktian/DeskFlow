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

    private Mutex? _mutex;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitSignalRegistration;
    private bool _initialized;
    private AppConfig _config = null!;
    private BoxManager _manager = null!;
    private TodoManager _todoManager = null!;
    private TrayController _tray = null!;
    private DesktopWatcher _watcher = null!;
    private DesktopDoubleClickHook _desktopHook = null!;
    private GlobalHotkeyService _hotkeys = null!;
    private SettingsWindow? _settingsWindow;
    private TodoHistoryWindow? _historyWindow;
    private DispatcherTimer? _iconGuardTimer;
    private volatile bool _restoreIconsPending;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var isExitRequest = e.Args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // 已有实例在运行：--exit 用于通知它优雅退出（更新 / 卸载场景）
            if (isExitRequest)
            {
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(ExitSignalName);
                    signal.Set();
                }
                catch
                {
                    // 实例刚好退出时忽略
                }
            }

            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"DeskFlow 遇到问题：\n{args.Exception.Message}\n\n如果频繁出现，请在设置中重置为默认配置。",
                "DeskFlow",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };

        _config = ConfigService.Load();
        _initialized = true;

        // 亚克力效果在 WPF 分层窗口上会渲染为纯黑不透明，V1.0 已移除该特性；
        // 这里把旧配置里遗留的开关强制归零，避免任何路径再触发它。
        _config.General.UseAcrylic = false;

        if (_config.General.AutoStart)
        {
            AutoStartService.SetEnabled(true);
        }

        ApplyDesktopIcons();

        _manager = new BoxManager(_config);
        _manager.SettingsRequested += (_, _) => OpenSettings();
        _manager.TodoCreateRequested += (_, _) => _todoManager.AddBoard();
        _manager.Initialize();

        _todoManager = new TodoManager(_config);
        _todoManager.HistoryRequested += (_, _) => OpenHistory();
        _todoManager.Initialize();

        _tray = new TrayController(_manager, _todoManager);
        _tray.SettingsRequested += (_, _) => OpenSettings();
        _tray.HistoryRequested += (_, _) => OpenHistory();
        _tray.ExitRequested += (_, _) => Shutdown();
        _tray.SystemIconsVisibilityChanged += (_, _) => ApplyDesktopIcons();

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
            (_, _) => Dispatcher.InvokeAsync(() => Shutdown()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        if (e.Args.Any(a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Shutdown(0);
            };
            timer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_initialized)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                SystemEvents.SessionEnding -= OnSessionEnding;

                // 退出即恢复：停止守护与后台隐藏重试，确保系统桌面图标一定还原
                _restoreIconsPending = true;
                StopIconGuard();

                _desktopHook?.Dispose();
                _hotkeys?.Dispose();
                _watcher?.Dispose();
                _tray?.Dispose();
                _todoManager?.Dispose();
                _manager?.Icons.Dispose();

                DesktopIconService.SetIconsVisibleWithRetry(true, attempts: 4);
                _manager?.Save();
            }
        }
        catch
        {
            // 退出阶段不再弹窗打扰用户
        }
        finally
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch
            {
                // 第二实例没有获得互斥体所有权，无需释放
            }

            _mutex?.Dispose();
            base.OnExit(e);
        }
    }

    private void OnDesktopChanged(object? sender, DesktopChangedEventArgs e)
    {
        // 文件被替换后图标可能已变化，先让缓存失效，再重新归类
        foreach (var path in e.Paths)
        {
            _manager.Icons.Remove(path);
        }

        _manager.AssignNewItems();
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
        _hotkeys.Register(GlobalHotkeyService.ToggleBoxesId, _config.General.Hotkeys.ToggleBoxes);
        _hotkeys.Register(GlobalHotkeyService.OrganizeId, _config.General.Hotkeys.Organize);
    }

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
        if (_config.General.HideSystemIcons)
        {
            HideSystemIconsInBackground();
            StartIconGuard();
        }
        else
        {
            StopIconGuard();
            DesktopIconService.SetIconsVisibleWithRetry(true, attempts: 3);
        }
    }

    private void HideSystemIconsInBackground()
    {
        _restoreIconsPending = false;

        _ = Task.Run(() =>
        {
            for (var i = 0; i < 12 && !_restoreIconsPending; i++)
            {
                try
                {
                    if (DesktopIconService.TrySetIconsVisible(false))
                    {
                        return;
                    }
                }
                catch
                {
                    // explorer 尚未就绪，继续重试
                }

                Thread.Sleep(1500);
            }
        });
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
            if (!_config.General.HideSystemIcons)
            {
                StopIconGuard();
                return;
            }

            // 状态已符合时不会发送切换命令，因此不会造成闪烁
            DesktopIconService.TrySetIconsVisible(false);
        };

        _iconGuardTimer.Start();
    }

    private void StopIconGuard() => _iconGuardTimer?.Stop();

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_config, _manager);
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

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() => _manager.EnsureOnScreen());
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        // 注销 / 关机：系统可能不会给完整的退出流程，这里先行恢复
        _restoreIconsPending = true;
        StopIconGuard();
        DesktopIconService.SetIconsVisibleWithRetry(true, attempts: 4);
        _manager.Save();
    }
}
