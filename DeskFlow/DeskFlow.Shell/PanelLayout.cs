using System.Windows;
using Size = System.Windows.Size;

namespace DeskFlow.Shell;

[Flags]
public enum ResizeEdges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

/// <summary>Geometry shared by file boxes and todo panels, in WPF layout units.</summary>
public static class PanelLayout
{
    // Already visible layouts are fixed points: no implicit 4/12px margins.
    public static Rect FitToArea(Rect rect, Rect area)
    {
        var width = Math.Min(rect.Width, area.Width);
        var height = Math.Min(rect.Height, area.Height);
        return new Rect(Math.Clamp(rect.Left, area.Left, area.Right - width),
            Math.Clamp(rect.Top, area.Top, area.Bottom - height), width, height);
    }

    /// <summary>Grow downward from the user-selected top; overflow scrolls instead of moving the panel.</summary>
    public static Rect AutoHeight(Rect rect, double requestedHeight, double minimumHeight, Rect area)
    {
        var minimum = Math.Min(minimumHeight, area.Height);
        var top = Math.Clamp(rect.Top, area.Top, area.Bottom - minimum);
        var height = Math.Clamp(requestedHeight, minimum, area.Bottom - top);
        return new Rect(rect.Left, top, rect.Width, height);
    }

    public static Rect ResizeFromStart(Rect start, Vector delta, ResizeEdges edges, Size minimum, Size maximum)
    {
        var left = start.Left;
        var top = start.Top;
        var width = start.Width;
        var height = start.Height;
        if ((edges & (ResizeEdges.Left | ResizeEdges.Right)) != 0)
        {
            width = Math.Clamp(start.Width + (edges.HasFlag(ResizeEdges.Left) ? -delta.X : delta.X), minimum.Width, maximum.Width);
            if (edges.HasFlag(ResizeEdges.Left)) left = start.Right - width;
        }
        if ((edges & (ResizeEdges.Top | ResizeEdges.Bottom)) != 0)
        {
            height = Math.Clamp(start.Height + (edges.HasFlag(ResizeEdges.Top) ? -delta.Y : delta.Y), minimum.Height, maximum.Height);
            if (edges.HasFlag(ResizeEdges.Top)) top = start.Bottom - height;
        }
        return new Rect(left, top, width, height);
    }
}

/// <summary>One gesture's snap state. Release uses the unsnapped cursor position, so small moves accumulate.</summary>
public sealed class PanelSnapSession
{
    public const double Threshold = 10;
    public const double ReleaseThreshold = 18;
    private double? _x;
    private double? _y;

    public void Reset() { _x = null; _y = null; }

    public Rect Move(Rect raw, IEnumerable<Rect> peers, Rect area, bool snapPanels, bool snapScreen)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        if (snapPanels)
        {
            foreach (var peer in peers)
            {
                xs.AddRange(new[] { peer.Left, peer.Right - raw.Width, peer.Right, peer.Left - raw.Width,
                    peer.Left + (peer.Width - raw.Width) / 2 });
                ys.AddRange(new[] { peer.Top, peer.Bottom - raw.Height, peer.Bottom, peer.Top - raw.Height,
                    peer.Top + (peer.Height - raw.Height) / 2 });
            }
        }
        if (snapScreen)
        {
            xs.AddRange(new[] { area.Left, area.Right - raw.Width });
            ys.AddRange(new[] { area.Top, area.Bottom - raw.Height });
        }
        return new Rect(Snap(raw.Left, xs, ref _x), Snap(raw.Top, ys, ref _y), raw.Width, raw.Height);
    }

    public Rect Resize(Rect raw, ResizeEdges edges, IEnumerable<Rect> peers, Rect area,
        bool snapPanels, bool snapScreen, Size minimum, Size maximum)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        if (snapPanels)
            foreach (var peer in peers) { xs.AddRange(new[] { peer.Left, peer.Right }); ys.AddRange(new[] { peer.Top, peer.Bottom }); }
        if (snapScreen) { xs.AddRange(new[] { area.Left, area.Right }); ys.AddRange(new[] { area.Top, area.Bottom }); }

        var left = raw.Left;
        var top = raw.Top;
        var width = raw.Width;
        var height = raw.Height;
        if (edges.HasFlag(ResizeEdges.Left))
        {
            width = Math.Clamp(raw.Right - Snap(raw.Left, xs, ref _x), minimum.Width, maximum.Width);
            left = raw.Right - width;
        }
        else if (edges.HasFlag(ResizeEdges.Right))
            width = Math.Clamp(Snap(raw.Right, xs, ref _x) - raw.Left, minimum.Width, maximum.Width);
        if (edges.HasFlag(ResizeEdges.Top))
        {
            height = Math.Clamp(raw.Bottom - Snap(raw.Top, ys, ref _y), minimum.Height, maximum.Height);
            top = raw.Bottom - height;
        }
        else if (edges.HasFlag(ResizeEdges.Bottom))
            height = Math.Clamp(Snap(raw.Bottom, ys, ref _y) - raw.Top, minimum.Height, maximum.Height);
        return new Rect(left, top, width, height);
    }

    private static double Snap(double raw, List<double> candidates, ref double? held)
    {
        if (held is double previous && candidates.Contains(previous) && Math.Abs(raw - previous) <= ReleaseThreshold)
            return previous;
        held = null;
        var nearest = candidates.Distinct().OrderBy(v => Math.Abs(v - raw)).ThenBy(v => v).FirstOrDefault(double.NaN);
        if (double.IsFinite(nearest) && Math.Abs(raw - nearest) <= Threshold) held = nearest;
        return held ?? raw;
    }
}
