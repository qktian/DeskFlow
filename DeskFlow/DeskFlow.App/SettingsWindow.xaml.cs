using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DeskFlow.App.Services;
using DeskFlow.Core.Models;
using DeskFlow.Core.Services;
using DeskFlow.Shell;

namespace DeskFlow.App;

public sealed class ComboItem<T>
{
    public ComboItem(string label, T value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }

    public T Value { get; }

    public override string ToString() => Label;
}

public sealed class BoxListEntry
{
    public BoxListEntry(BoxModel model)
    {
        Model = model;
    }

    public BoxModel Model { get; }

    public string DisplayName => Model.Kind switch
    {
        BoxKind.Unclassified => $"{Model.Name}（兜底）",
        BoxKind.Portal => $"{Model.Name}（门户）",
        _ => $"{Model.Name}   {Model.Items.Count} 项"
    };
}

public sealed class ExcludedEntry
{
    public ExcludedEntry(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public string DisplayName => System.IO.Path.GetFileName(Path);
}

public sealed class RuleListEntry
{
    public RuleListEntry(RuleModel model, IReadOnlyDictionary<string, string> boxNames)
    {
        Model = model;
        TargetBoxName = boxNames.TryGetValue(model.TargetBoxId, out var name) ? name : "（已删除）";
    }

    public RuleModel Model { get; }

    public bool Enabled
    {
        get => Model.Enabled;
        set => Model.Enabled = value;
    }

    public string Name => Model.Name;

    public string Condition => Model.Describe();

    public string TargetBoxName { get; }
}

public sealed class SnapshotListEntry
{
    public SnapshotListEntry(BoxSnapshot model) => Model = model;

    public BoxSnapshot Model { get; }

    public string DisplayName =>
        $"{Model.Name}   {Model.CreatedAt:yyyy-MM-dd HH:mm}   {Model.Boxes.Sum(b => b.Items.Count)} 项";
}

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly BoxManager _manager;
    private bool _loading;
    private BoxModel? _selectedBox;

    // 打开设置时的配置快照，用于「取消」时整体回滚
    private readonly List<BoxModel> _boxSnapshot;
    private readonly List<RuleModel> _ruleSnapshot;
    private readonly GeneralSettings _generalSnapshot;
    private readonly List<string> _excludedSnapshot;

    public SettingsWindow(AppConfig config, BoxManager manager)
    {
        InitializeComponent();

        _config = config;
        _manager = manager;

        _boxSnapshot = config.Boxes.Select(b => ConfigService.Clone(b)).ToList();
        _ruleSnapshot = config.Rules.Select(r => ConfigService.Clone(r)).ToList();
        _generalSnapshot = ConfigService.Clone(config.General);
        _excludedSnapshot = new List<string>(config.ExcludedPaths);

        LoadSettings();
        RefreshBoxList();
        RefreshRuleList();
        RefreshExcludedList();
        RefreshSnapshots();

        WireEditorEvents();
    }

    private void WireEditorEvents()
    {
        BoxNameBox.TextChanged += (_, _) => ApplyEditorToBox();
        BoxColorBox.TextChanged += (_, _) => ApplyEditorToBox();
        BoxForeColorBox.TextChanged += (_, _) => ApplyEditorToBox();
        ShowTitleCheckBox.Click += (_, _) => ApplyEditorToBox();
        PinnedCheckBox.Click += (_, _) => ApplyEditorToBox();
        SortComboBox.SelectionChanged += (_, _) => ApplyEditorToBox();
    }

    /// <summary>设置保存后触发，宿主据此重新注册热键、钩子等系统级能力。</summary>
    public event EventHandler? SettingsApplied;

