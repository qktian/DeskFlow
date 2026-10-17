using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeskFlow.App;
using DeskFlow.App.Services;
using DeskFlow.Core.Models;

internal static partial class Program
{
    private static void BoxLayout()
    {
        var directory = Path.Combine(Root, "box-layout");
        Directory.CreateDirectory(directory);
        var paths = Enumerable.Range(0, 19).Select(i => Path.Combine(directory, $"item-{i:D2}.txt")).ToList();
        foreach (var path in paths) File.WriteAllText(path, "layout fixture");
        var model = new BoxModel { Name = "Layout fixture", Width = 480, Height = 700, Items = paths };
        var config = new AppConfig { Boxes = new() { model } };
        config.General.IconSize = 32;
        var manager = new BoxManager(config, _ => true);
        using var icons = manager.Icons;
        var window = new BoxWindow(model, manager)
        {
            Opacity = 0, ShowInTaskbar = false, ShowActivated = false, IsHitTestVisible = false
        };
        try
        {
            window.Show();
            Settle();
            var list = (ListBox)window.FindName("ItemsScroll");
            var scroll = Descendants<ScrollViewer>(list).First();
            var cell = window.ItemWidth + window.ItemMargin.Left + window.ItemMargin.Right;
            var chrome = window.ActualWidth - scroll.ViewportWidth;
            var tolerance = 1.1 / VisualTreeHelper.GetDpi(window).DpiScaleX;

            void ResizeViewport(double width)
            {
                window.Width = width + chrome;
                Settle();
            }
            Rect Bounds(Border card) => card.TransformToAncestor(window).TransformBounds(new Rect(card.RenderSize));
            Border[] Cards(int row)
            {
                list.ScrollIntoView(list.Items[row]);
                Settle();
                var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromIndex(row);
                Require(container is not null, "Expected realized test row");
                return Descendants<Border>(container!).Where(b => b.Tag is FileItemViewModel)
                    .OrderBy(b => Bounds(b).Left).ToArray();
            }

            Check("Box: six columns survive shrinking the old right-hand reservation", () =>
            {
                foreach (var spare in new[] { 20d, 15d, 8d, 2d })
                {
                    ResizeViewport(6 * cell + spare);
                    Require(window.ItemColumns == 6, $"Premature wrap at viewport {scroll.ViewportWidth}");
                    Require(Cards(0).Length == 6);
                }
            });
            Check("Box: a full row keeps fixed card width", () =>
            {
                ResizeViewport(6 * cell + 21);
                var cards = Cards(0);
                Require(cards.Length == 6);
                foreach (var card in cards)
                {
                    Require(Math.Abs(card.ActualWidth - window.ItemWidth) <= tolerance,
                        $"Card width changed: {card.ActualWidth} instead of {window.ItemWidth}");
                    Require(Math.Abs(card.ActualHeight - window.ItemHeight) <= tolerance,
                        $"Card height changed: {card.ActualHeight} instead of {window.ItemHeight}");
                }
            });
            Check("Box: partial last row retains the first column's position and fixed size", () =>
            {
                ResizeViewport(6 * cell + 21);
                var first = Bounds(Cards(0)[0]);
                var tail = Cards(list.Items.Count - 1);
                Require(tail.Length == 1);
                var last = Bounds(tail[0]);
                Require(Math.Abs(first.Left - last.Left) <= tolerance
                        && Math.Abs(first.Width - last.Width) <= tolerance
                        && Math.Abs(first.Height - last.Height) <= tolerance);
            });
            Check("Box: only crossing the actual width boundary changes the row count", () =>
            {
                ResizeViewport(6 * cell - 4);
                Require(window.ItemColumns == 5 && Cards(0).Length == 5);
                ResizeViewport(6 * cell);
                Require(window.ItemColumns == 6 && Cards(0).Length == 6);
            });
            Check("Box: a 237-wide box still lays out three fixed cards", () =>
            {
                ResizeViewport(3 * cell + 1);
                Require(window.ItemColumns == 3, $"Expected three columns at viewport {scroll.ViewportWidth}");
                Require(Cards(0).Length == 3);
                foreach (var card in Cards(0))
                    Require(Math.Abs(card.ActualWidth - window.ItemWidth) <= tolerance);
            });
            Check("Box: resizing within one column count keeps virtualized row objects", () =>
            {
                ResizeViewport(6 * cell + 2);
                var rows = list.ItemsSource;
                ResizeViewport(6 * cell + 24);
                Require(ReferenceEquals(rows, list.ItemsSource), "Rows were unnecessarily rebuilt");
            });
        }
        finally { window.Close(); }

        static void Settle()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
