using System;
using System.IO;
using Microsoft.Win32;

namespace DamaCapture.Services;

/// <summary>
/// Starts the app in the tray when the user signs in to Windows, through the per-user Run entry. Hotkeys only work
/// while the app is running, so this is what makes them available after a restart. Only this user's own entry is
/// touched: when the setting is changed, and to follow the app when an existing entry points at an older copy.
/// </summary>
public static class StartupRegistration
{
    public const string TrayArgument = "--tray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "DamaCapture";

    /// <summary>The command Windows should run at sign-in, or null when this process is not the app's own executable (a development host).</summary>
    public static string? Command(string? processPath = null)
    {
        processPath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !string.Equals(Path.GetFileNameWithoutExtension(processPath), "DamaCapture", StringComparison.OrdinalIgnoreCase)) return null;
        return $"\"{processPath}\" {TrayArgument}";
    }

    public static bool IsEnabled(string keyPath = RunKey, string valueName = ValueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) is string value && value.Length > 0;
    }

    public static void Set(bool enabled, string? command, string keyPath = RunKey, string valueName = ValueName)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath) ?? throw new InvalidOperationException("시작 프로그램 항목을 열 수 없습니다.");
        if (!enabled) { key.DeleteValue(valueName, throwOnMissingValue: false); return; }
        if (command == null) throw new InvalidOperationException("배포된 DamaCapture.exe에서만 시작 프로그램으로 등록할 수 있습니다.");
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    /// <summary>An entry left by an older copy is pointed at the copy that is running now, so an update keeps starting.</summary>
    public static void Refresh(string? command, string keyPath = RunKey, string valueName = ValueName)
    {
        if (command == null) return;
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key?.GetValue(valueName) is string current && current.Length > 0 && !string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
            key.SetValue(valueName, command, RegistryValueKind.String);
    }
}