    private void LoadSettings()
    {
        _loading = true;

        AutoStartCheckBox.IsChecked = _config.General.AutoStart;
        HideIconsCheckBox.IsChecked = _config.General.HideSystemIcons;
        DoubleClickCheckBox.IsChecked = _config.General.DoubleClickToHide;
        EdgeRollCheckBox.IsChecked = _config.General.EdgeRoll;
        SnapCheckBox.IsChecked = _config.General.SnapToScreenEdge;
        SnapBoxesCheckBox.IsChecked = _config.General.SnapBoxes;
        GlobalIconSizeSlider.Value = _config.General.IconSize;
        GlobalIconSizeValueText.Text = _config.General.IconSize.ToString();
        ForceClassifyCheckBox.IsChecked = _config.General.ForceAutoClassify;

        ShowCompletedTodosCheckBox.IsChecked = _config.General.ShowCompletedTodos;

        ToggleHotkeyBox.Text = _config.General.Hotkeys.ToggleBoxes;
        OrganizeHotkeyBox.Text = _config.General.Hotkeys.Organize;

        SortComboBox.ItemsSource = new[]
        {
            new ComboItem<SortMode>("名称", SortMode.Name),
            new ComboItem<SortMode>("类型", SortMode.Type),
            new ComboItem<SortMode>("修改时间", SortMode.ModifiedAt),
            new ComboItem<SortMode>("大小", SortMode.Size)
        };
        SortComboBox.DisplayMemberPath = nameof(ComboItem<SortMode>.Label);
        SortComboBox.SelectedValuePath = nameof(ComboItem<SortMode>.Value);

        RuleKindBox.ItemsSource = new[]
        {
            new ComboItem<FileItemKind?>("不限制", null),
            new ComboItem<FileItemKind?>("文件夹", FileItemKind.Folder),
            new ComboItem<FileItemKind?>("快捷方式", FileItemKind.Shortcut),
            new ComboItem<FileItemKind?>("文档", FileItemKind.Document),
            new ComboItem<FileItemKind?>("图片", FileItemKind.Image),
            new ComboItem<FileItemKind?>("视频", FileItemKind.Video),
            new ComboItem<FileItemKind?>("音乐", FileItemKind.Audio),
            new ComboItem<FileItemKind?>("压缩包", FileItemKind.Archive),
            new ComboItem<FileItemKind?>("安装包", FileItemKind.Executable),
            new ComboItem<FileItemKind?>("其他", FileItemKind.Other)
        };
        RuleKindBox.DisplayMemberPath = nameof(ComboItem<FileItemKind?>.Label);
        RuleKindBox.SelectedValuePath = nameof(ComboItem<FileItemKind?>.Value);
        RuleKindBox.SelectedIndex = 0;

        ConfigPathText.Text = ConfigService.ConfigPath;
        VersionText.Text = "版本 " + (GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

        _loading = false;
    }

    private void RefreshBoxList()
    {
        var selectedId = _selectedBox?.Id;
        BoxList.ItemsSource = _config.Boxes
            .OrderBy(b => b.Kind == BoxKind.Unclassified)
            .ThenBy(b => b.Order)
            .Select(b => new BoxListEntry(b))
            .ToList();

        // 重新绑定目标盒子下拉时保留用户已选的盒子
        var previousTarget = RuleTargetBox.SelectedValue as string;
        RuleTargetBox.ItemsSource = _config.Boxes
            .Select(b => new ComboItem<string>(b.Name, b.Id))
            .ToList();
        RuleTargetBox.DisplayMemberPath = nameof(ComboItem<string>.Label);
        RuleTargetBox.SelectedValuePath = nameof(ComboItem<string>.Value);

        if (RuleTargetBox.Items.Count > 0)
        {
            var match = previousTarget is null
                ? null
                : RuleTargetBox.Items
                    .OfType<ComboItem<string>>()
                    .FirstOrDefault(i => i.Value == previousTarget);

            RuleTargetBox.SelectedItem = match ?? RuleTargetBox.Items[0];
        }

        if (selectedId is not null)
        {
            BoxList.SelectedItem = BoxList.Items
                .OfType<BoxListEntry>()
                .FirstOrDefault(e => e.Model.Id == selectedId);
        }
    }

    private void RefreshRuleList()
    {
        var boxNames = _config.Boxes.ToDictionary(b => b.Id, b => b.Name);
        RuleList.ItemsSource = _config.Rules
            .OrderBy(r => r.Priority)
            .Select(r => new RuleListEntry(r, boxNames))
            .ToList();
    }

    private void RefreshExcludedList()
    {
        ExcludedList.ItemsSource = _config.ExcludedPaths
            .Select(p => new ExcludedEntry(p))
            .ToList();
    }

    // ---------- 快照 ----------

    private void RefreshSnapshots()
    {
        SnapshotList.ItemsSource = _config.Snapshots
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new SnapshotListEntry(s))
            .ToList();
        RestoreSnapshotButton.IsEnabled = false;
        DeleteSnapshotButton.IsEnabled = false;
    }

