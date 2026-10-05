using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DeskFlow.App.Services;
using DeskFlow.Core.Models;
using DeskFlow.Shell;

namespace DeskFlow.App;

/// <summary>Offline search of paths already collected in boxes; never crawls the disk.</summary>
public sealed class SearchWindow : Window
{
    private sealed record Result(string Path, string BoxName, string BoxId)
    {
        public string Label => $"{FileItem.GetDisplayName(Path)}    ·    {BoxName}\n{Path}";
    }

    private readonly BoxManager _manager;
    private readonly TextBox _query = new() { Margin = new Thickness(12, 12, 12, 6), Height = 32,
        VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListBox _results = new() { Margin = new Thickness(12, 0, 12, 6), DisplayMemberPath = nameof(Result.Label) };
    private readonly TextBlock _status = new() { Margin = new Thickness(12, 0, 12, 10) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private List<Result> _index = new();

    public SearchWindow(BoxManager manager)
    {
        _manager = manager;
        Title = "搜索已收纳条目 · DeskFlow";
        Width = 640; Height = 540; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(_query);
        Grid.SetRow(_results, 1); grid.Children.Add(_results);
        var footer = new DockPanel();
        var open = new Button { Content = "打开", Width = 72, Height = 28, Margin = new Thickness(4) };
        var reveal = new Button { Content = "在资源管理器中显示", Width = 142, Height = 28, Margin = new Thickness(4) };
        var refresh = new Button { Content = "刷新索引", Width = 88, Height = 28, Margin = new Thickness(4) };
        DockPanel.SetDock(open, Dock.Right); DockPanel.SetDock(reveal, Dock.Right); DockPanel.SetDock(refresh, Dock.Right);
        footer.Children.Add(open); footer.Children.Add(reveal); footer.Children.Add(refresh); footer.Children.Add(_status);
        Grid.SetRow(footer, 2); grid.Children.Add(footer);
        Content = grid;

        _debounce.Tick += (_, _) => { _debounce.Stop(); Filter(); };
        _query.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _results.MouseDoubleClick += (_, _) => OpenSelected();
        _results.KeyDown += (_, e) => { if (e.Key == Key.Enter) OpenSelected(); };
        open.Click += (_, _) => OpenSelected();
        reveal.Click += (_, _) => { if (_results.SelectedItem is Result result) FileOperationService.ShowInExplorer(result.Path); };
        refresh.Click += (_, _) => RefreshIndex();
        Loaded += (_, _) => _query.Focus();
        RefreshIndex();
    }

    private void RefreshIndex()
    {
        _index = _manager.Config.Boxes
            .SelectMany(b => b.Items.Select(path => new Result(path, b.Name, b.Id)))
            .Where(r => File.Exists(r.Path) || Directory.Exists(r.Path))
            .DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Filter();
    }

    private void Filter()
    {
        var query = _query.Text.Trim();
        var found = string.IsNullOrEmpty(query) ? _index : _index.Where(r =>
            r.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            r.BoxName.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var result = found.Take(500).ToList();
        _results.ItemsSource = result;
        _status.Text = $"{result.Count} / {_index.Count} 项" + (result.Count == 500 ? "（最多显示前 500 项，请继续输入）" : "");
    }

    private void OpenSelected()
    {
        if (_results.SelectedItem is Result result) FileOperationService.Open(result.Path);
    }
}
