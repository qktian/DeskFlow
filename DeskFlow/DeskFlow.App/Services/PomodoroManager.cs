using System.Media;
using System.Windows;
using System.Windows.Threading;
using DeskFlow.Core.Models;
using DeskFlow.Core.Services;
using Microsoft.Win32;

namespace DeskFlow.App.Services;

/// <summary>Application-owned timer. Window visibility never controls the clock.</summary>
public sealed class PomodoroManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private PomodoroWindow? _window;
    private int _checkpointTicks;
    private bool _disposed;
    public PomodoroEngine Engine { get; private set; }
    public AppConfig Config => _config;
    public event Action? Refreshed;
    public event Action<string, string>? Notification;

    public PomodoroManager(AppConfig config)
    {
        _config = config;
        Engine = CreateEngine();
        _timer.Tick += OnTick;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private PomodoroEngine CreateEngine()
    {
        var engine = new PomodoroEngine(_config.Pomodoro);
        engine.Changed += SaveAndRefresh;
        engine.PhaseCompleted += Completed;
        return engine;
    }

    public void Initialize()
    {
        SaveAndRefresh();
        if (_config.Pomodoro.ShowPanel) Show();
        _timer.Start();
    }

    public void ReloadFromConfig()
    {
        if (_window is not null) { _window.AppClosing = true; _window.Close(); }
        Engine.Changed -= SaveAndRefresh;
        Engine.PhaseCompleted -= Completed;
        Engine = CreateEngine();
        if (Engine.Data.ShowPanel) Show();
        SaveAndRefresh();
    }

    public void Show(TodoItemModel? task = null)
    {
        if (_disposed) return;
        if (_window is null)
        {
            _window = new PomodoroWindow(this);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _config.Pomodoro.ShowPanel = true;
        if (task is not null) _window.UseTask(task);
        SaveAndRefresh();
    }

    public void Toggle()
    {
        if (Engine.Session.Status == PomodoroStatus.Running) Engine.Pause();
        else Engine.Start();
    }

    public void EnsureOnScreen() => _window?.EnsureOnScreen();
    public void SaveAndRefresh()
    {
        ConfigService.Save(_config);
        Refreshed?.Invoke();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Engine.Tick();
        Refreshed?.Invoke();
        if (++_checkpointTicks < 60) return;
        _checkpointTicks = 0;
        if (Engine.Session.Status == PomodoroStatus.Running) ConfigService.Save(_config);
    }

    private void Completed(PomodoroPhase phase)
    {
        if (_disposed) return;
        if (Engine.Data.Settings.SoundEnabled)
        {
            try { SystemSounds.Asterisk.Play(); }
            catch (Exception error) { DiagnosticLog.Error("Pomodoro", "Notification sound failed", error); }
        }
        if (Engine.Data.Settings.NotificationEnabled)
            Notification?.Invoke($"番茄钟 · {PomodoroEngine.Label(phase)}结束", Engine.Notice);
    }

    private void Suspend(string reason)
    {
        if (_disposed) return;
        Engine.Pause(reason);
        SaveAndRefresh();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            DispatchSuspend("电脑休眠，计时已暂停；唤醒后可继续。");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            DispatchSuspend("电脑已锁定或会话断开，计时已暂停。");
    }

    private void DispatchSuspend(string reason)
    {
        if (_dispatcher.HasShutdownStarted) return;
        // Synchronous handoff: the checkpoint is written before suspend, bounded against a stuck UI.
        try { _dispatcher.Invoke(() => Suspend(reason), DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2)); }
        catch (Exception error) { DiagnosticLog.Error("Pomodoro", "Suspend checkpoint deferred", error); }
    }

    public void PrepareForExit()
    {
        if (_window is not null) _window.AppClosing = true;
        CheckpointForExit();
    }

    public void CheckpointForExit() => Suspend("已保存退出前的进度，点击继续。");

    public void Dispose()
    {
        if (_disposed) return;
        CheckpointForExit();
        _disposed = true;
        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        // Do not turn off startup visibility when the application itself exits.
        if (_window is not null) { _window.AppClosing = true; _window.Close(); }
        Engine.Changed -= SaveAndRefresh;
        Engine.PhaseCompleted -= Completed;
        SaveAndRefresh();
    }
}