    private void SnapshotList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var has = SnapshotList.SelectedItem is SnapshotListEntry;
        RestoreSnapshotButton.IsEnabled = has;
        DeleteSnapshotButton.IsEnabled = has;
    }

    private void CreateSnapshotButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog("保存快照", "快照名称", $"快照 {DateTime.Now:yyyy-MM-dd HH:mm}") { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText))
        {
            return;
        }

        _manager.CreateSnapshot(dialog.InputText.Trim());
        RefreshSnapshots();
    }

    private void RestoreSnapshotButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotListEntry entry)
        {
            return;
        }

        var snapshot = entry.Model;
        var total = snapshot.Boxes.Sum(b => b.Items.Count);
        if (MessageBox.Show(
                $"恢复快照「{snapshot.Name}」？\n当前盒子的条目归属会被替换为快照时的状态，\n已不存在的 {total} 项中找不到的会被忽略。",
                "DeskFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var (restored, skipped) = _manager.RestoreSnapshot(snapshot);

        // 恢复结果作为新的「取消回滚基线」，避免点取消时撤销恢复
        _boxSnapshot.Clear();
        _boxSnapshot.AddRange(_config.Boxes.Select(b => ConfigService.Clone(b)));
        _excludedSnapshot.Clear();
        _excludedSnapshot.AddRange(_config.ExcludedPaths);

        RefreshBoxList();
        RefreshRuleList();
        RefreshExcludedList();

        MessageBox.Show(
            $"已恢复「{snapshot.Name}」：还原 {restored} 项，忽略 {skipped} 个已不存在的条目。",
            "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DeleteSnapshotButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotListEntry entry)
        {
            return;
        }

        if (MessageBox.Show($"删除快照「{entry.Model.Name}」？", "DeskFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _manager.DeleteSnapshot(entry.Model);
        RefreshSnapshots();
    }

    // ---------- 已排除条目 ----------

    private void RestoreExcludedButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ExcludedList.SelectedItem is not ExcludedEntry entry)
        {
            MessageBox.Show("请先选择要恢复的条目。", "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _manager.RestoreItem(entry.Path);
        RefreshExcludedList();
        RefreshBoxList();
    }

    private void RestoreAllExcludedButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_config.ExcludedPaths.Count == 0)
        {
            return;
        }

        if (MessageBox.Show("恢复全部被排除的条目？它们会按规则重新归入盒子。",
                "DeskFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _manager.ClearExcluded();
        RefreshExcludedList();
        RefreshBoxList();
    }

    // ---------- 盒子编辑 ----------

    private void BoxList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BoxList.SelectedItem is not BoxListEntry entry)
        {
            BoxEditor.IsEnabled = false;
            _selectedBox = null;
            return;
        }

        _selectedBox = entry.Model;
        BoxEditor.IsEnabled = true;
        DeleteBoxButton.IsEnabled = entry.Model.Kind != BoxKind.Unclassified;

        _loading = true;
        BoxNameBox.Text = entry.Model.Name;
        BoxColorBox.Text = entry.Model.Style.BackgroundColor;
        BoxForeColorBox.Text = entry.Model.Style.ForegroundColor;
        OpacitySlider.Value = entry.Model.Style.Opacity;
        RadiusSlider.Value = entry.Model.Style.CornerRadius;
        ShowTitleCheckBox.IsChecked = entry.Model.Style.ShowTitle;
        PinnedCheckBox.IsChecked = entry.Model.Pinned;
        SortComboBox.SelectedValue = entry.Model.View.SortBy;
        OpacityValueText.Text = $"{(int)(entry.Model.Style.Opacity * 100)}%";
        RadiusValueText.Text = entry.Model.Style.CornerRadius.ToString();
        _loading = false;
    }

    private void ApplyEditorToBox()
    {
        if (_loading || _selectedBox is null)
        {
            return;
        }

        _selectedBox.Name = string.IsNullOrWhiteSpace(BoxNameBox.Text) ? _selectedBox.Name : BoxNameBox.Text.Trim();
        _selectedBox.Style.BackgroundColor = BoxColorBox.Text.Trim();
        _selectedBox.Style.ForegroundColor = BoxForeColorBox.Text.Trim();
        _selectedBox.Style.Opacity = OpacitySlider.Value;
        _selectedBox.Style.CornerRadius = (int)RadiusSlider.Value;
        _selectedBox.Style.ShowTitle = ShowTitleCheckBox.IsChecked == true;
        _selectedBox.Pinned = PinnedCheckBox.IsChecked == true;

        if (SortComboBox.SelectedValue is SortMode sortMode)
        {
            _selectedBox.View.SortBy = sortMode;
        }
    }

    private void OpacitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        OpacityValueText.Text = $"{(int)(e.NewValue * 100)}%";
        ApplyEditorToBox();
    }

    private void GlobalIconSizeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        GlobalIconSizeValueText.Text = ((int)e.NewValue).ToString();
    }

    private void RadiusSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        RadiusValueText.Text = ((int)e.NewValue).ToString();
        ApplyEditorToBox();
    }

    private void AddBoxButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog("新建盒子", "盒子名称", "新建盒子") { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText))
        {
            return;
        }

        _manager.AddBox(dialog.InputText.Trim());
        RefreshBoxList();
        RefreshRuleList();
    }

    private void DeleteBoxButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (BoxList.SelectedItem is not BoxListEntry entry)
        {
            return;
        }

        if (entry.Model.Kind == BoxKind.Unclassified)
        {
            MessageBox.Show("未分类盒子是兜底容器，不能删除。", "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"删除盒子「{entry.Model.Name}」？盒内文件不会被删除，只会回到未分类。",
                "DeskFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _manager.RemoveBox(entry.Model);
        _selectedBox = null;
        RefreshBoxList();
        RefreshRuleList();
        _manager.AssignNewItems();
    }

    // ---------- 规则编辑 ----------

    private void AddRuleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (RuleTargetBox.SelectedValue is not string boxId)
        {
            MessageBox.Show("请先选择要归入的盒子。", "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = string.IsNullOrWhiteSpace(RuleNameBox.Text) ? "自定义规则" : RuleNameBox.Text.Trim();
        var extensions = (RuleExtensionsBox.Text ?? string.Empty)
            .Split(new[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.TrimStart('.').ToLowerInvariant())
            .Where(x => x.Length > 0)
            .ToList();

        var kind = RuleKindBox.SelectedValue is FileItemKind kindValue ? kindValue : (FileItemKind?)null;

        // 无扩展名、无类型、无关键词的规则会匹配所有条目，等于把桌面清空到单一盒子
        if (extensions.Count == 0 && kind is null)
        {
            MessageBox.Show("请至少填写一个扩展名，或指定文件类型。\n两者都为空的规则会匹配所有条目。",
                "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _config.Rules.Add(new RuleModel
        {
            Name = name,
            Priority = (_config.Rules.Count == 0 ? 0 : _config.Rules.Max(r => r.Priority)) + 10,
            Extensions = extensions,
            Kind = kind,
            TargetBoxId = boxId
        });

        RuleNameBox.Clear();
        RuleExtensionsBox.Clear();
        RefreshRuleList();
    }

    private void DeleteRuleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not RuleListEntry entry)
        {
            return;
        }

        _config.Rules.Remove(entry.Model);
        RefreshRuleList();
    }

    // ---------- 维护操作 ----------

    private void OrganizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ApplyGeneralSettings();
        _manager.OrganizeAll();
        RefreshBoxList();
        RefreshRuleList();
    }

    private void RelayoutButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("按屏幕重新排布全部盒子，当前位置会被覆盖。继续？", "DeskFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _manager.AutoLayout();
        _manager.Rebuild();
    }

    private void RestoreIconsButton_OnClick(object sender, RoutedEventArgs e)
    {
        DesktopIconService.SetIconsVisible(true);
        HideIconsCheckBox.IsChecked = false;
        _config.General.HideSystemIcons = false;
    }

    private void OpenConfigFolder_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", ConfigService.ConfigDirectory) { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    private void ResetConfig_OnClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("恢复默认盒子与规则？当前布局和归类会被重置。", "DeskFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var defaults = ConfigService.CreateDefault();
        _config.General = defaults.General;
        _config.Boxes = defaults.Boxes;
        _config.Rules = defaults.Rules;

        _manager.Initialize();
        LoadSettings();
        RefreshBoxList();
        RefreshRuleList();
        RefreshExcludedList();
    }

    // ---------- 保存 ----------

    private void ApplyGeneralSettings()
    {
        _config.General.AutoStart = AutoStartCheckBox.IsChecked == true;
        _config.General.HideSystemIcons = HideIconsCheckBox.IsChecked == true;
        _config.General.DoubleClickToHide = DoubleClickCheckBox.IsChecked == true;
        _config.General.EdgeRoll = EdgeRollCheckBox.IsChecked == true;
        _config.General.SnapToScreenEdge = SnapCheckBox.IsChecked == true;
        _config.General.SnapBoxes = SnapBoxesCheckBox.IsChecked == true;
        _config.General.IconSize = (int)Math.Clamp(GlobalIconSizeSlider.Value, 24, 80);
        _config.General.UseAcrylic = false;
        _config.General.ForceAutoClassify = ForceClassifyCheckBox.IsChecked == true;
        _config.General.ShowCompletedTodos = ShowCompletedTodosCheckBox.IsChecked == true;
        _config.General.Hotkeys.ToggleBoxes = (ToggleHotkeyBox.Text ?? string.Empty).Trim();
        _config.General.Hotkeys.Organize = (OrganizeHotkeyBox.Text ?? string.Empty).Trim();
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        ApplyGeneralSettings();
        ApplyEditorToBox();

        AutoStartService.SetEnabled(_config.General.AutoStart);
        DesktopIconService.SetIconsVisible(!_config.General.HideSystemIcons);

        _manager.ApplyStyles();
        _manager.Save();

        SettingsApplied?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        RollbackChanges();
        Close();
    }

    /// <summary>
    /// 把「打开设置」以来的改动整体回滚：编辑器是即时写模型的，
    /// 取消必须还原盒子增删与属性、规则增删、排除列表与常规设置，
    /// 并把已经生效的系统级副作用（桌面图标、开机自启）同步回原状态。
    /// </summary>
    private void RollbackChanges()
    {
        _loading = true;
        try
        {
            var snapshotIds = _boxSnapshot
                .Select(b => b.Id)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var box in _config.Boxes.ToList())
            {
                if (!snapshotIds.Contains(box.Id))
                {
                    _manager.RemoveBox(box, save: false);
                }
            }

            var restored = new List<BoxModel>();
            foreach (var snapshot in _boxSnapshot)
            {
                var target = _config.Boxes.FirstOrDefault(b => b.Id == snapshot.Id);
                if (target is null)
                {
                    target = new BoxModel { Id = snapshot.Id };
                    target.CopyFrom(snapshot);
                    _config.Boxes.Add(target);
                }
                else
                {
                    target.CopyFrom(snapshot);
                }

                restored.Add(target);
            }

            _config.Boxes.Clear();
            _config.Boxes.AddRange(restored);

            _config.Rules.Clear();
            _config.Rules.AddRange(_ruleSnapshot.Select(r => ConfigService.Clone(r)));

            _config.ExcludedPaths.Clear();
            _config.ExcludedPaths.AddRange(_excludedSnapshot);

            _config.General.CopyFrom(_generalSnapshot);

            _manager.Rebuild();
            _manager.ApplyStyles();

            DesktopIconService.SetIconsVisible(!_config.General.HideSystemIcons);
            AutoStartService.SetEnabled(_config.General.AutoStart);
        }
        finally
        {
            _loading = false;
        }
    }
}
