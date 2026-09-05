namespace DeskFlow.Core.Models;

public enum BoxKind
{
    /// <summary>普通盒子，按规则或手动收纳条目。</summary>
    Normal,

    /// <summary>未分类盒子，兜底收纳未命中任何规则的条目。</summary>
    Unclassified,

    /// <summary>文件夹门户，映射真实磁盘目录。</summary>
    Portal
}

public enum SortMode
{
    Name,
    Type,
    ModifiedAt,
    Size
}

public sealed class BoxStyle
{
    /// <summary>ARGB 十六进制字符串，如 #33000000。</summary>
    public string BackgroundColor { get; set; } = "#33000000";

    public double Opacity { get; set; } = 0.92;

    public int CornerRadius { get; set; } = 12;

    public string ForegroundColor { get; set; } = "#FFFFFF";

    public bool ShowTitle { get; set; } = true;
}

public sealed class BoxView
{
    public SortMode SortBy { get; set; } = SortMode.Name;
}

public sealed class BoxModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "新建盒子";

    public BoxKind Kind { get; set; } = BoxKind.Normal;

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 260;

    public double Height { get; set; } = 320;

    public string ScreenId { get; set; } = string.Empty;

    public int Order { get; set; }

    /// <summary>条目图标边长（像素），可调，默认 44。</summary>
    public int IconSize { get; set; } = 44;

    public bool Collapsed { get; set; }

    /// <summary>置顶显示（普通置顶，不与全屏应用冲突）。</summary>
    public bool Pinned { get; set; }

    public BoxStyle Style { get; set; } = new();

    public BoxView View { get; set; } = new();

    /// <summary>盒内条目的完整路径列表（手动或规则归类后的归属关系）。</summary>
    public List<string> Items { get; set; } = new();

    /// <summary>用户手动放入的条目路径，自动归类时不覆盖（除非开启强制归类）。</summary>
    public List<string> ManualItems { get; set; } = new();

    /// <summary>是否因贴近屏幕边缘而自动卷起（区别于用户手动折叠）。</summary>
    public bool EdgeRolled { get; set; }

    /// <summary>仅 Portal 盒子使用：绑定的真实目录。</summary>
    public string? PortalPath { get; set; }

    public void Normalize()
    {
        Width = Math.Clamp(Width, 160, 1200);
        Height = Math.Clamp(Height, 120, 1000);
    }

    /// <summary>就地复制另一个盒子的全部状态（不含 Id），用于设置窗口取消时回滚。</summary>
    public void CopyFrom(BoxModel other)
    {
        Name = other.Name;
        Kind = other.Kind;
        X = other.X;
        Y = other.Y;
        Width = other.Width;
        Height = other.Height;
        ScreenId = other.ScreenId;
        Order = other.Order;
        Collapsed = other.Collapsed;
        Pinned = other.Pinned;
        EdgeRolled = other.EdgeRolled;
        PortalPath = other.PortalPath;

        Style = new BoxStyle
        {
            BackgroundColor = other.Style.BackgroundColor,
            ForegroundColor = other.Style.ForegroundColor,
            Opacity = other.Style.Opacity,
            CornerRadius = other.Style.CornerRadius,
            ShowTitle = other.Style.ShowTitle
        };

        View = new BoxView { SortBy = other.View.SortBy };
        Items = new List<string>(other.Items);
        ManualItems = new List<string>(other.ManualItems);
    }
}
