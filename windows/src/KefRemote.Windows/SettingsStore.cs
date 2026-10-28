using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KefRemote.Windows;

public sealed class AppSettings
{
    public string DriverId { get; set; } = "kef-w2";
    public string Host { get; set; } = "";

    /// <summary>Device password protected with DPAPI for the current Windows user. Never stored in plain text.</summary>
    public string? ProtectedPassword { get; set; }

    public int AppVolumeLimit { get; set; } = 30;
    public bool StartWithWindows { get; set; }
    public int HotkeyVolumeStep { get; set; } = 1;
    public bool ShowVolumeOsd { get; set; } = true;

    /// <summary>Global shortcuts. Empty means the defaults; an entry with Key 0 means that action is turned off.</summary>
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    public string Password
    {
        get
        {
            if (string.IsNullOrEmpty(ProtectedPassword)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), null, DataProtectionScope.CurrentUser)); }
            catch (Exception e) when (e is CryptographicException or FormatException) { return ""; }
        }
        set => ProtectedPassword = string.IsNullOrEmpty(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }
}

/// <summary>Settings in %LOCALAPPDATA%\KefRemote\settings.json.</summary>
public static class SettingsStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Directory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KefRemote");
    static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
