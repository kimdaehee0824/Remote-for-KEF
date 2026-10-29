using KefRemote.App.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace KefRemote.App;

static class Program
{
    const string MutexName = @"Local\KefRemote.Instance";
    public const string ShowEventName = @"Local\KefRemote.Show";

    /// <summary>
    /// Arguments:
    ///   --background            start in the tray without opening anything (used by "start with Windows")
    ///   --self-test &lt;folder&gt;   render every screen with a simulated speaker, save PNGs there and exit
    ///   --demo                  run against a simulated speaker with the flyout open, for screenshots
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);

        string mutexName = options.SelfTestFolder != null ? MutexName + ".SelfTest" : options.Demo ? MutexName + ".Demo" : MutexName;
        using var mutex = new Mutex(true, mutexName, out bool first);
        if (!first)
        {
            // Already running: let that instance take the foreground and show itself, then leave.
            Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(options);
        });
        return App.ExitCode;
    }
}

sealed record LaunchOptions(bool Background, string? SelfTestFolder, bool Demo = false)
{
    public static LaunchOptions Parse(string[] args)
    {
        bool background = false, demo = false;
        string? selfTest = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--background":
                    background = true;
                    break;
                case "--self-test":
                    selfTest = i + 1 < args.Length ? Path.GetFullPath(args[++i]) : Path.Combine(AppContext.BaseDirectory, "self-test");
                    break;
                case "--demo":
                    demo = true;
                    break;
            }
        }
        return new LaunchOptions(background, selfTest, demo);
    }
}
