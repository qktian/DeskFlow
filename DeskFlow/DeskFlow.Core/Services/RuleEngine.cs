using DeskFlow.Core.Models;

namespace DeskFlow.Core.Services;

public static class RuleEngine
{
    /// <summary>New user rules take precedence; normalize priorities to avoid overflow.</summary>
    public static void AddOverride(List<RuleModel> rules, RuleModel rule)
    {
        var ordered = rules.OrderBy(r => r.Priority).ThenBy(r => r.Name).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Priority = i + 1;
        rule.Priority = 0;
        rules.Add(rule);
    }
    /// <summary>生成内置默认规则（需配合 DefaultBoxes 使用，按名称绑定目标盒子）。</summary>
    public static List<RuleModel> CreateDefaultRules(IReadOnlyDictionary<string, string> boxIdByName)
    {
        var rules = new List<RuleModel>();
        var priority = 10;

        void Add(string name, FileItemKind? kind, string[]? extensions, string boxName)
        {
            if (!boxIdByName.TryGetValue(boxName, out var boxId))
            {
                return;
            }

            rules.Add(new RuleModel
            {
                Name = name,
                Priority = priority,
                Kind = kind,
                Extensions = extensions?.ToList() ?? new List<string>(),
                TargetBoxId = boxId
            });
            priority += 10;
        }

        Add("文件夹", FileItemKind.Folder, null, "文件夹");
        Add("快捷方式", FileItemKind.Shortcut, null, "快捷方式");
        Add("文档", FileItemKind.Document, null, "文档");
        Add("图片", FileItemKind.Image, null, "图片");
        Add("视频", FileItemKind.Video, null, "视频");
        Add("音乐", FileItemKind.Audio, null, "音乐");
        Add("压缩包", FileItemKind.Archive, null, "压缩包");
        Add("安装包", FileItemKind.Executable, null, "安装包");

        return rules;
    }

    /// <summary>
    /// 按优先级链匹配条目。返回命中的规则；未命中返回 null。
    /// </summary>
    public static RuleModel? Match(FileItem item, IEnumerable<RuleModel> rules)
    {
        return rules
            .Where(r => r.Enabled)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name)
            .FirstOrDefault(r => r.IsMatch(item));
    }

    /// <summary>
    /// 批量归类：返回 目标盒子Id → 条目路径 的映射。
    /// 未命中任何规则且存在未分类盒子时，条目落入未分类盒子。
    /// </summary>
    public static Dictionary<string, List<string>> Classify(
        IEnumerable<FileItem> items,
        IEnumerable<RuleModel> rules,
        IEnumerable<BoxModel> boxes)
    {
        var result = new Dictionary<string, List<string>>();
        var boxList = boxes.ToList();
        var ruleList = rules.Where(r => r.Enabled).OrderBy(r => r.Priority).ThenBy(r => r.Name).ToList();
        var unclassified = boxList.FirstOrDefault(b => b.Kind == BoxKind.Unclassified);
        var knownBoxIds = boxList.Select(b => b.Id).ToHashSet();

        void Push(string boxId, string path)
        {
            if (!result.TryGetValue(boxId, out var list))
            {
                list = new List<string>();
                result[boxId] = list;
            }

            list.Add(path);
        }

        foreach (var item in items)
        {
            var rule = ruleList.FirstOrDefault(r => r.IsMatch(item));
            if (rule is not null && knownBoxIds.Contains(rule.TargetBoxId))
            {
                Push(rule.TargetBoxId, item.Path);
            }
            else if (unclassified is not null)
            {
                Push(unclassified.Id, item.Path);
            }
        }

        return result;
    }
}
