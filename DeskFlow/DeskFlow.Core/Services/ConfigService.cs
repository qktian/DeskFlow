using System.Text.Json;
using DeskFlow.Core.Models;

namespace DeskFlow.Core.Services;

public static class ConfigService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskFlow");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static string BackupPath => Path.Combine(ConfigDirectory, "config.bak.json");

    public static AppConfig Load()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);

            AppConfig? config = null;
            if (File.Exists(ConfigPath))
            {
                config = TryDeserialize(ConfigPath);
            }

            if (config is null && File.Exists(BackupPath))
            {
                config = TryDeserialize(BackupPath);
            }

            if (config is null)
            {
                config = CreateDefault();
                Save(config);
            }

            EnsureIntegrity(config);
            return config;
        }
        catch
        {
            return CreateDefault();
        }
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);

            if (File.Exists(ConfigPath))
            {
                File.Copy(ConfigPath, BackupPath, true);
            }

            var json = JsonSerializer.Serialize(config, SerializerOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 配置写入失败不阻断主流程
        }
    }

    private static AppConfig? TryDeserialize(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>补齐缺失字段，保证升级后配置可用。</summary>
    private static void EnsureIntegrity(AppConfig config)
    {
        config.Boxes ??= new List<BoxModel>();
        config.TodoBoards ??= new List<TodoBoardModel>();
        config.Snapshots ??= new List<BoxSnapshot>();
        config.Rules ??= new List<RuleModel>();
        config.ExcludedPaths ??= new List<string>();
        config.General ??= new GeneralSettings();
        config.General.Hotkeys ??= new HotkeySettings();

        foreach (var box in config.Boxes)
        {
            box.Style ??= new BoxStyle();
            box.View ??= new BoxView();
            box.Items ??= new List<string>();
            box.ManualItems ??= new List<string>();
            box.Normalize();
        }

        foreach (var todo in config.TodoBoards)
        {
            todo.Normalize();
        }

        var boxIds = config.Boxes.Select(b => b.Id).ToHashSet();
        config.Rules.RemoveAll(r => !boxIds.Contains(r.TargetBoxId));

        if (!config.Boxes.Any(b => b.Kind == BoxKind.Unclassified))
        {
            config.Boxes.Add(CreateBox("未分类", BoxKind.Unclassified));
        }
    }

    /// <summary>深拷贝可序列化对象，用于设置窗口的「取消」回滚快照。</summary>
    public static T Clone<T>(T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        return JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? value;
    }

    public static AppConfig CreateDefault()
    {
        var config = new AppConfig();

        var names = new[] { "文件夹", "快捷方式", "文档", "图片", "视频", "音乐", "压缩包", "安装包" };
        foreach (var name in names)
        {
            config.Boxes.Add(CreateBox(name, BoxKind.Normal));
        }

        config.Boxes.Add(CreateBox("未分类", BoxKind.Unclassified));

        var idByName = config.Boxes.ToDictionary(b => b.Name, b => b.Id, StringComparer.Ordinal);
        config.Rules = RuleEngine.CreateDefaultRules(idByName);

        return config;
    }

    private static BoxModel CreateBox(string name, BoxKind kind)
    {
        return new BoxModel
        {
            Name = name,
            Kind = kind,
            Width = 240,
            Height = kind == BoxKind.Unclassified ? 240 : 300
        };
    }
}
