using DeskFlow.Core.Models;
using DeskFlow.Core.Services;

static class StatisticsTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var today = new DateTime(2026, 10, 17);
        PomodoroRecord Record(int day, double seconds = 1500, bool complete = true, string? id = "task", string title = "写文档") => new()
        {
            StartedAt = new DateTimeOffset(2026,10,day,8,0,0,TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026,10,day,9,0,0,TimeSpan.Zero),
            FocusSeconds = seconds, Completed = complete, TaskId = id, TaskTitle = title
        };
        PomodoroStatisticsResult Calc(IEnumerable<PomodoroRecord> records, int start = 11, int end = 17) =>
            PomodoroStatistics.Calculate(records, new DateTime(2026,10,start), new DateTime(2026,10,end), today, 8, TimeZoneInfo.Utc);
        yield return ("statistics: empty dates are zero-filled", () => {
            var s = Calc(Array.Empty<PomodoroRecord>());
            Equal(7,s.Days.Count); Equal(0,s.Completed); Equal(0d,s.FocusSeconds); Equal(null,s.CompletionRate);
            Equal(0d,s.AverageDailySeconds); Equal(0,s.CurrentStreak); Equal(0,s.Tasks.Count);
        });
        yield return ("statistics: completed and interrupted minutes and denominator", () => {
            var s=Calc(new[]{Record(11),Record(17,300,false),Record(10),Record(18)});
            Equal(1,s.Completed); Equal(1,s.Interrupted); Equal(1800d,s.FocusSeconds);
            Equal(0.5,s.CompletionRate!.Value); Equal(2,s.ActiveDays); Equal(1800d/7,s.AverageDailySeconds);
            Equal(1500d,s.Days[0].CompletedSeconds); Equal(300d,s.Days[6].InterruptedSeconds);
        });
        yield return ("statistics: local midnight uses end date", () => {
            var zone=TimeZoneInfo.CreateCustomTimeZone("UTC+8",TimeSpan.FromHours(8),"UTC+8","UTC+8");
            var r=Record(16);r.EndedAt=new DateTimeOffset(2026,10,16,16,0,0,TimeSpan.Zero);
            var s=PomodoroStatistics.Calculate(new[]{r},today,today,today,8,zone);
            Equal(1,s.Completed); Equal(today,s.Days[0].Date);
            Equal(0,Calc(new[]{r},17,17).Completed);
        });
        yield return ("statistics: renamed task groups by ID with latest title", () => {
            var s=Calc(new[]{Record(11,title:"旧名"),Record(17,title:"新名"),Record(16,id:"other",title:"新名")});
            Equal(2,s.Tasks.Count);Equal("新名",s.Tasks[0].Title);Equal(2,s.Tasks[0].Completed);
            Equal(2d/3,s.Tasks[0].Share);
        });
        yield return ("statistics: free focus names and linked IDs do not collide", () => {
            var s=Calc(new[]{Record(11,id:null,title:"阅读"),Record(12,id:null,title:"阅读"),Record(13,id:null,title:"写作"),Record(14,id:"阅读",title:"阅读")});
            Equal(3,s.Tasks.Count);Equal(2,s.Tasks[0].Completed);Equal(1d,s.Tasks.Sum(t=>t.Share));
            Equal(s.FocusSeconds,s.Tasks.Sum(t=>t.FocusSeconds));
        });
        yield return ("statistics: yesterday streak survives unfinished today", () => {
            var s=Calc(new[]{Record(14),Record(15),Record(16),Record(17,100,false)});
            Equal(3,s.CurrentStreak);Equal(3,s.LongestStreak);Equal(0,s.TodayCompleted);
        });
        yield return ("statistics: streak gap and duplicate same-day completion", () => {
            var s=Calc(new[]{Record(11),Record(12),Record(13),Record(16),Record(17),Record(17)});
            Equal(2,s.CurrentStreak);Equal(3,s.LongestStreak);Equal(2,s.TodayCompleted);
            Equal(0,Calc(new[]{Record(11),Record(12)}).CurrentStreak);
        });
        yield return ("statistics: today target independent of selected past range", () => {
            var s=Calc(new[]{Record(11),Record(17),Record(17)},11,12);
            Equal(1,s.Completed);Equal(2,s.TodayCompleted);Equal(8,s.DailyGoal);
        });
        yield return ("statistics: validate inverted, future and excessive ranges", () => {
            foreach(var (start,end) in new[]{(today,today.AddDays(-1)),(today,today.AddDays(1)),(today.AddDays(-366),today)})
            {
                var failed=false;try{PomodoroStatistics.Calculate(Array.Empty<PomodoroRecord>(),start,end,today,8);}catch(ArgumentException){failed=true;}
                Equal(true,failed);
            }
            var max=PomodoroStatistics.Calculate(Array.Empty<PomodoroRecord>(),today.AddDays(-365),today,today,8);
            Equal(366,max.Days.Count);
        });
        yield return ("statistics: one-day range includes both day boundaries", () => {
            var a=Record(17);a.EndedAt=new DateTimeOffset(2026,10,17,0,0,0,TimeSpan.Zero);
            var b=Record(17);b.EndedAt=new DateTimeOffset(2026,10,17,23,59,59,TimeSpan.Zero);
            Equal(2,Calc(new[]{a,b},17,17).Completed);
        });
        yield return ("statistics: CSV protects formulas, quotes and exact minutes", () => {
            var s=Calc(new[]{Record(17,90,false,id:null,title:"=SUM(1,2)\n\"注释\"")});
            var csv=PomodoroStatistics.ToCsv(s);
            Equal(true,csv.Contains("\"'=SUM(1,2)\n\"\"注释\"\"\""));
            Equal(true,csv.Contains(",0,1,1.5,0,1.5"));
            Equal(true,csv.Contains("2026-10-11 至 2026-10-17"));
        });
        yield return ("statistics: invalid numeric records do not poison sums", () => {
            var s=Calc(new[]{Record(11,double.NaN),Record(12,double.PositiveInfinity),Record(13,-1),Record(17,60)});
            Equal(60d,s.FocusSeconds);Equal(1,s.Completed);
        });
    }
    private static void Equal<T>(T expected,T actual)
    {if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new Exception($"Expected {expected}, got {actual}");}
}
