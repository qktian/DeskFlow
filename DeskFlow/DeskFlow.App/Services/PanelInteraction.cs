using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskFlow.Core.Models;
using DeskFlow.Shell;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace DeskFlow.App.Services;

/// <summary>Uses the pointer's absolute displacement from gesture start, never previously snapped geometry.</summary>
internal sealed class PanelInteraction(Window window, Func<GeneralSettings> settings)
{
    private readonly PanelSnapSession _snap = new();
    private Rect _start;
    private Point _pointer;
    private DpiScale _dpi;
    public bool Moving { get; private set; }
    public bool Resizing { get; private set; }
    public bool Active => Moving || Resizing;

    public static bool IsTitleButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private Rect Bounds => new(window.Left, window.Top, window.Width, window.Height);
    private Point Pointer => window.PointToScreen(Mouse.GetPosition(window));
    private Vector Delta
    {
        get
        {
            var pixels = Pointer - _pointer;
            return new Vector(pixels.X / _dpi.DpiScaleX, pixels.Y / _dpi.DpiScaleY);
        }
    }

    public void Begin(bool resize)
    {
        _start = Bounds;
        _pointer = Pointer;
        _dpi = VisualTreeHelper.GetDpi(window);
        _snap.Reset();
        Moving = !resize;
        Resizing = resize;
    }

    private IEnumerable<Rect> Peers(Rect area) => Application.Current.Windows.Cast<Window>()
        .Where(w => !ReferenceEquals(w, window) && w.IsVisible && (w is BoxWindow or TodoBoardWindow))
        .Select(w => new Rect(w.Left, w.Top, w.Width, w.Height)).Where(r => r.IntersectsWith(area));

    public Rect Move()
    {
        var delta = Delta;
        var raw = new Rect(_start.Left + delta.X, _start.Top + delta.Y, window.Width, window.Height);
        var area = ScreenHelper.FromWindow(window).WorkArea;
        var general = settings();
        return _snap.Move(raw, Peers(area), area, general.SnapBoxes, general.SnapToScreenEdge);
    }

    public Rect Resize(ResizeEdges edges, Size minimum, Size maximum)
    {
        var raw = PanelLayout.ResizeFromStart(_start, Delta, edges, minimum, maximum);
        var area = ScreenHelper.FromWindow(window).WorkArea;
        var general = settings();
        return _snap.Resize(raw, edges, Peers(area), area, general.SnapBoxes, general.SnapToScreenEdge, minimum, maximum);
    }

    public void End() { Moving = false; Resizing = false; _snap.Reset(); }
}
