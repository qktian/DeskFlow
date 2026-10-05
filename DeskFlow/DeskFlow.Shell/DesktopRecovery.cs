using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskFlow.Core.Services;

namespace DeskFlow.Shell;

public sealed record RecoveryLease(string Token, int ProcessId, long StartTicks);

/// <summary>Serialize recovery with a new owner's lease so an old guardian cannot undo a new session.</summary>
public sealed class RecoveryStore(string directory)
{
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "desktop-recovery.json");
    private readonly string _mutexName = "Local\\DeskFlow.Recovery." + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())))[..24];

    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(1000); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new TimeoutException("Desktop recovery lease is busy.");
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    private RecoveryLease? Read() => File.Exists(_path)
        ? JsonSerializer.Deserialize<RecoveryLease>(File.ReadAllText(_path)) : null;

    public void Arm(RecoveryLease lease) => Locked(() =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, lease);
            stream.Flush(true);
        }
        File.Move(temporary, _path, true);
        return true;
    });

    public bool IsCurrent(string token) => Locked(() => Read()?.Token == token);

    // Caller must own the lease, or have observed its owner's exit before invoking recovery.
    public bool Recover(string token, Func<bool> restore) => Locked(() =>
    {
        if (Read()?.Token != token) return true;
        if (!restore()) return false;
        File.Delete(_path);
        return true;
    });

    public RecoveryLease? GetLease() => Locked(Read);
}

public sealed class DesktopRecovery : IDisposable
{
    private readonly RecoveryStore _store = new(ConfigService.ConfigDirectory);
    private RecoveryLease? _lease;
    private Process? _guardian;
    public const string WatchArgument = "--watch-owner";

    public bool EnsureArmed()
    {
        try
        {
            if (_lease is not null && _guardian is { HasExited: false } && _store.IsCurrent(_lease.Token)) return true;
            _guardian?.Dispose();
            using var owner = Process.GetCurrentProcess();
            _lease = new RecoveryLease(Guid.NewGuid().ToString("N"), owner.Id, owner.StartTime.ToUniversalTime().Ticks);
            _store.Arm(_lease);
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyName(_lease.Token));
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add(WatchArgument);
            start.ArgumentList.Add(_lease.Token);
            _guardian = Process.Start(start);
            if (_guardian is null || !ready.WaitOne(2500) || _guardian.HasExited)
                throw new IOException("Desktop recovery guardian did not become ready.");
            DiagnosticLog.Info("Recovery", "Guardian ready before hiding desktop icons.");
            return true;
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("Recovery", "Cannot arm guardian; desktop icons will remain visible.", error);
            Restore();
            return false;
        }
    }

    public bool Restore()
    {
        try
        {
            var lease = _lease ?? _store.GetLease();
            return lease is null ? DesktopIconService.SetIconsVisibleWithRetry(true)
                : _store.Recover(lease.Token, () => DesktopIconService.SetIconsVisibleWithRetry(true));
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("Recovery", "Lease unavailable; attempting direct restore", error);
            return DesktopIconService.SetIconsVisibleWithRetry(true);
        }
    }

    public static string ReadyName(string token) => "Local\\DeskFlow.Recovery.Ready." + token;

    // Shared by the production helper and the isolated regression fixture.
    public static int Watch(RecoveryStore store, string token, Func<bool> restore, Action? ready = null)
    {
        try
        {
            var lease = store.GetLease();
            if (lease?.Token != token) return 0;
            Process? owner = null;
            try
            {
                owner = Process.GetProcessById(lease.ProcessId);
                if (owner.StartTime.ToUniversalTime().Ticks != lease.StartTicks)
                { owner.Dispose(); owner = null; }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { owner?.Dispose(); owner = null; }
            using (owner)
            {
                try { ready?.Invoke(); }
                catch (WaitHandleCannotBeOpenedException) { /* Owner may already have exited; still recover. */ }
                if (owner is not null)
                    while (!owner.WaitForExit(500))
                        if (!store.IsCurrent(token)) return 0;
            }
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (store.Recover(token, restore))
                {
                    DiagnosticLog.Info("Recovery", "Owner exited; desktop recovery completed or lease superseded.");
                    return 0;
                }
                Thread.Sleep(500);
            }
            DiagnosticLog.Error("Recovery", "Explorer unavailable; recovery lease retained. Run --recover-desktop after Explorer is ready.");
            return 1;
        }
        catch (Exception error) { DiagnosticLog.Error("Recovery", "Guardian failed; use --recover-desktop.", error); return 1; }
    }

    public static int RunGuardian(string token) => Watch(new RecoveryStore(ConfigService.ConfigDirectory), token,
        () => DesktopIconService.TrySetIconsVisible(true), () =>
        {
            using var signal = EventWaitHandle.OpenExisting(ReadyName(token));
            signal.Set();
        });

    public void Dispose() => _guardian?.Dispose();
}
