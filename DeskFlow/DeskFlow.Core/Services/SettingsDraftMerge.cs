using DeskFlow.Core.Models;

namespace DeskFlow.Core.Services;

/// <summary>Apply only fields changed by the settings draft onto the latest live config.</summary>
public static class SettingsDraftMerge
{
    public static AppConfig Merge(AppConfig baseline, AppConfig draft, AppConfig current)
    {
        var result = ConfigService.Clone(current);
        MergeGeneral(baseline.General, draft.General, result.General);
        var baseBoxes = baseline.Boxes.ToDictionary(b => b.Id, StringComparer.Ordinal);
        var draftBoxes = draft.Boxes.ToDictionary(b => b.Id, StringComparer.Ordinal);
        result.Boxes.RemoveAll(box => baseBoxes.ContainsKey(box.Id) && !draftBoxes.ContainsKey(box.Id));
        var boxes = result.Boxes.ToDictionary(b => b.Id, StringComparer.Ordinal);
        foreach (var pair in draftBoxes)
        {
            if (!baseBoxes.TryGetValue(pair.Key, out var original))
            {
                if (!boxes.ContainsKey(pair.Key)) { var added = ConfigService.Clone(pair.Value); result.Boxes.Add(added); boxes.Add(pair.Key, added); }
                continue;
            }
            if (!boxes.TryGetValue(pair.Key, out var live)) continue;
            MergeBox(original, pair.Value, live);
        }
        MergeListById(baseline.Rules, draft.Rules, result.Rules, mergeRule: (old, edited, live) =>
        {
            if (old.Enabled != edited.Enabled) live.Enabled = edited.Enabled;
            if (old.Name != edited.Name) live.Name = edited.Name;
            if (!old.Extensions.SequenceEqual(edited.Extensions)) live.Extensions = edited.Extensions.ToList();
            if (!old.NameKeywords.SequenceEqual(edited.NameKeywords)) live.NameKeywords = edited.NameKeywords.ToList();
            if (old.Kind != edited.Kind) live.Kind = edited.Kind;
            if (old.TargetBoxId != edited.TargetBoxId) live.TargetBoxId = edited.TargetBoxId;
            if (old.Priority != edited.Priority) live.Priority = edited.Priority;
        });
        MergeSet(baseline.ExcludedPaths, draft.ExcludedPaths, result.ExcludedPaths);
        foreach (var old in baseline.Boxes)
        {
            if (!draftBoxes.TryGetValue(old.Id, out var edited) || !boxes.TryGetValue(old.Id, out var live)) continue;
            MergeSet(old.Items, edited.Items, live.Items);
            MergeSet(old.ManualItems, edited.ManualItems, live.ManualItems);
        }
        MergeListById(baseline.Snapshots, draft.Snapshots, result.Snapshots, (old, edited, live) =>
        {
            if (old.Name != edited.Name) live.Name = edited.Name;
            if (old.CreatedAt != edited.CreatedAt) live.CreatedAt = edited.CreatedAt;
            if (!old.ExcludedPaths.SequenceEqual(edited.ExcludedPaths)) live.ExcludedPaths = edited.ExcludedPaths.ToList();
        });
        result.Version = Math.Max(current.Version, draft.Version);
        return result;
    }

