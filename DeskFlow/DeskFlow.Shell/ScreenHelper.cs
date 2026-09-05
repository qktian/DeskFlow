using System.Runtime.InteropServices;
using System.Windows;

namespace DeskFlow.Shell;

public sealed record MonitorArea(string DeviceName, Rect WorkArea, Rect Bounds, bool IsPrimary)
{
    public string Id => DeviceName;
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
                screen.Primary));
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

    /// <summary>把盒子限制在屏幕可视范围内，避免显示器拔插后盒子丢失在虚拟屏外。</summary>
    public static Rect ClampToMonitor(Rect rect, MonitorArea monitor)
    {
        var area = monitor.WorkArea;
        var width = Math.Min(rect.Width, Math.Max(160, area.Width - 16));
        var height = Math.Min(rect.Height, Math.Max(120, area.Height - 16));
        var x = Math.Clamp(rect.X, area.Left + 4, Math.Max(area.Left + 4, area.Right - width - 4));
        var y = Math.Clamp(rect.Y, area.Top + 4, Math.Max(area.Top + 4, area.Bottom - height - 4));
        return new Rect(x, y, width, height);
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
