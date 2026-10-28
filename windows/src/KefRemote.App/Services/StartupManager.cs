using Microsoft.Win32;

namespace KefRemote.App;

/// <summary>Per-user start after sign-in through HKCU\...\Run. Starts in the tray only.</summary>
static class StartupManager
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "RemoteForKEF";

    public static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value == Command;
        }
    }

    /// <summary>True when the executable sits somewhere that is likely to move or be cleaned up.</summary>
    public static bool IsUnstableLocation
    {
        get
        {
            string path = Environment.ProcessPath ?? "";
            string temp = Path.GetTempPath();
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || path.StartsWith(downloads, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
    }
}
