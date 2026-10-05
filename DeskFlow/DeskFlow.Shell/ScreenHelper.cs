using System.Runtime.InteropServices;
using System.Windows;
using Point = System.Windows.Point;

namespace DeskFlow.Shell;

public sealed record MonitorArea(string DeviceName, Rect WorkArea, Rect Bounds, bool IsPrimary, double ScaleX = 1, double ScaleY = 1)
{
    public string Id => DeviceName;

    public Point ToDesktop(Point local) => new(WorkArea.Left + local.X, WorkArea.Top + local.Y);
    public Point ToLocal(Point desktop) => new(desktop.X - WorkArea.Left, desktop.Y - WorkArea.Top);
}

/// <summary>多显示器与 DPI 换算：把像素工作区转换为 WPF 使用的设备无关像素。</summary>
public static class ScreenHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativeMethods.POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public static IReadOnlyList<MonitorArea> GetMonitors()
    {
        var result = new List<MonitorArea>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var scale = GetScale(
                screen.Bounds.Left + screen.Bounds.Width / 2,
                screen.Bounds.Top + screen.Bounds.Height / 2);

            result.Add(new MonitorArea(
                screen.DeviceName,
                ToDip(screen.WorkingArea, scale),
                ToDip(screen.Bounds, scale),
                screen.Primary, scale, scale));
        }

        return result.Count == 0
            ? new[] { new MonitorArea("PRIMARY", SystemParameters.WorkArea, SystemParameters.WorkArea, true) }
            : result;
    }

    public static MonitorArea Primary =>
        GetMonitors().FirstOrDefault(m => m.IsPrimary) ?? GetMonitors()[0];

    public static MonitorArea FromPoint(System.Windows.Point dipPoint)
    {
        var monitors = GetMonitors();
        foreach (var monitor in monitors)
        {
            if (monitor.WorkArea.Contains(dipPoint) || monitor.Bounds.Contains(dipPoint))
            {
                return monitor;
            }
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public static MonitorArea ForId(string? id)
    {
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public static MonitorArea FromWindow(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            var screen = System.Windows.Forms.Screen.FromHandle(handle);
            var monitor = GetMonitors().FirstOrDefault(m => string.Equals(m.Id, screen.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (monitor is not null) return monitor;
        }
        return FromPoint(new Point(window.Left + window.Width / 2, window.Top + window.Height / 2));
    }

    /// <summary>把盒子限制在屏幕可视范围内，避免显示器拔插后盒子丢失在虚拟屏外。</summary>
    public static MonitorArea SelectMonitor(Rect rect, IReadOnlyList<MonitorArea> monitors, string? preferredId = null)
    {
        if (monitors.Count == 0) throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        var best = monitors.Select(m => (Monitor: m, Overlap: Rect.Intersect(rect, m.WorkArea)))
            .OrderByDescending(entry => entry.Overlap.IsEmpty ? 0 : entry.Overlap.Width * entry.Overlap.Height)
            .ThenByDescending(entry => entry.Monitor.Id == preferredId).First();
        if (!best.Overlap.IsEmpty && best.Overlap.Width > 0 && best.Overlap.Height > 0) return best.Monitor;
        return monitors.FirstOrDefault(m => m.Id == preferredId) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    /// <summary>只调整越界部分；已经完整可见的布局保持原样。</summary>
    public static Rect ClampToMonitor(Rect rect, MonitorArea monitor)
    {
        return PanelLayout.FitToArea(rect, monitor.WorkArea);
    }

    /// <summary>Convert pre-v2 saved desktop coordinates into offsets local to each monitor.</summary>
    public static void MigrateLegacyCoordinates(DeskFlow.Core.Models.AppConfig config)
    {
        if (config.Version >= 2) return;
        var monitors = GetMonitors();
        foreach (var box in config.Boxes)
        {
            var old = new Rect(box.X, box.Y, box.Width, box.Collapsed ? 30 : box.Height);
            var monitor = monitors.FirstOrDefault(m => m.Id == box.ScreenId) ?? SelectMonitor(old, monitors, config.General.PreferredMonitorId);
            var recovered = string.IsNullOrWhiteSpace(box.ScreenId) || !monitors.Any(m => m.Id == box.ScreenId)
                ? ClampToMonitor(old, monitor) : old;
            var local = monitor.ToLocal(new Point(recovered.Left, recovered.Top));
            box.X = Math.Max(0, local.X);
            box.Y = Math.Max(0, local.Y);
            box.ScreenId = monitor.Id;
        }
        foreach (var board in config.TodoBoards)
        {
            var old = new Rect(board.X, board.Y, board.Width, board.Collapsed ? 32 : board.Height);
            var monitor = monitors.FirstOrDefault(m => m.Id == board.ScreenId) ?? SelectMonitor(old, monitors, config.General.PreferredMonitorId);
            var recovered = string.IsNullOrWhiteSpace(board.ScreenId) || !monitors.Any(m => m.Id == board.ScreenId)
                ? ClampToMonitor(old, monitor) : old;
            var local = monitor.ToLocal(new Point(recovered.Left, recovered.Top));
            board.X = Math.Max(0, local.X);
            board.Y = Math.Max(0, local.Y);
            board.ScreenId = monitor.Id;
        }
        config.Version = 2;
    }

    private static Rect ToDip(System.Drawing.Rectangle rect, double scale)
    {
        return new Rect(rect.Left / scale, rect.Top / scale, rect.Width / scale, rect.Height / scale);
    }

    private static double GetScale(int pixelX, int pixelY)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativeMethods.POINT { X = pixelX, Y = pixelY }, 1);
            if (monitor == IntPtr.Zero)
            {
                return 1.0;
            }

            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch
        {
            // 回退到 100% 缩放
        }

        return 1.0;
    }
}
