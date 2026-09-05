using System.ComponentModel;
using System.Windows.Media;

namespace DeskFlow.App;

/// <summary>
/// 盒子条目视图模型。图标由后台线程异步提取，因此 Icon 需要支持变更通知。
/// </summary>
public sealed class FileItemViewModel : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _isNameVisible;

    public FileItemViewModel(string path, string name, ImageSource? icon)
    {
        Path = path;
        Name = name;
        _icon = icon;
    }

    public string Path { get; }

    public string Name { get; }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value))
            {
                return;
            }

            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    /// <summary>名称是否可见：默认隐藏，鼠标悬停或单击选中时显示。</summary>
    public bool IsNameVisible
    {
        get => _isNameVisible;
        set
        {
            if (_isNameVisible == value)
            {
                return;
            }

            _isNameVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNameVisible)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
