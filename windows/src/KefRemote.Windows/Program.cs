namespace KefRemote.Windows;

static class Program
{
    const string MutexName = @"Local\KefRemote.Instance";
    public const string ShowEventName = @"Local\KefRemote.Show";

    [STAThread]
    static void Main(string[] args)
    {
        bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        using var mutex = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            // Hand over to the running instance. A background start never shows a dialog.
            if (!background && EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) using (show) show.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
        Application.Run(new TrayApplicationContext(showWindow: !background));
    }
}
