using System.IO;
using System.Windows.Threading;
using DeskFlow.Core.Services;

namespace DeskFlow.App.Services;

public sealed record DesktopRename(string OldPath, string NewPath);

public sealed class DesktopChangedEventArgs : EventArgs
{
    public DesktopChangedEventArgs(IReadOnlyList<string> paths, IReadOnlyList<DesktopRename> renames, bool fullRescan)
    {
        Paths = paths;
        Renames = renames;
        FullRescan = fullRescan;
    }
    public IReadOnlyList<DesktopRename> Renames { get; }
    public bool FullRescan { get; }

    /// <summary>本轮发生变化的条目路径（含重命名前的旧路径）。</summary>
    public IReadOnlyList<string> Paths { get; }
}

/// <summary>
/// 监听系统桌面目录变化，事件合并后触发一次归类，避免写入大文件时被反复打斷。
/// 变化路径会随事件一并给出，调用方可据此让图标缓存失效。
/// </summary>
public sealed class DesktopWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _changedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<DesktopRename> _renames = new();
    private bool _scheduled;
    private bool _fullRescan;
    private volatile bool _disposed;

    public DesktopWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _timer.Tick += OnTimerTick;
    }

    public event EventHandler<DesktopChangedEventArgs>? DesktopChanged;

    public void Start(IEnumerable<string>? directories = null)
    {
        foreach (var directory in directories ?? DesktopScanner.GetDesktopDirectories())
        {
            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };

                watcher.Created += (_, e) => Schedule(e.FullPath);
                watcher.Deleted += (_, e) => Schedule(e.FullPath);
                watcher.Changed += (_, e) => Schedule(e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    lock (_changedPaths)
                    {
                        _renames.Add(new DesktopRename(e.OldFullPath, e.FullPath));
                        _changedPaths.Add(e.OldFullPath);
                    }
                    Schedule(e.FullPath);
                };
                // Buffer overflow means individual events may have been lost.
                watcher.Error += (_, args) =>
                {
                    DiagnosticLog.Error("DesktopWatcher", "Events may have been lost; scheduling a full rescan", args.GetException());
                    Schedule(null);
                };
                watcher.EnableRaisingEvents = true;

                _watchers.Add(watcher);
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("DesktopWatcher", "Could not watch a desktop directory", error);
            }
        }
    }

    private void Schedule(string? path)
    {
        lock (_changedPaths)
        {
            if (_disposed) return;
            if (path is null) _fullRescan = true;
            else _changedPaths.Add(path);
            if (_scheduled) return;
            _scheduled = true;
        }
        // One dispatcher post per batch, with bounded latency during continuous writes.
        _dispatcher.BeginInvoke(() => { if (!_disposed) _timer.Start(); });
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();

        List<string> paths;
        List<DesktopRename> renames;
        bool fullRescan;
        lock (_changedPaths)
        {
            paths = _changedPaths.ToList();
            renames = _renames.ToList();
            fullRescan = _fullRescan;
            _changedPaths.Clear();
            _renames.Clear();
            _fullRescan = false;
            _scheduled = false;
        }

        if (!_disposed) DesktopChanged?.Invoke(this, new DesktopChangedEventArgs(paths, renames, fullRescan));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        _disposed = true;
    }
}
