namespace KefRemote.App;

/// <summary>Small diagnostic log next to the settings. Never contains the device password.</summary>
static class Log
{
    const long MaxBytes = 256 * 1024;
    static readonly object Gate = new();

    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KefRemote");

    public static string FilePath => Path.Combine(Folder, "log.txt");

    public static void Write(string message, Exception? error = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{(error != null ? Environment.NewLine + error : "")}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
