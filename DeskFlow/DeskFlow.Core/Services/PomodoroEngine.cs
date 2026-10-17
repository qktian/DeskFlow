using DeskFlow.Core.Models;

namespace DeskFlow.Core.Services;

/// <summary>Single timer shared by the panel, tray and todo entry points. No UI or disk access.</summary>
public sealed class PomodoroEngine
{
    private readonly TimeProvider _clock;
    private long _lastTimestamp;
    public PomodoroData Data { get; }
    public PomodoroSession Session => Data.Session;
    public event Action? Changed;
    public event Action<PomodoroPhase>? PhaseCompleted;
    public string Notice { get; private set; } = "";

    public PomodoroEngine(PomodoroData data, TimeProvider? clock = null)
    {
        Data = data; Data.Normalize(); _clock = clock ?? TimeProvider.System;
        // Offline time never earns focus credit, including abnormal termination.
        if (Session.Status == PomodoroStatus.Running)
        {
            Session.Status = PomodoroStatus.Paused;
            Notice = "已恢复上次进度，点击继续；离线时间不计入专注。";
        }
        if (Session.Status == PomodoroStatus.Ready) Prepare(Session.Phase);
    }

    public void Start()
    {
        if (Session.Status == PomodoroStatus.Running) return;
        Session.StartedAt ??= _clock.GetUtcNow();
        Session.Status = PomodoroStatus.Running;
        _lastTimestamp = _clock.GetTimestamp(); Notice = ""; Changed?.Invoke();
    }

    public void Pause(string notice = "已暂停，点击继续。")
    {
        if (Session.Status != PomodoroStatus.Running) return;
        // Tick may finish a phase, but never leave its automatic successor running on exit/lock.
        Tick();
        if (Session.Status == PomodoroStatus.Running) Session.Status = PomodoroStatus.Paused;
        Notice = notice; Changed?.Invoke();
    }

    public void Tick()
    {
        if (Session.Status != PomodoroStatus.Running) return;
        var now = _clock.GetTimestamp();
        var seconds = _clock.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        _lastTimestamp = now;
        // Protect against sleep and long dispatcher stalls even if the OS event was missed.
        if (seconds > 30 || seconds < 0)
        {
            Session.Status = PomodoroStatus.Paused;
            Notice = "检测到休眠或长时间中断，已暂停；中断时间不计入专注。";
            Changed?.Invoke(); return;
        }
        Session.RemainingSeconds = Math.Max(0, Session.RemainingSeconds - seconds);
        if (Session.RemainingSeconds > 0) return;
        var phase = Session.Phase;
        if (phase == PomodoroPhase.Focus)
        {
            AddRecord(true); Session.CompletedInCycle++;
        }
        if (phase == PomodoroPhase.LongBreak) Session.CompletedInCycle = 0;
        var next = phase == PomodoroPhase.Focus
            ? (Session.CompletedInCycle >= Data.Settings.LongBreakEvery ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak)
            : PomodoroPhase.Focus;
        Prepare(next);
        var autoStart = next == PomodoroPhase.Focus ? Data.Settings.AutoStartFocus : Data.Settings.AutoStartBreak;
        if (autoStart)
        {
            Session.StartedAt = _clock.GetUtcNow();
            Session.Status = PomodoroStatus.Running;
        }
        Notice = $"{Label(phase)}已结束，{(autoStart ? "已开始" : "等待开始")}{Label(next)}。";
        Changed?.Invoke(); PhaseCompleted?.Invoke(phase);
    }

    public void SelectTask(string? id, string title)
    {
        if (Session.StartedAt is not null || Session.Status != PomodoroStatus.Ready)
            throw new InvalidOperationException("本段计时已开始，请先重置再切换关联任务。");
        Session.TaskId = id;
        Session.TaskTitle = string.IsNullOrWhiteSpace(title) ? "自由专注" : title.Trim();
        Changed?.Invoke();
    }

    // Reset/skip are deliberate interruption actions, never completed tomatoes.
    public void Reset(bool wholeCycle = false)
    {
        CaptureInterruption();
        if (wholeCycle) Session.CompletedInCycle = 0;
        Prepare(wholeCycle ? PomodoroPhase.Focus : Session.Phase);
        Notice = "已重置；未完成的专注不计入番茄数。"; Changed?.Invoke();
    }

    public void SwitchTo(PomodoroPhase phase)
    {
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        CaptureInterruption(); Prepare(phase); Notice = ""; Changed?.Invoke();
    }

    public void Skip()
    {
        CaptureInterruption();
        var phase = Session.Phase;
        if (phase == PomodoroPhase.LongBreak) Session.CompletedInCycle = 0;
        Prepare(phase == PomodoroPhase.Focus
            ? (Session.CompletedInCycle >= Data.Settings.LongBreakEvery ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak)
            : PomodoroPhase.Focus);
        Notice = "已跳过，下一段需手动开始；跳过专注不计入番茄数。"; Changed?.Invoke();
    }

    public void ApplySettings(PomodoroSettings settings)
    {
        settings.Normalize(); Data.Settings = settings;
        // Active and paused sessions retain their originally selected duration.
        if (Session.Status == PomodoroStatus.Ready) Prepare(Session.Phase);
        Changed?.Invoke();
    }

    private void CaptureInterruption()
    {
        // Account elapsed time without triggering completion/automatic transitions during a reset.
        if (Session.Status == PomodoroStatus.Running)
        {
            var elapsed = _clock.GetElapsedTime(_lastTimestamp, _clock.GetTimestamp()).TotalSeconds;
            if (elapsed is >= 0 and <= 30) Session.RemainingSeconds = Math.Max(0, Session.RemainingSeconds - elapsed);
        }
        if (Session.Phase == PomodoroPhase.Focus) AddRecord(false);
    }

    private void AddRecord(bool completed)
    {
        if (Session.StartedAt is not { } start) return;
        var seconds = Session.DurationSeconds - Session.RemainingSeconds;
        if (!completed && seconds < 1) return;
        Data.History.Add(new PomodoroRecord
        {
            StartedAt = start, EndedAt = _clock.GetUtcNow(), FocusSeconds = seconds,
            Completed = completed, TaskId = Session.TaskId, TaskTitle = Session.TaskTitle
        });
    }

    private void Prepare(PomodoroPhase phase)
    {
        Session.Phase = phase; Session.Status = PomodoroStatus.Ready;
        Session.DurationSeconds = Data.Settings.Seconds(phase);
        Session.RemainingSeconds = Session.DurationSeconds; Session.StartedAt = null;
    }

    public static string Label(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.ShortBreak => "短休息", PomodoroPhase.LongBreak => "长休息", _ => "专注"
    };
}
