namespace DeskFlow.Core.Models;

public sealed class HotkeySettings
{
    public string ToggleBoxes { get; set; } = "Ctrl+`";

    public string Organize { get; set; } = "Ctrl+Alt+O";
}

public sealed class GeneralSettings
{
    public bool AutoStart { get; set; } = true;

    /// <summary>隐藏系统桌面真实图标，改由本产品渲染（图标收编）。</summary>
    public bool HideSystemIcons { get; set; } = true;

    public bool DoubleClickToHide { get; set; } = true;

    /// <summary>盒子贴近屏幕边缘时自动卷起。</summary>
    public bool EdgeRoll { get; set; } = true;

    public bool SnapToScreenEdge { get; set; } = true;

    /// <summary>拖动盒子时自动对齐相邻盒子（边缘 / 中心吸附）。</summary>
    public bool SnapBoxes { get; set; } = true;

    /// <summary>所有盒子统一的图标边长（像素）。</summary>
    public int IconSize { get; set; } = 48;

    /// <summary>使用 Windows 亚克力背景（部分系统版本不支持，失败时自动回退纯色半透明）。</summary>
    public bool UseAcrylic { get; set; }

    /// <summary>强制自动归类：覆盖用户手动挪动过的条目。</summary>
    public bool ForceAutoClassify { get; set; }

    /// <summary>默认盒子是否已完成首次自动排布。</summary>
    public bool LayoutInitialized { get; set; }

    /// <summary>待办面板中是否显示已完成（含已打卡）的任务。</summary>
    public bool ShowCompletedTodos { get; set; } = true;

    /// <summary>屏幕热插拔后用作首选回收屏的显示器设备标识。</summary>
    public string PreferredMonitorId { get; set; } = string.Empty;

    public HotkeySettings Hotkeys { get; set; } = new();

    public void CopyFrom(GeneralSettings other)
    {
        AutoStart = other.AutoStart;
        HideSystemIcons = other.HideSystemIcons;
        DoubleClickToHide = other.DoubleClickToHide;
        EdgeRoll = other.EdgeRoll;
        SnapToScreenEdge = other.SnapToScreenEdge;
        SnapBoxes = other.SnapBoxes;
        IconSize = other.IconSize;
        UseAcrylic = other.UseAcrylic;
        ForceAutoClassify = other.ForceAutoClassify;
        LayoutInitialized = other.LayoutInitialized;
        ShowCompletedTodos = other.ShowCompletedTodos;
        PreferredMonitorId = other.PreferredMonitorId;
        Hotkeys.ToggleBoxes = other.Hotkeys.ToggleBoxes;
        Hotkeys.Organize = other.Hotkeys.Organize;
    }
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    public GeneralSettings General { get; set; } = new();

    public List<BoxModel> Boxes { get; set; } = new();

    /// <summary>待办组件清单（一个组件 = 一个清单面板）。</summary>
    public List<TodoBoardModel> TodoBoards { get; set; } = new();

    /// <summary>手动创建的桌面归属快照。</summary>
    public List<BoxSnapshot> Snapshots { get; set; } = new();

    public List<RuleModel> Rules { get; set; } = new();

    /// <summary>
    /// 用户手动「移出盒子」的条目路径。自动归类永不收纳这些条目，
    /// 直到用户在设置中心恢复、或文件被删除后由 Prune 清理。
    /// </summary>
    public List<string> ExcludedPaths { get; set; } = new();

    public BoxModel? UnclassifiedBox => Boxes.FirstOrDefault(b => b.Kind == BoxKind.Unclassified);
}
