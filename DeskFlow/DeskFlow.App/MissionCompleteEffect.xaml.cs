using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using DeskFlow.Core.Services;
using DeskFlow.Shell;

namespace DeskFlow.App;

/// <summary>短暂显示的无焦点、鼠标穿透的任务完成提示。</summary>
public partial class MissionCompleteEffect : Window
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private static MissionCompleteEffect? _current;

    private MissionCompleteEffect(Window anchor)
    {
        InitializeComponent();
        var area = ScreenHelper.FromWindow(anchor).WorkArea;
        Left = Math.Clamp(anchor.Left + anchor.Width / 2 - Width / 2,
            area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(anchor.Top + Math.Min(anchor.Height / 2, 90) - Height / 2,
            area.Top, Math.Max(area.Top, area.Bottom - Height));

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                new IntPtr(style | NativeMethods.WS_EX_TOOLWINDOW | WsExTransparent | WsExNoActivate));
        };
        Loaded += (_, _) =>
        {
            var storyboard = (Storyboard)Resources["CelebrateStoryboard"];
            storyboard.Completed += (_, _) => Close();
            storyboard.Begin(this);
        };
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this)) _current = null;
        };
    }

    public static void Play(Window anchor)
    {
        TodoSound.PlayDone();
        try
        {
            _current?.Close();
            var effect = new MissionCompleteEffect(anchor);
            _current = effect;
            effect.Show();
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("TodoEffect", "Could not show completion effect", error);
        }
    }
}
