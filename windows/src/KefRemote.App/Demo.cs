namespace KefRemote.App;

/// <summary>
/// <c>--demo</c>: the normal app against the simulated speaker of the self-test, with its own settings folder.
/// Unlike the self-test it keeps the real acrylic backdrop and keeps running with the flyout open, so screenshots
/// can be taken with the system's own tools.
/// </summary>
static class Demo
{
    public static HostEnvironment CreateEnvironment()
    {
        string folder = Path.Combine(Path.GetTempPath(), "RemoteForKEF-demo");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        var speaker = new DemoSpeaker();
        return new HostEnvironment(folder, _ => new DemoDriver(speaker), Demo: true);
    }

    public static async Task ShowAsync(AppHost host)
    {
        await host.ApplyConnectionAsync("192.168.0.20", "");
        host.ShowFlyout();
    }
}
