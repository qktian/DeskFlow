namespace DeskFlow.Core.Models;

public enum PomodoroPhase { Focus, ShortBreak, LongBreak }
public enum PomodoroStatus { Ready, Running, Paused }

public sealed class PomodoroSettings
{
    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public int LongBreakEvery { get; set; } = 4;
    public int DailyGoal { get; set; } = 8;
    public bool AutoStartBreak { get; set; } = true;
    public bool AutoStartFocus { get; set; }
    public bool SoundEnabled { get; set; } = true;
    public bool NotificationEnabled { get; set; } = true;
    public bool AlwaysOnTop { get; set; }

    public void Normalize()
    {
        FocusMinutes = Math.Clamp(FocusMinutes, 1, 180);
        ShortBreakMinutes = Math.Clamp(ShortBreakMinutes, 1, 60);
        LongBreakMinutes = Math.Clamp(LongBreakMinutes, 1, 120);
        LongBreakEvery = Math.Clamp(LongBreakEvery, 2, 12);
        DailyGoal = Math.Clamp(DailyGoal, 1, 30);
    }
    public int Seconds(PomodoroPhase phase) => 60 * (phase switch
    {
        PomodoroPhase.ShortBreak => ShortBreakMinutes,
        PomodoroPhase.LongBreak => LongBreakMinutes,
        _ => FocusMinutes
    });
}

public sealed class PomodoroSession
{
    public PomodoroPhase Phase { get; set; }
    public PomodoroStatus Status { get; set; }
    public double RemainingSeconds { get; set; } = 25 * 60;
    public int DurationSeconds { get; set; } = 25 * 60;
    public int CompletedInCycle { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? TaskId { get; set; }
    public string TaskTitle { get; set; } = "自由专注";
}

public sealed class PomodoroRecord
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public double FocusSeconds { get; set; }
    public bool Completed { get; set; }
    public string? TaskId { get; set; }
    public string TaskTitle { get; set; } = "自由专注";
}

public sealed class PomodoroData
{
    public PomodoroSettings Settings { get; set; } = new();
    public PomodoroSession Session { get; set; } = new();
    public List<PomodoroRecord> History { get; set; } = new();
    public bool ShowPanel { get; set; } = true;
    public bool Compact { get; set; }
    public string ScreenId { get; set; } = "";
    public double X { get; set; } = 40;
    public double Y { get; set; } = 80;

    public void Normalize()
    {
        Settings ??= new(); Settings.Normalize();
        Session ??= new(); History ??= new();
        History.RemoveAll(r => r is null || !double.IsFinite(r.FocusSeconds) || r.FocusSeconds < 0);
        if (!Enum.IsDefined(Session.Phase)) Session.Phase = PomodoroPhase.Focus;
        if (!Enum.IsDefined(Session.Status)) Session.Status = PomodoroStatus.Ready;
        Session.DurationSeconds = Math.Clamp(Session.DurationSeconds, 60, 10800);
        Session.RemainingSeconds = double.IsFinite(Session.RemainingSeconds)
            ? Math.Clamp(Session.RemainingSeconds, 0, Session.DurationSeconds) : Session.DurationSeconds;
        Session.CompletedInCycle = Math.Clamp(Session.CompletedInCycle, 0, 12);
        Session.TaskTitle = string.IsNullOrWhiteSpace(Session.TaskTitle) ? "自由专注" : Session.TaskTitle;
        if (!double.IsFinite(X)) X = 40;
        if (!double.IsFinite(Y)) Y = 80;
        ScreenId ??= "";
    }
}
