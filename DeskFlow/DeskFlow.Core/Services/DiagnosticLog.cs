using System.Security.Cryptography;
using System.Text;

namespace DeskFlow.Core.Services;

/// <summary>Bounded local logs shared safely by the app and its recovery process.</summary>
public sealed class RollingLog(string directory, long maxBytes = 1024 * 1024, int backups = 3)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public void Write(string level, string component, string message, Exception? error = null)
    {
        try
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DirectoryPath.ToUpperInvariant())))[..24];
            using var mutex = new Mutex(false, "Local\\DeskFlow.Log." + key);
            bool acquired;
            try { acquired = mutex.WaitOne(200); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "deskflow.log");
                var detail = error is null ? string.Empty : " | " + error;
                var line = $"{DateTimeOffset.Now:O} [{Environment.ProcessId}] {level} {component}: {message}{detail}";
                line = line.Replace("\r", " ").Replace("\n", " ");
                if (line.Length > 12000) line = line[..12000] + " [truncated]";
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > maxBytes)
                {
                    for (var i = Math.Clamp(backups, 1, 10); i >= 1; i--)
                    {
                        var source = i == 1 ? path : path + "." + (i - 1);
                        if (File.Exists(source)) File.Move(source, path + "." + i, overwrite: true);
                    }
                }
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch { /* Diagnostics must never interrupt recovery or application shutdown. */ }
    }
}

public static class DiagnosticLog
{
    public static RollingLog Current { get; set; } = new(Path.Combine(ConfigService.ConfigDirectory, "logs"));
    public static void Info(string component, string message) => Current.Write("INFO", component, message);
    public static void Error(string component, string message, Exception? error = null) => Current.Write("ERROR", component, message, error);
}
