namespace KefRemote.App;

/// <summary>
/// <c>--demo</c>: the normal app against the simulated speakers of the self-test, with its own settings folder.
/// Unlike the self-test it keeps the real acrylic backdrop and keeps running with the flyout open, so screenshots
/// can be taken with the system's own tools. <c>--speakers 2</c> to <c>4</c> adds the other rooms.
/// </summary>
static class Demo
{
    static readonly (string Address, string? Finish)[] Rooms =
    [
        (DemoSpeaker.DeskAddress, "lsx-ii-lt/sage-green"),
        (DemoSpeaker.LivingAddress, "ls60-wireless/royal-blue"),
        (DemoSpeaker.BedroomAddress, "ls50-wireless-ii/mineral-white"),
        (DemoSpeaker.StudyAddress, null),
    ];

    public static HostEnvironment CreateEnvironment()
    {
        string folder = Path.Combine(Path.GetTempPath(), "RemoteForKEF-demo");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        var room = DemoSpeaker.Room();
        return new HostEnvironment(folder, o => DemoSpeaker.Driver(room, o), Demo: true);
    }

    public static async Task ShowAsync(AppHost host, int speakers)
    {
        foreach (var (address, finish) in Rooms.Take(Math.Clamp(speakers, 1, Rooms.Length)))
        {
            var slot = await host.AddSpeakerAsync(address, "");
            slot.Entry.Finish = finish;
        }
        host.SaveSettings();
        host.ShowFlyout();
    }
}
