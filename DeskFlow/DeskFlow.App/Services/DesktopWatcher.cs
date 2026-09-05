using System.IO;
using System.Windows.Threading;
using DeskFlow.Core.Services;

namespace DeskFlow.App.Services;

public sealed class DesktopChangedEventArgs : EventArgs
{
    public DesktopChangedEventArgs(IReadOnlyList<string> paths) => Paths = paths;

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
    private bool _disposed;

    public DesktopWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _timer.Tick += OnTimerTick;
    }

    public event EventHandler<DesktopChangedEventArgs>? DesktopChanged;

    public void Start()
    {
        foreach (var directory in DesktopScanner.GetDesktopDirectories())
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
                    Schedule(e.OldFullPath);
                    Schedule(e.FullPath);
                };
                watcher.EnableRaisingEvents = true;

                _watchers.Add(watcher);
            }
            catch
            {
                // 目录不可监听时跳过（例如部分网络位置）
            }
        }
    }

    private void Schedule(string path)
    {
        lock (_changedPaths)
        {
            _changedPaths.Add(path);
        }

        _timer.Stop();
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();

        List<string> paths;
        lock (_changedPaths)
        {
            paths = _changedPaths.ToList();
            _changedPaths.Clear();
        }

        DesktopChanged?.Invoke(this, new DesktopChangedEventArgs(paths));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

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