    private static void MergeGeneral(GeneralSettings old, GeneralSettings edit, GeneralSettings live)
    {
        if (old.LayoutInitialized != edit.LayoutInitialized) live.LayoutInitialized = edit.LayoutInitialized;
        if (old.AutoStart != edit.AutoStart) live.AutoStart = edit.AutoStart;
        if (old.HideSystemIcons != edit.HideSystemIcons) live.HideSystemIcons = edit.HideSystemIcons;
        if (old.DoubleClickToHide != edit.DoubleClickToHide) live.DoubleClickToHide = edit.DoubleClickToHide;
        if (old.EdgeRoll != edit.EdgeRoll) live.EdgeRoll = edit.EdgeRoll;
        if (old.SnapToScreenEdge != edit.SnapToScreenEdge) live.SnapToScreenEdge = edit.SnapToScreenEdge;
        if (old.SnapBoxes != edit.SnapBoxes) live.SnapBoxes = edit.SnapBoxes;
        if (old.IconSize != edit.IconSize) live.IconSize = edit.IconSize;
        if (old.UseAcrylic != edit.UseAcrylic) live.UseAcrylic = edit.UseAcrylic;
        if (old.ForceAutoClassify != edit.ForceAutoClassify) live.ForceAutoClassify = edit.ForceAutoClassify;
        if (old.ShowCompletedTodos != edit.ShowCompletedTodos) live.ShowCompletedTodos = edit.ShowCompletedTodos;
        if (old.PreferredMonitorId != edit.PreferredMonitorId) live.PreferredMonitorId = edit.PreferredMonitorId;
        if (old.Hotkeys.ToggleBoxes != edit.Hotkeys.ToggleBoxes) live.Hotkeys.ToggleBoxes = edit.Hotkeys.ToggleBoxes;
        if (old.Hotkeys.Organize != edit.Hotkeys.Organize) live.Hotkeys.Organize = edit.Hotkeys.Organize;
    }

    private static void MergeBox(BoxModel old, BoxModel edit, BoxModel live)
    {
        if (old.Name != edit.Name) live.Name = edit.Name;
        if (old.Pinned != edit.Pinned) live.Pinned = edit.Pinned;
        if (old.Style.BackgroundColor != edit.Style.BackgroundColor) live.Style.BackgroundColor = edit.Style.BackgroundColor;
        if (old.Style.ForegroundColor != edit.Style.ForegroundColor) live.Style.ForegroundColor = edit.Style.ForegroundColor;
        if (old.Style.Opacity != edit.Style.Opacity) live.Style.Opacity = edit.Style.Opacity;
        if (old.Style.CornerRadius != edit.Style.CornerRadius) live.Style.CornerRadius = edit.Style.CornerRadius;
        if (old.Style.ShowTitle != edit.Style.ShowTitle) live.Style.ShowTitle = edit.Style.ShowTitle;
        if (old.View.SortBy != edit.View.SortBy) live.View.SortBy = edit.View.SortBy;
    }

    private static void MergeSet(List<string> baseline, List<string> edited, List<string> live)
    {
        var before = baseline.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = edited.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = live.ToHashSet(StringComparer.OrdinalIgnoreCase);
        current.ExceptWith(before.Except(after));
        current.UnionWith(after.Except(before));
        var order = live.Where(current.Contains).ToList();
        foreach (var path in edited) if (current.Contains(path) && !order.Contains(path, StringComparer.OrdinalIgnoreCase)) order.Add(path);
        live.Clear(); live.AddRange(order);
    }

    private static void MergeListById<T>(List<T> baseline, List<T> edited, List<T> live, Action<T, T, T>? merge = null,
        Action<T, T, T>? mergeRule = null) where T : class
    {
        var old = baseline.ToDictionary(ItemId, StringComparer.Ordinal);
        var edits = edited.ToDictionary(ItemId, StringComparer.Ordinal);
        var current = live.ToDictionary(ItemId, StringComparer.Ordinal);
        foreach (var id in old.Keys.Except(edits.Keys)) { current.Remove(id); live.RemoveAll(item => ItemId(item) == id); }
        foreach (var pair in edits)
        {
            if (!old.TryGetValue(pair.Key, out var baseItem))
            {
                if (!current.ContainsKey(pair.Key)) { live.Add(ConfigService.Clone(pair.Value)); }
                continue;
            }
            if (current.TryGetValue(pair.Key, out var liveItem)) (merge ?? mergeRule)?.Invoke(baseItem, pair.Value, liveItem);
        }
    }

    private static string ItemId<T>(T value) => value switch
    {
        RuleModel rule => rule.Id,
        BoxSnapshot snapshot => snapshot.Id,
        _ => throw new NotSupportedException(typeof(T).Name)
    };
}
