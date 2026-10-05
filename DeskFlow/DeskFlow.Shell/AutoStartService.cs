using System.IO;
using Microsoft.Win32;
using DeskFlow.Core.Services;

namespace DeskFlow.Shell;

/// <summary>开机自启：写入当前用户的 Run 项，不需要管理员权限。</summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskFlow";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("AutoStart", "Cannot read startup setting", error);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{ExecutablePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("AutoStart", "Cannot update startup setting", error);
        }
    }

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DeskFlow.exe");
}
