using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DamaCapture.Services;

public sealed class AppSettings
{
    public bool IncludeCursor { get; set; }
    public int DelaySeconds { get; set; }
    public int JpegQuality { get; set; } = 92;
    public string SaveFolder { get; set; } = DefaultSaveFolder;
    public bool CloseToTray { get; set; } = true;
    public bool KeepHistory { get; set; } = true;
    public bool KeepCaptureImages { get; set; } = true;
    public bool ArchiveClipboard { get; set; }
    public int HistoryVersion { get; set; }
    public bool ReduceMotion { get; set; }
    public int MaxHistory { get; set; } // Retained only for reading older settings; history has no count limit.
    public string AfterCapture { get; set; } = "editor";
    public bool DetectSensitive { get; set; } = true;
    public bool ReadCodes { get; set; } = true;
    /// <summary>Records older than this many days are deleted automatically; 0 keeps everything.</summary>
    public int HistoryRetentionDays { get; set; }
    /// <summary>Only this many newest records are kept; 0 keeps everything.</summary>
    public int HistoryRetentionCount { get; set; }
    public uint RegionHotkey { get; set; } = 0x31;
    public uint WindowHotkey { get; set; } = 0x32;
    public uint ScrollHotkey { get; set; } = 0x33;
    public uint HotkeyModifiers { get; set; } = 6; // Retained for older settings files; each hotkey now has its own.
    public uint RegionModifiers { get; set; } = DefaultModifiers;
    public uint WindowModifiers { get; set; } = DefaultModifiers;
    public uint ScrollModifiers { get; set; } = DefaultModifiers;
    /// <summary>File type for captures saved straight to the folder: "png" or "jpg".</summary>
    public string SaveFormat { get; set; } = "png";
    /// <summary>The user's own annotation color as #RRGGBB, or empty.</summary>
    public string CustomInk { get; set; } = "";

    public const uint PrintScreen = 0x2C;
    /// <summary>Ctrl+Shift, in RegisterHotKey's bits (Alt 1, Ctrl 2, Shift 4).</summary>
    public const uint DefaultModifiers = 6;
    /// <summary>F keys and PrtSc can stand alone; a letter or digit must not, or ordinary typing would trigger it.</summary>
    public static bool IsStandaloneKey(uint key) => key == PrintScreen || key is >= 0x70 and <= 0x7B;
    public static bool ValidHotkey(uint modifiers, uint key) =>
        ValidKey(key) && modifiers <= 7 && (IsStandaloneKey(key) || System.Numerics.BitOperations.PopCount(modifiers) >= 2);
    public static string KeyText(uint key) => key == PrintScreen ? "PrtSc" : key is >= 0x70 and <= 0x7B ? "F" + (key - 0x70 + 1) : ((char)key).ToString();
    public static string ModifierText(uint modifiers) =>
        string.Join("+", new[] { (2u, "Ctrl"), (1u, "Alt"), (4u, "Shift") }.Where(part => (modifiers & part.Item1) != 0).Select(part => part.Item2));
    public static string HotkeyText(uint modifiers, uint key) => modifiers == 0 ? KeyText(key) : ModifierText(modifiers) + "+" + KeyText(key);

    internal static string DefaultSaveFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } pictures
            ? pictures : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dama");

    internal static AppSettings Validated(AppSettings? settings)
    {
        settings ??= new AppSettings();
        string folder = DefaultSaveFolder;
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.SaveFolder) && Path.IsPathFullyQualified(settings.SaveFolder))
                folder = Path.GetFullPath(settings.SaveFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        // A key that is not allowed falls back to its default; a combination that is not allowed falls back to Ctrl+Shift.
        static (uint Modifiers, uint Key) Hotkey(uint modifiers, uint key, uint fallbackKey)
        {
            if (!ValidKey(key)) key = fallbackKey;
            return (ValidHotkey(modifiers, key) ? modifiers : DefaultModifiers, key);
        }
        var region = Hotkey(settings.RegionModifiers, settings.RegionHotkey, 0x31);
        var window = Hotkey(settings.WindowModifiers, settings.WindowHotkey, 0x32);
        var scroll = Hotkey(settings.ScrollModifiers, settings.ScrollHotkey, 0x33);
        return new AppSettings
        {
            IncludeCursor = settings.IncludeCursor,
            DelaySeconds = Math.Clamp(settings.DelaySeconds, 0, 30),
            JpegQuality = Math.Clamp(settings.JpegQuality, 1, 100),
            SaveFolder = folder,
            CloseToTray = settings.CloseToTray,
            KeepHistory = settings.KeepHistory,
            KeepCaptureImages = settings.KeepCaptureImages,
            ArchiveClipboard = settings.ArchiveClipboard,
            HistoryVersion = settings.HistoryVersion,
            ReduceMotion = settings.ReduceMotion,
            MaxHistory = 0,
            AfterCapture = settings.AfterCapture is "editor" or "copy" or "save" or "autosave" ? settings.AfterCapture : "editor",
            DetectSensitive = settings.DetectSensitive,
            ReadCodes = settings.ReadCodes,
            HistoryRetentionDays = Math.Clamp(settings.HistoryRetentionDays, 0, 3650),
            HistoryRetentionCount = Math.Clamp(settings.HistoryRetentionCount, 0, 100_000),
            RegionHotkey = region.Key, WindowHotkey = window.Key, ScrollHotkey = scroll.Key,
            RegionModifiers = region.Modifiers, WindowModifiers = window.Modifiers, ScrollModifiers = scroll.Modifiers,
            HotkeyModifiers = 6,
            SaveFormat = settings.SaveFormat == "jpg" ? "jpg" : "png",
            CustomInk = System.Text.RegularExpressions.Regex.IsMatch(settings.CustomInk ?? "", "^#[0-9A-Fa-f]{6}$") ? settings.CustomInk! : ""
        };
    }

    private static bool ValidKey(uint key) => key == PrintScreen || key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x7B;
}

public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string FilePath { get; }

    public SettingsStore(string? directory = null) => FilePath = Path.Combine(
        directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DamaCapture"),
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 65_536) return new AppSettings();
            return AppSettings.Validated(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // A damaged settings file must never prevent taking a capture. It is left intact until an explicit save.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AtomicJson.Write(FilePath, AppSettings.Validated(settings));
    }
}

internal static class AtomicJson
{
    public static void Write<T>(string path, T value)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".dama-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, SettingsStore.JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory rename commits a complete file; a failed write never truncates the existing settings.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
