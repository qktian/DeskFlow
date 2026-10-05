using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using DeskFlow.Core.Services;
using DeskFlow.Shell;

internal static partial class Program
{
    private static Process Fixture(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static void Reliability()
    {
        Check("Rolling logs rotate within size and count limits", () =>
        {
            var dir = Path.Combine(Root, "rotation");
            var log = new RollingLog(dir, 1024, 2);
            for (var i = 0; i < 100; i++) log.Write("INFO", "test", new string('x', 100));
            var files = Directory.GetFiles(dir);
            Require(files.Length == 3 && files.All(p => new FileInfo(p).Length <= 1024));
        });
        Check("Concurrent log writes preserve complete entries", () =>
        {
            var dir = Path.Combine(Root, "concurrent-log");
            var log = new RollingLog(dir);
            Parallel.For(0, 60, i => log.Write("INFO", "concurrent", $"entry-{i}"));
            Require(File.ReadAllLines(Path.Combine(dir, "deskflow.log")).Length == 60);
        });
        Check("Unavailable log directory cannot break cleanup", () =>
        {
            var file = Path.Combine(Root, "not-a-directory");
            File.WriteAllText(file, "blocked");
            new RollingLog(file).Write("ERROR", "test", "safe");
            var completed = false;
            ReliableCleanup.Run(("failed dispose", () => throw new IOException("fixture")), ("restore", () => completed = true));
            Require(completed);
        });
        Check("Invalid and ambiguous hotkey input is rejected", () =>
        {
            Require(HotkeyParser.Parse("Ctrl+A+B") is null);
            Require(HotkeyParser.Parse("Ctrl++A") is null);
        });
        Check("Hotkey conflicts preserve registrations and roll back staged keys", () =>
        {
            using var blocker = new GlobalHotkeyService();
            using var subject = new GlobalHotkeyService();
            using var probe = new GlobalHotkeyService();
            var free = new List<string>();
            foreach (var key in Enumerable.Range(13, 12))
            {
                var text = $"Ctrl+Alt+Shift+F{key}";
                if (blocker.Register(key, text)) free.Add(text);
                if (free.Count == 3) break;
            }
            Require(free.Count == 3, "Could not reserve three test combinations");
            blocker.Dispose();
            using var occupied = new GlobalHotkeyService();
            Require(occupied.Register(1, free[2]));
            Require(subject.Register(1, free[0]));
            Require(!subject.TryApply(new Dictionary<int, string> { [1] = free[1], [2] = free[2] }, out var error));
            Require(error.Contains("占用") && subject.Active[1] == HotkeyParser.Parse(free[0]));
            Require(!probe.Register(1, free[0]), "Original hotkey was lost");
            Require(probe.Register(1, free[1]), "Staged hotkey leaked after failure");
        });
        Check("Hotkeys can swap actions, disable, and reject duplicate or reserved keys", () =>
        {
            using var hotkeys = new GlobalHotkeyService();
            var free = new List<string>();
            foreach (var key in Enumerable.Range(13, 12))
            {
                var text = $"Ctrl+Alt+Shift+F{key}";
                if (hotkeys.Register(free.Count + 1, text)) free.Add(text);
                if (free.Count == 2) break;
            }
            Require(free.Count == 2);
            Require(hotkeys.TryApply(new Dictionary<int, string> { [1] = free[1], [2] = free[0] }, out _));
            Require(hotkeys.Active[1] == HotkeyParser.Parse(free[1]));
            Require(!hotkeys.TryApply(new Dictionary<int, string> { [1] = free[1], [2] = free[1] }, out _));
            Require(!hotkeys.Register(1, "Ctrl+F12") && hotkeys.Active.Count == 2);
            Require(hotkeys.TryApply(new Dictionary<int, string> { [1] = "", [2] = " " }, out _) && hotkeys.Active.Count == 0);
        });
        Check("Shell message timeout bounds an unresponsive foreign UI thread", () =>
        {
            using var sink = new TestSinkHost();
            Require(BoundedWindowMessage.TrySend(sink.Handle, 0x8002, IntPtr.Zero, out _));
            var clock = Stopwatch.StartNew();
            Require(!BoundedWindowMessage.TrySend(sink.Handle, 0x8001, IntPtr.Zero, out _, 100));
            Require(clock.ElapsedMilliseconds < 1000, $"Message blocked for {clock.ElapsedMilliseconds}ms");
            Console.WriteLine($"  Hung-window call returned after {clock.ElapsedMilliseconds} ms");
        });
        Check("Destroyed window fails promptly and replacement accepts messages", () =>
        {
            var sink = new TestSinkHost();
            var old = sink.Handle;
            sink.Dispose();
            Require(!BoundedWindowMessage.TrySend(old, 0x8002, IntPtr.Zero, out _));
            using var replacement = new TestSinkHost();
            Require(BoundedWindowMessage.TrySend(replacement.Handle, 0x8002, IntPtr.Zero, out _));
        });
        Check("Old guardian cannot restore a newer session; failures retain lease", () =>
        {
            var store = new RecoveryStore(Path.Combine(Root, "leases"));
            using var owner = Process.GetCurrentProcess();
            store.Arm(new RecoveryLease("new", owner.Id, owner.StartTime.ToUniversalTime().Ticks));
            var called = false;
            Require(store.Recover("old", () => { called = true; return true; }) && !called);
            Require(!store.Recover("new", () => false) && store.IsCurrent("new"));
            Require(store.Recover("new", () => true) && store.GetLease() is null);
        });
        Check("PID reuse does not prevent recovery; Explorer-not-ready retries succeed", () =>
        {
            var store = new RecoveryStore(Path.Combine(Root, "reused-pid"));
            store.Arm(new RecoveryLease("mismatch", Environment.ProcessId, 0));
            var attempts = 0;
            Require(DesktopRecovery.Watch(store, "mismatch", () => ++attempts >= 3) == 0);
            Require(attempts == 3 && store.GetLease() is null);
        });
        Check("Unavailable Explorer exhausts bounded retries and retains recovery record", () =>
        {
            var store = new RecoveryStore(Path.Combine(Root, "unavailable-explorer"));
            store.Arm(new RecoveryLease("unavailable", Environment.ProcessId, 0));
            var attempts = 0;
            var clock = Stopwatch.StartNew();
            Require(DesktopRecovery.Watch(store, "unavailable", () => { attempts++; return false; }) == 1);
            Require(attempts == 20 && clock.Elapsed < TimeSpan.FromSeconds(15) && store.IsCurrent("unavailable"));
        });
        foreach (var crash in new[] { false, true }) Check(crash
            ? "Independent guardian restores after fixture owner is killed"
            : "Clean restoration stops guardian while owner remains alive", () =>
        {
            var dir = Path.Combine(Root, crash ? "crashed-owner" : "clean-owner");
            Directory.CreateDirectory(dir);
            var store = new RecoveryStore(dir);
            using var owner = Fixture("--fixture-owner");
            Process? guardian = null;
            try
            {
                var token = Guid.NewGuid().ToString("N");
                store.Arm(new RecoveryLease(token, owner.Id, owner.StartTime.ToUniversalTime().Ticks));
                guardian = Fixture("--fixture-guardian", dir, token);
                Require(SpinWait.SpinUntil(() => File.Exists(Path.Combine(dir, "ready")), 5000), "Guardian did not become ready");
                if (crash) { owner.Kill(); Require(owner.WaitForExit(3000)); }
                else Require(store.Recover(token, () => true));
                Require(guardian.WaitForExit(5000) && guardian.ExitCode == 0);
                Require(File.Exists(Path.Combine(dir, "restored")) == crash);
                Require(store.GetLease() is null);
                if (!crash) Require(!owner.HasExited);
            }
            finally
            {
                if (!owner.HasExited) { owner.Kill(); owner.WaitForExit(3000); }
                if (guardian is { HasExited: false }) { guardian.Kill(); guardian.WaitForExit(3000); }
                guardian?.Dispose();
            }
        });
    }

    private sealed class TestSinkHost : IDisposable
    {
        private readonly Thread _thread;
        private Dispatcher _dispatcher = null!;
        private Sink _sink = null!;
        public IntPtr Handle { get; private set; }
        public TestSinkHost()
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _sink = new Sink();
                Handle = _sink.Handle;
                ready.Set();
                Dispatcher.Run();
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Require(ready.Wait(3000));
        }
        public void Dispose()
        {
            _dispatcher.BeginInvoke(() => { _sink.DestroyHandle(); _dispatcher.InvokeShutdown(); });
            Require(_thread.Join(4000), "Test window did not shut down");
        }
        private sealed class Sink : System.Windows.Forms.NativeWindow
        {
            public Sink() => CreateHandle(new System.Windows.Forms.CreateParams { Caption = "DeskFlow isolated timeout fixture" });
            protected override void WndProc(ref System.Windows.Forms.Message message)
            {
                if (message.Msg == 0x8001) { Thread.Sleep(1800); message.Result = (IntPtr)1; return; }
                if (message.Msg == 0x8002) { message.Result = (IntPtr)1; return; }
                base.WndProc(ref message);
            }
        }
    }
}
