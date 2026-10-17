using System.Text.Json;
using DeskFlow.Core.Models;

namespace DeskFlow.Core.Services;

public static class ConfigService
{
    private static readonly object SaveLock = new();
    public static event Action<Exception>? SaveFailed;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskFlow");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static string BackupPath => Path.Combine(ConfigDirectory, "config.bak.json");

    public static AppConfig Load(string? directory = null)
    {
        directory ??= ConfigDirectory;
        var configPath = Path.Combine(directory, "config.json");
        var backupPath = Path.Combine(directory, "config.bak.json");
        try
        {
            Directory.CreateDirectory(directory);

            AppConfig? config = null;
            if (File.Exists(configPath))
            {
                config = TryDeserialize(configPath);
            }

            if (config is null && File.Exists(backupPath))
            {
                config = TryDeserialize(backupPath);
                DiagnosticLog.Info("Config", config is null ? "Backup unreadable." : "Loaded backup configuration.");
            }

            if (config is null)
            {
                config = CreateDefault();
                Save(config, directory);
            }

            EnsureIntegrity(config);
            return config;
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("Config", "Load failed; using defaults", error);
            return CreateDefault();
        }
    }

    public static bool Save(AppConfig config, string? directory = null)
    {
        directory ??= ConfigDirectory;
        string? temporaryPath = null;
        try
        {
            lock (SaveLock)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "config.json");
                var backup = Path.Combine(directory, "config.bak.json");
                var json = JsonSerializer.Serialize(config, SerializerOptions);
                // Frequent refreshes often leave the configuration unchanged.
                if (File.Exists(path) && File.ReadAllText(path) == json) return true;

                temporaryPath = Path.Combine(directory, $"config.{Guid.NewGuid():N}.tmp");
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    // Never overwrite the last good backup with a corrupt primary.
                    var validPrimary = TryDeserialize(path) is not null;
                    File.Replace(temporaryPath, path, validPrimary ? backup : null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
                temporaryPath = null;
                return true;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Config", "Save failed; previous configuration retained", ex);
            SaveFailed?.Invoke(ex);
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { /* Keep the original save error. */ }
            }
        }
    }

    private static AppConfig? TryDeserialize(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
            if (config is not null) EnsureIntegrity(config);
            return config;
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("Config", "Could not read configuration", error);
            return null;
        }
    }

    /// <summary>补齐缺失字段，保证升级后配置可用。</summary>
    private static void EnsureIntegrity(AppConfig config)
    {
        config.Pomodoro ??= new PomodoroData();
        config.Pomodoro.Normalize();
        config.Boxes ??= new List<BoxModel>();
        config.TodoBoards ??= new List<TodoBoardModel>();
        config.Snapshots ??= new List<BoxSnapshot>();
        config.Rules ??= new List<RuleModel>();
        config.ExcludedPaths ??= new List<string>();
        config.General ??= new GeneralSettings();
        config.General.Hotkeys ??= new HotkeySettings();

        config.Boxes.RemoveAll(b => b is null);
        config.TodoBoards.RemoveAll(b => b is null);
        config.Rules.RemoveAll(r => r is null);
        config.Snapshots.RemoveAll(s => s is null);
        config.ExcludedPaths.RemoveAll(string.IsNullOrWhiteSpace);
        foreach (var rule in config.Rules)
        {
            rule.Extensions = (rule.Extensions ?? new()).Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Distinct().ToList();
            rule.NameKeywords = (rule.NameKeywords ?? new()).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        }
        foreach (var snapshot in config.Snapshots)
        {
            snapshot.Boxes ??= new();
            snapshot.TodoLayouts ??= new();
            snapshot.ExcludedPaths ??= new();
            snapshot.Boxes.RemoveAll(b => b is null);
            snapshot.TodoLayouts.RemoveAll(b => b is null);
            snapshot.ExcludedPaths.RemoveAll(string.IsNullOrWhiteSpace);
            foreach (var box in snapshot.Boxes)
            {
                box.Items ??= new();
                box.ManualItems ??= new();
                box.Items.RemoveAll(string.IsNullOrWhiteSpace);
                box.ManualItems.RemoveAll(string.IsNullOrWhiteSpace);
                if (box.Layout is not null) box.Layout.Style ??= new BoxStyle();
            }
            foreach (var board in snapshot.TodoLayouts)
            {
                board.Layout ??= new PanelLayoutSnapshot();
                board.Layout.Style ??= new BoxStyle();
            }
        }

        foreach (var box in config.Boxes)
        {
            box.Style ??= new BoxStyle();
            box.View ??= new BoxView();
            box.Items ??= new List<string>();
            box.ManualItems ??= new List<string>();
            box.Items.RemoveAll(string.IsNullOrWhiteSpace);
            box.ManualItems.RemoveAll(string.IsNullOrWhiteSpace);
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
        var config = new AppConfig { Version = 2 };

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

    public static AppConfig? LoadBackup(string? directory = null)
    {
        directory ??= ConfigDirectory;
        try
        {
            var candidate = TryDeserialize(Path.Combine(directory, "config.bak.json"));
            if (candidate is null) return null;
            EnsureIntegrity(candidate);
            return candidate;
        }
        catch (Exception error) { DiagnosticLog.Error("Config", "Backup load failed", error); return null; }
    }

    public static bool RestoreBackup(out Exception? error, string? directory = null)
    {
        directory ??= ConfigDirectory;
        error = null;
        try
        {
            var candidate = LoadBackup(directory) ?? throw new InvalidDataException("配置备份不存在或无法读取。");
            Directory.CreateDirectory(directory);
            var current = Path.Combine(directory, "config.json");
            if (File.Exists(current))
            {
                var restorePoint = Path.Combine(directory, $"config.before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(current, restorePoint, overwrite: false);
            }
            if (!Save(candidate, directory)) throw new IOException("备份配置无法写入，请检查磁盘空间和目录权限。");
            DiagnosticLog.Info("Config", $"Restored backup config version {candidate.Version}.");
            return true;
        }
        catch (Exception failure) { error = failure; DiagnosticLog.Error("Config", "Backup restore failed", failure); return false; }
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
