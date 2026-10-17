using System.Windows;
using System.Windows.Threading;
using DeskFlow.App;

internal static partial class Program
{
    private static void TodoEffect()
    {
        Check("Todo: completion effect appears and closes after animation", () =>
        {
            var anchor = new Window
            {
                Width = 300, Height = 220, Left = 40, Top = 40,
                Opacity = 0, ShowActivated = false, ShowInTaskbar = false, IsHitTestVisible = false
            };
            try
            {
                anchor.Show();
                MissionCompleteEffect.Play(anchor);
                Require(Application.Current.Windows.OfType<MissionCompleteEffect>().Count() == 1,
                    "Completion overlay did not appear");

                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
                Require(!Application.Current.Windows.OfType<MissionCompleteEffect>().Any(),
                    "Completion overlay did not close");
            }
            finally { anchor.Close(); }
        });
    }
}
