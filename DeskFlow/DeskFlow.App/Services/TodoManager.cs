using System.Windows;
using DeskFlow.Core.Models;
using DeskFlow.Core.Services;
using DeskFlow.Shell;

namespace DeskFlow.App.Services;

/// <summary>
/// 待办组件生命周期管理：与 BoxManager 平级，负责创建/关闭/显隐/持久化待办清单窗口。
/// </summary>
public sealed class TodoManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly List<TodoBoardWindow> _windows = new();
    private bool _visible = true;
    private bool _disposed;

    public TodoManager(AppConfig config)
    {
        _config = config;
    }

    public IReadOnlyList<TodoBoardWindow> Windows => _windows;

    public bool IsVisible => _visible;

    /// <summary>面板 / 托盘请求打开「待办历史」窗口。</summary>
    public event EventHandler? HistoryRequested;

    public void RequestHistory() => HistoryRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>全局配置（供面板读取「显示已完成 / 时间范围」等筛选设置）。</summary>
    public AppConfig Config => _config;

    /// <summary>启动时按配置恢复已存在的待办组件。</summary>
    public void Initialize()
    {
        foreach (var model in _config.TodoBoards)
        {
            model.Normalize();
            OpenWindow(model);
        }
    }

    /// <summary>新建一个待办组件（在右下角附近层叠摆放）。</summary>
    public TodoBoardWindow AddBoard(string? name = null)
    {
        var primary = ScreenHelper.Primary;
        var area = primary.WorkArea;

        var model = new TodoBoardModel
        {
            Name = string.IsNullOrWhiteSpace(name) ? "待办事项" : name.Trim(),
            Width = 300,
            Height = 400,
            X = Math.Max(area.Left + 12, area.Right - 320 - (_config.TodoBoards.Count % 5) * 24),
            Y = area.Top + 48 + (_config.TodoBoards.Count % 5) * 28,
            ScreenId = primary.Id
        };

        _config.TodoBoards.Add(model);
        var window = OpenWindow(model);
        Save();
        return window;
    }

    private TodoBoardWindow OpenWindow(TodoBoardModel model)
    {
        var window = new TodoBoardWindow(model, this);
        window.Show();
        _windows.Add(window);
        return window;
    }

    public void RemoveBoard(TodoBoardModel model)
    {
        var window = _windows.FirstOrDefault(w => ReferenceEquals(w.Model, model));
        if (window is not null)
        {
            _windows.Remove(window);
            window.Close();
        }

        _config.TodoBoards.Remove(model);
        Save();
    }

    public void Save() => ConfigService.Save(_config);

    /// <summary>设置变更后重新加载所有待办面板（过滤规则可能已变化）。</summary>
    public void RefreshAll()
    {
        foreach (var window in _windows)
        {
            window.Reload();
        }
    }

    public void ToggleVisible() => SetVisible(!_visible);

    public void SetVisible(bool visible)
    {
        _visible = visible;
        foreach (var window in _windows)
        {
            window.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var window in _windows.ToList())
        {
            window.Close();
        }

        _windows.Clear();
        _disposed = true;
    }
}
