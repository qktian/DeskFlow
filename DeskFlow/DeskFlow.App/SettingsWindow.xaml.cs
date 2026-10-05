using System.Diagnostics;
using System.IO;
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
    private AppConfig _config;
    private readonly AppConfig _liveConfig;
    private AppConfig _baseline;
    private readonly BoxManager _manager;
    private bool _loading;
    private BoxModel? _selectedBox;
    private string? _editingRuleId;

    public SettingsWindow(AppConfig config, BoxManager manager)
    {
        InitializeComponent();

        _liveConfig = config;
        _baseline = ConfigService.Clone(config);
        _config = ConfigService.Clone(config);
        _manager = manager;

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
        PreferredMonitorBox.ItemsSource = ScreenHelper.GetMonitors().Select(m =>
            new ComboItem<string>($"{(m.IsPrimary ? "主屏 · " : string.Empty)}{m.Id}  {m.Bounds.Width * m.ScaleX:0}×{m.Bounds.Height * m.ScaleY:0}  {m.ScaleX:P0}", m.Id)).ToList();
        PreferredMonitorBox.DisplayMemberPath = nameof(ComboItem<string>.Label);
        PreferredMonitorBox.SelectedValuePath = nameof(ComboItem<string>.Value);
        PreferredMonitorBox.SelectedValue = ScreenHelper.ForId(_config.General.PreferredMonitorId).Id;
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
        var selectedId = (RuleList.SelectedItem as RuleListEntry)?.Model.Id ?? _editingRuleId;
        var boxNames = _config.Boxes.ToDictionary(b => b.Id, b => b.Name);
        RuleList.ItemsSource = _config.Rules
            .OrderBy(r => r.Priority)
            .Select(r => new RuleListEntry(r, boxNames))
            .ToList();
        RuleList.SelectedItem = RuleList.Items.OfType<RuleListEntry>().FirstOrDefault(e => e.Model.Id == selectedId);
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

        var snapshot = _manager.CreateSnapshot(dialog.InputText.Trim());
        _config.Snapshots.Clear();
        _config.Snapshots.AddRange(_liveConfig.Snapshots.Select(ConfigService.Clone));
        _baseline.Snapshots.Clear();
        _baseline.Snapshots.AddRange(_liveConfig.Snapshots.Select(ConfigService.Clone));
        if (_manager.LastSnapshotPrunedCount > 0)
            MessageBox.Show($"已创建快照，并清理了 {_manager.LastSnapshotPrunedCount} 份较旧快照。当前上限为 {BoxManager.SnapshotLimit} 份或 32 MiB。", "快照容量管理", MessageBoxButton.OK, MessageBoxImage.Information);
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

        foreach (var box in _config.Boxes) { box.Items.Clear(); box.ManualItems.Clear(); }
        var (restored, skipped) = RestoreDraftSnapshot(snapshot);

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

        var liveSnapshot = _liveConfig.Snapshots.FirstOrDefault(s => s.Id == entry.Model.Id);
        if (liveSnapshot is not null) _manager.DeleteSnapshot(liveSnapshot);
        _config.Snapshots.RemoveAll(s => s.Id == entry.Model.Id);
        _baseline.Snapshots.RemoveAll(s => s.Id == entry.Model.Id);
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

        _config.ExcludedPaths.RemoveAll(p => string.Equals(p, entry.Path, StringComparison.OrdinalIgnoreCase));
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

        _config.ExcludedPaths.Clear();
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

        var primary = ScreenHelper.ForId(_config.General.PreferredMonitorId);
        _config.Boxes.Add(new BoxModel
        {
            Name = dialog.InputText.Trim(), Order = _config.Boxes.Count, Width = 240, Height = 300,
            X = Math.Max(12, 40 + (_config.Boxes.Count % 4) * 252), Y = 40, ScreenId = primary.Id
        });
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

        _config.Rules.RemoveAll(r => r.TargetBoxId == entry.Model.Id);
        _config.Boxes.Remove(entry.Model);
        _selectedBox = null;
        RefreshBoxList();
        RefreshRuleList();
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
        var keywords = (RuleKeywordsBox.Text ?? string.Empty)
            .Split(new[] { ',', '，', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (extensions.Count == 0 && kind is null && keywords.Count == 0)
        {
            MessageBox.Show("请至少填写一个扩展名，或指定文件类型。\n两者都为空的规则会匹配所有条目。",
                "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var existing = _config.Rules.FirstOrDefault(r => r.Id == _editingRuleId);
        if (existing is null)
        {
            RuleEngine.AddOverride(_config.Rules, new RuleModel
            {
                Name = name, Extensions = extensions, Kind = kind,
                NameKeywords = keywords, TargetBoxId = boxId
            });
        }
        else
        {
            existing.Name = name; existing.Extensions = extensions; existing.Kind = kind;
            existing.NameKeywords = keywords; existing.TargetBoxId = boxId;
        }
        RefreshRuleList();
    }

    private void RuleList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RuleList.SelectedItem is not RuleListEntry entry) return;
        var rule = entry.Model;
        _editingRuleId = rule.Id;
        RuleNameBox.Text = rule.Name;
        RuleExtensionsBox.Text = string.Join(",", rule.Extensions);
        RuleKeywordsBox.Text = string.Join(",", rule.NameKeywords);
        RuleKindBox.SelectedValue = rule.Kind;
        RuleTargetBox.SelectedValue = rule.TargetBoxId;
    }

    private void NewRuleButton_OnClick(object sender, RoutedEventArgs e)
    {
        _editingRuleId = null;
        RuleList.SelectedItem = null;
        RuleNameBox.Clear(); RuleExtensionsBox.Clear(); RuleKeywordsBox.Clear();
        RuleKindBox.SelectedIndex = 0;
    }

    private void MoveRuleUpButton_OnClick(object sender, RoutedEventArgs e) => MoveRule(-1);
    private void MoveRuleDownButton_OnClick(object sender, RoutedEventArgs e) => MoveRule(1);

    private void MoveRule(int direction)
    {
        if (RuleList.SelectedItem is not RuleListEntry selected) return;
        var ordered = _config.Rules.OrderBy(r => r.Priority).ThenBy(r => r.Name).ToList();
        var index = ordered.FindIndex(r => r.Id == selected.Model.Id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= ordered.Count) return;
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++) ordered[i].Priority = i * 10;
        RefreshRuleList();
    }

    private async void PreviewRulesButton_OnClick(object sender, RoutedEventArgs e)
    {
        RulePreviewText.Text = "正在扫描桌面并计算预览…";
        try
        {
            var draft = ConfigService.Clone(_config);
            var files = await Task.Run(DesktopScanner.Scan);
            var current = _liveConfig.Boxes.SelectMany(b => b.Items.Select(path => (path, b.Name)))
                .GroupBy(x => x.path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            var excluded = draft.ExcludedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var manual = draft.Boxes.SelectMany(b => b.ManualItems.Select(path => (path, b.Name)))
                .GroupBy(x => x.path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            var changed = 0;
            foreach (var file in files)
            {
                if (excluded.Contains(file.Path)) continue;
                var rule = RuleEngine.Match(file, draft.Rules);
                var target = manual.TryGetValue(file.Path, out var manualBox) && !draft.General.ForceAutoClassify
                    ? manualBox : draft.Boxes.FirstOrDefault(b => b.Id == rule?.TargetBoxId)?.Name
                        ?? draft.UnclassifiedBox?.Name ?? "未收纳";
                var before = current.GetValueOrDefault(file.Path, "未收纳");
                if (before == target) continue;
                changed++;
                if (lines.Count < 60) lines.Add($"{file.Name}: {before} → {target}（{rule?.Name ?? "未命中规则"}）");
            }
            RulePreviewText.Text = $"桌面 {files.Count} 项，预计变更 {changed} 项。规则更改保存后可执行整理。\n"
                + string.Join("\n", lines) + (changed > lines.Count ? $"\n…还有 {changed - lines.Count} 项" : "");
        }
        catch (Exception error) { RulePreviewText.Text = "预览失败：" + error.Message; }
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
        if (!CommitDraft()) return;
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

        ApplyGeneralSettings();
        if (!CommitDraft()) return;
        _manager.AutoLayout();
        _manager.Rebuild();
        _manager.Save();
        CheckpointDraft();
    }

    public Func<string, string, string?>? ValidateHotkeys { get; set; }
    public Action? RebuildTodos { get; set; }

    // An explicit recovery request must not be undone by cancelling an already-open settings window.
    public void KeepDesktopIconsVisible()
    {
        _config.General.HideSystemIcons = false;
        _liveConfig.General.HideSystemIcons = false;
        _baseline.General.HideSystemIcons = false;
        HideIconsCheckBox.IsChecked = false;
        ConfigService.Save(_liveConfig);
    }

    private void RestoreIconsButton_OnClick(object sender, RoutedEventArgs e)
    {
        HideIconsCheckBox.IsChecked = false;
        _config.General.HideSystemIcons = false;
        _liveConfig.General.HideSystemIcons = false;
        _baseline.General.HideSystemIcons = false;
        ConfigService.Save(_liveConfig);
        SettingsApplied?.Invoke(this, EventArgs.Empty);
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
        LayoutDraftDefaults();

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
        _config.General.PreferredMonitorId = PreferredMonitorBox.SelectedValue as string ?? string.Empty;
        _config.General.IconSize = (int)Math.Clamp(GlobalIconSizeSlider.Value, 24, 80);
        _config.General.UseAcrylic = false;
        _config.General.ForceAutoClassify = ForceClassifyCheckBox.IsChecked == true;
        _config.General.ShowCompletedTodos = ShowCompletedTodosCheckBox.IsChecked == true;
        _config.General.Hotkeys.ToggleBoxes = (ToggleHotkeyBox.Text ?? string.Empty).Trim();
        _config.General.Hotkeys.Organize = (OrganizeHotkeyBox.Text ?? string.Empty).Trim();
    }

    private void LayoutDraftDefaults()
    {
        var monitor = ScreenHelper.ForId(_config.General.PreferredMonitorId);
        var area = monitor.WorkArea;
        const double boxWidth = 240, boxHeight = 300, gap = 12;
        var ordered = _config.Boxes.OrderBy(box => box.Kind == BoxKind.Unclassified).ThenBy(box => box.Name).ToList();
        var rows = Math.Max(1, (int)((area.Height - 24) / (boxHeight + gap)));
        var columns = (int)Math.Ceiling(ordered.Count / (double)rows);
        var startX = Math.Max(area.Left + 12, area.Right - 12 - columns * (boxWidth + gap));
        for (var i = 0; i < ordered.Count; i++)
        {
            var box = ordered[i];
            var height = box.Kind == BoxKind.Unclassified ? Math.Min(boxHeight, 220) : boxHeight;
            box.Width = boxWidth; box.Height = height;
            box.X = startX - area.Left + i / rows * (boxWidth + gap);
            box.Y = 12 + i % rows * (height + gap);
            box.ScreenId = monitor.Id;
        }
        _config.General.LayoutInitialized = true;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var hotkeyError = ValidateHotkeys?.Invoke(ToggleHotkeyBox.Text.Trim(), OrganizeHotkeyBox.Text.Trim());
        if (hotkeyError is not null)
        {
            MessageBox.Show(hotkeyError, "快捷键未生效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ApplyGeneralSettings();
        ApplyEditorToBox();

        if (!CommitDraft())
        {
            ValidateHotkeys?.Invoke(_liveConfig.General.Hotkeys.ToggleBoxes, _liveConfig.General.Hotkeys.Organize);
            return;
        }
        AutoStartService.SetEnabled(_liveConfig.General.AutoStart);

        SettingsApplied?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool CommitDraft()
    {
        var merged = SettingsDraftMerge.Merge(_baseline, _config, _liveConfig);
        if (!ConfigService.Save(merged)) return false;
        _liveConfig.Version = merged.Version;
        _liveConfig.General.CopyFrom(merged.General);
        _liveConfig.Boxes = merged.Boxes;
        _liveConfig.Rules = merged.Rules;
        _liveConfig.ExcludedPaths = merged.ExcludedPaths;
        _liveConfig.Snapshots = merged.Snapshots;
        _baseline = ConfigService.Clone(_liveConfig);
        _config = ConfigService.Clone(_liveConfig);
        _manager.Rebuild();
        _manager.AssignNewItems();
        SettingsApplied?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void CheckpointDraft()
    {
        _liveConfig.Boxes = _manager.Config.Boxes;
        _baseline = ConfigService.Clone(_liveConfig);
        _config = ConfigService.Clone(_liveConfig);
        RefreshBoxList(); RefreshRuleList(); RefreshExcludedList(); RefreshSnapshots(); LoadSettings();
    }

    private void RestoreBackupButton_OnClick(object sender, RoutedEventArgs e)
    {
        var backup = ConfigService.LoadBackup();
        if (backup is null)
        {
            MessageBox.Show("没有可用的配置备份，或备份文件已损坏。", "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var summary = $"版本 {backup.Version} · {backup.Boxes.Count} 个盒子 · {backup.TodoBoards.Count} 个待办清单 · {backup.Snapshots.Count} 个快照";
        if (MessageBox.Show($"将从上次保存的备份恢复配置吗？\n\n{summary}\n\n当前配置会先保存为恢复点。", "恢复配置备份", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!ConfigService.RestoreBackup(out var error))
        {
            MessageBox.Show($"恢复失败，当前配置未替换。\n{error}", "恢复配置备份", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var restored = ConfigService.Load();
        ScreenHelper.MigrateLegacyCoordinates(restored);
        ConfigService.Save(restored);
        _liveConfig.Version = restored.Version;
        _liveConfig.General.CopyFrom(restored.General);
        _liveConfig.Boxes = restored.Boxes;
        _liveConfig.TodoBoards = restored.TodoBoards;
        _liveConfig.Snapshots = restored.Snapshots;
        _liveConfig.Rules = restored.Rules;
        _liveConfig.ExcludedPaths = restored.ExcludedPaths;
        _manager.Initialize();
        RebuildTodos?.Invoke();
        SettingsApplied?.Invoke(this, EventArgs.Empty);
        _baseline = ConfigService.Clone(_liveConfig);
        _config = ConfigService.Clone(_liveConfig);
        LoadSettings(); RefreshBoxList(); RefreshRuleList(); RefreshExcludedList(); RefreshSnapshots();
        MessageBox.Show("配置备份已恢复。恢复前的配置副本保存在配置目录中，文件名以 config.before-restore- 开头。", "恢复完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PruneHistoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var cutoff = DateTime.Now.AddYears(-1);
        var old = _liveConfig.TodoBoards.SelectMany(b => b.Items).Where(item => item.Kind == TodoKind.Recurring
            ? item.ClosedAt is DateTime closed && closed < cutoff
            : item.Completed && (item.CompletedAt ?? item.DueDate) is DateTime completed && completed < cutoff).ToList();
        var oldCheckIns = _liveConfig.TodoBoards.SelectMany(b => b.Items)
            .Where(item => item.Kind == TodoKind.Recurring && item.ClosedAt is null)
            .SelectMany(item => item.CheckIns.Keys.Where(key => DateTime.TryParseExact(key, "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out var day) && day < cutoff).Select(key => (item, key))).ToList();
        if (old.Count == 0 && oldCheckIns.Count == 0)
        {
            MessageBox.Show($"没有 {cutoff:yyyy-MM-dd} 以前已完成或已归档的任务。", "历史容量管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"清理 {cutoff:yyyy-MM-dd} 以前的历史吗？\n\n删除已完成 / 已归档任务：{old.Count} 条\n清除活动循环任务的旧打卡记录：{oldCheckIns.Count} 条\n活动任务本身会保留。", "清理待办历史", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var ids = old.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var board in _liveConfig.TodoBoards)
        {
            foreach (var item in board.Items.Where(item => ids.Contains(item.ParentId ?? string.Empty))) item.ParentId = null;
            board.Items.RemoveAll(item => ids.Contains(item.Id));
        }
        foreach (var (item, key) in oldCheckIns) item.CheckIns.Remove(key);
        if (!ConfigService.Save(_liveConfig)) return;
        RebuildTodos?.Invoke();
        SettingsApplied?.Invoke(this, EventArgs.Empty);
        MessageBox.Show($"已清理 {old.Count} 条历史任务和 {oldCheckIns.Count} 条旧打卡记录。", "历史容量管理", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private (int Restored, int Skipped) RestoreDraftSnapshot(BoxSnapshot snapshot)
    {
        var boxes = _config.Boxes.ToDictionary(b => b.Id, StringComparer.Ordinal);
        var fallback = _config.UnclassifiedBox;
        var excluded = new HashSet<string>(snapshot.ExcludedPaths, StringComparer.OrdinalIgnoreCase);
        var restored = 0;
        var skipped = 0;
        foreach (var saved in snapshot.Boxes)
        {
            var target = boxes.TryGetValue(saved.BoxId, out var match) ? match : fallback;
            foreach (var path in saved.Items.Concat(saved.ManualItems).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if ((!File.Exists(path) && !Directory.Exists(path)) || excluded.Contains(path)) { skipped++; continue; }
                target?.Items.Add(path);
                if (target is null) { skipped++; continue; }
                if (saved.ManualItems.Contains(path, StringComparer.OrdinalIgnoreCase)) target.ManualItems.Add(path);
                restored++;
            }
        }
        _config.ExcludedPaths = snapshot.ExcludedPaths.Where(p => File.Exists(p) || Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (restored, skipped);
    }
}
