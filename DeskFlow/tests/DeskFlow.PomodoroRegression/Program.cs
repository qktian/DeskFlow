using System.Text.Json;
using DeskFlow.Core.Models;
using DeskFlow.Core.Services;

var tests = new (string Name, Action Run)[]
{
    ("defaults and old configuration", () => {
        var config = JsonSerializer.Deserialize<AppConfig>("{\"Version\":2}")!;
        var engine = new PomodoroEngine(config.Pomodoro);
        Equal(1500d, engine.Session.RemainingSeconds); Equal(4, engine.Data.Settings.LongBreakEvery);
        Equal(PomodoroStatus.Ready, engine.Session.Status);
    }),
    ("pause excludes idle time", () => {
        var (e,c) = Make(); e.Start(); Advance(e,c,20); e.Pause();
        c.Advance(600); e.Tick(); Equal(40d,e.Session.RemainingSeconds);
        e.Start(); Advance(e,c,10); Equal(30d,e.Session.RemainingSeconds);
    }),
    ("complete once and keep history", () => {
        var (e,c) = Make(); var notices=0; e.PhaseCompleted += _ => notices++;
        e.Start(); Advance(e,c,60); for(var i=0;i<10;i++) e.Tick();
        Equal(1,e.Data.History.Count); Equal(1,notices); Equal(true,e.Data.History[0].Completed);
        Equal(60d,e.Data.History[0].FocusSeconds); Equal(PomodoroPhase.ShortBreak,e.Session.Phase);
    }),
    ("four tomatoes lead to long break", () => {
        var (e,c)=Make();
        for(var i=1;i<=4;i++) {
            e.Start(); Advance(e,c,60);
            Equal(i,e.Session.CompletedInCycle);
            Equal(i==4?PomodoroPhase.LongBreak:PomodoroPhase.ShortBreak,e.Session.Phase);
            e.Start(); Advance(e,c,60); Equal(PomodoroPhase.Focus,e.Session.Phase);
        }
        Equal(0,e.Session.CompletedInCycle); Equal(4,e.Data.History.Count);
    }),
    ("auto start and stop independently", () => {
        var (e,c)=Make(); e.Data.Settings.AutoStartBreak=true; e.Data.Settings.AutoStartFocus=false;
        e.Start(); Advance(e,c,60); Equal(PomodoroStatus.Running,e.Session.Status);
        Advance(e,c,60); Equal(PomodoroStatus.Ready,e.Session.Status);
        e.Data.Settings.AutoStartFocus=true; e.Start(); Advance(e,c,120);
        Equal(PomodoroPhase.Focus,e.Session.Phase); Equal(PomodoroStatus.Running,e.Session.Status);
    }),
    ("skip never earns tomato", () => {
        var (e,c)=Make(); e.Start(); Advance(e,c,20); e.Skip();
        Equal(0,e.Session.CompletedInCycle); Equal(false,e.Data.History.Single().Completed);
        Equal(20d,e.Data.History.Single().FocusSeconds); Equal(PomodoroStatus.Ready,e.Session.Status);
        e.Skip(); Equal(PomodoroPhase.Focus,e.Session.Phase); Equal(1,e.Data.History.Count);
    }),
    ("reset and new cycle retain history", () => {
        var (e,c)=Make(); e.Start(); Advance(e,c,60); e.Skip();
        e.Start(); Advance(e,c,15); e.Reset(); Equal(1,e.Session.CompletedInCycle);
        Equal(60d,e.Session.RemainingSeconds); Equal(2,e.Data.History.Count);
        e.Reset(true); Equal(0,e.Session.CompletedInCycle); Equal(2,e.Data.History.Count);
    }),
    ("restart restores paused without offline credit", () => {
        var (e,c)=Make(); e.Start(); Advance(e,c,20);
        var copy = ConfigService.Clone(e.Data); c.Advance(6000);
        var restored = new PomodoroEngine(copy,c);
        Equal(PomodoroStatus.Paused,restored.Session.Status); Equal(40d,restored.Session.RemainingSeconds);
        restored.Start(); Advance(restored,c,40); Equal(1,restored.Data.History.Count);
    }),
    ("long stall pauses without backfilling", () => {
        var (e,c)=Make(); e.Start(); Advance(e,c,10); c.Advance(10000); e.Tick();
        Equal(PomodoroStatus.Paused,e.Session.Status); Equal(50d,e.Session.RemainingSeconds); Equal(0,e.Data.History.Count);
    }),
    ("wall clock changes do not change duration", () => {
        var (e,c)=Make(); e.Start(); c.ShiftWallClock(86400); e.Tick(); Equal(60d,e.Session.RemainingSeconds);
        Advance(e,c,10); Equal(50d,e.Session.RemainingSeconds);
    }),
    ("settings keep active duration and update next phase", () => {
        var (e,c)=Make(); e.Start(); Advance(e,c,10); e.Pause();
        e.ApplySettings(new PomodoroSettings { FocusMinutes=2, ShortBreakMinutes=3 });
        Equal(50d,e.Session.RemainingSeconds); e.Start(); Advance(e,c,50);
        Equal(180d,e.Session.RemainingSeconds); e.Skip(); Equal(120d,e.Session.RemainingSeconds);
    }),
    ("task is immutable while started; history is snapshot", () => {
        var (e,c)=Make(); e.SelectTask("task-1","编写文档"); e.Start();
        var threw=false; try {e.SelectTask("task-2","其他");} catch(InvalidOperationException){threw=true;}
        Equal(true,threw); Advance(e,c,60); e.SelectTask(null,"自由专注");
        Equal("task-1",e.Data.History.Single().TaskId); Equal("编写文档",e.Data.History.Single().TaskTitle);
    }),
    ("switching modes does not complete partial focus", () => {
        var (e,c)=Make(); e.Start(); Advance(e,c,12); e.SwitchTo(PomodoroPhase.LongBreak);
        Equal(false,e.Data.History.Single().Completed); Equal(0,e.Session.CompletedInCycle);
        Equal(PomodoroStatus.Ready,e.Session.Status);
    }),
    ("pause on boundary also pauses auto-started successor", () => {
        var (e,c)=Make(); e.Data.Settings.AutoStartBreak=true; e.Start(); Advance(e,c,59); c.Advance(1); e.Pause();
        Equal(1,e.Data.History.Count); Equal(PomodoroPhase.ShortBreak,e.Session.Phase); Equal(PomodoroStatus.Paused,e.Session.Status);
    }),
    ("rapid repeat controls do not duplicate completed records", () => {
        var (e,c)=Make(); e.Start(); e.Start(); Advance(e,c,60); e.Pause(); e.Pause(); e.Reset(); e.Reset(); e.Skip();
        Equal(1,e.Data.History.Count); Equal(1,e.Session.CompletedInCycle);
    }),
    ("malformed imported fields normalized", () => {
        var data=new PomodoroData { Settings=new(){FocusMinutes=-1, LongBreakEvery=999},
            Session=new(){Phase=(PomodoroPhase)99,Status=(PomodoroStatus)99, RemainingSeconds=double.NaN},X=double.NaN};
        var e=new PomodoroEngine(data); Equal(1,data.Settings.FocusMinutes); Equal(12,data.Settings.LongBreakEvery);
        Equal(PomodoroPhase.Focus,e.Session.Phase); Equal(60d,e.Session.RemainingSeconds); Equal(40d,data.X);
    }),
    ("settings merge preserves live pomodoro progress", () => {
        var live=ConfigService.CreateDefault();var baseline=ConfigService.Clone(live);var draft=ConfigService.Clone(live);
        draft.General.AutoStart=!baseline.General.AutoStart;
        live.Pomodoro.Session.RemainingSeconds=345; live.Pomodoro.Session.Status=PomodoroStatus.Paused;
        live.Pomodoro.History.Add(new(){FocusSeconds=1500,Completed=true});
        var result=SettingsDraftMerge.Merge(baseline,draft,live);
        Equal(345d,result.Pomodoro.Session.RemainingSeconds); Equal(1,result.Pomodoro.History.Count);
    }),
    ("save and load retain active state with existing boxes", () => {
        var dir=Path.Combine(Path.GetTempPath(),"DeskFlow-Pomodoro-"+Guid.NewGuid().ToString("N"));
        try {
            var config=ConfigService.CreateDefault(); var count=config.Boxes.Count;
            config.Pomodoro.Session.Status=PomodoroStatus.Running;config.Pomodoro.Session.RemainingSeconds=123;
            config.Pomodoro.Session.StartedAt=DateTimeOffset.Now;
            Equal(true,ConfigService.Save(config,dir)); var loaded=ConfigService.Load(dir);
            Equal(count,loaded.Boxes.Count); var e=new PomodoroEngine(loaded.Pomodoro);
            Equal(PomodoroStatus.Paused,e.Session.Status); Equal(123d,e.Session.RemainingSeconds);
        } finally {if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }),
    ("skipping long break starts fresh cycle", () => {
        var (e,c)=Make();e.Session.CompletedInCycle=4;e.SwitchTo(PomodoroPhase.LongBreak);e.Skip();
        Equal(0,e.Session.CompletedInCycle);Equal(PomodoroPhase.Focus,e.Session.Phase);
    }),
    ("fractional ticks preserve elapsed time", () => {
        var (e,c)=Make();e.Start();for(var i=0;i<240;i++){c.Advance(.25);e.Tick();}
        Equal(1,e.Data.History.Count);Equal(60d,e.Data.History[0].FocusSeconds);
    })
};
tests = tests.Concat(StatisticsTests.Cases()).ToArray();
var failures=0;
foreach(var (name,run) in tests)
{
    try {run();Console.WriteLine("PASS "+name);}
    catch(Exception ex) {failures++;Console.WriteLine("FAIL "+name+": "+ex.Message);}
}
Console.WriteLine($"{tests.Length-failures}/{tests.Length} passed");
return failures==0?0:1;

static void Equal<T>(T expected,T actual)
{if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new Exception($"Expected {expected}, got {actual}");}
static (PomodoroEngine,FakeClock) Make()
{
    var clock=new FakeClock();return (new PomodoroEngine(new PomodoroData {Settings=new(){FocusMinutes=1,ShortBreakMinutes=1,LongBreakMinutes=1,AutoStartBreak=false,AutoStartFocus=false}},clock),clock);
}
static void Advance(PomodoroEngine engine,FakeClock clock,int seconds)
{for(var i=0;i<seconds;i++){clock.Advance(1);engine.Tick();}}
sealed class FakeClock : TimeProvider
{
    private long _ticks;private double _offset;
    public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    public override long GetTimestamp()=>_ticks;
    public override DateTimeOffset GetUtcNow()=>new DateTimeOffset(2026,10,17,8,0,0,TimeSpan.Zero).AddTicks(_ticks).AddSeconds(_offset);
    public void Advance(double seconds)=>_ticks+=(long)(seconds*TimeSpan.TicksPerSecond);
    public void ShiftWallClock(double seconds)=>_offset+=seconds;
}
