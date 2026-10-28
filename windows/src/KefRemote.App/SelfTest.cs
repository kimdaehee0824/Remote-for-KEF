using KefRemote.App.Views;
using KefRemote.Core;
using KefRemote.Desktop;
using KefRemote.Drivers.Kef;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace KefRemote.App;

/// <summary>
/// <c>--self-test &lt;folder&gt;</c>: runs the real app against a simulated speaker, walks through every screen state,
/// saves a PNG of each and a report, then exits with 0 only if every step passed. Used by CI on Windows, where
/// nobody can look at the screen, and handy for screenshots.
/// </summary>
static class SelfTest
{
    public static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int failures = 0;
        var speaker = new DemoSpeaker();
        string settingsFolder = Path.Combine(folder, "settings");
        if (Directory.Exists(settingsFolder)) Directory.Delete(settingsFolder, recursive: true);

        AppHost? host = null;
        async Task Step(string name, Func<Task> body)
        {
            try
            {
                await body();
                report.Add("PASS " + name);
            }
            catch (Exception e)
            {
                failures++;
                report.Add($"FAIL {name}: {e.GetType().Name}: {e.Message}");
                Log.Write("self-test step failed: " + name, e);
            }
        }
        async Task Capture(FrameworkElement element, string file)
        {
            await Task.Delay(500);
            await SavePngAsync(element, Path.Combine(folder, file));
        }
        static void Expect(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("expected " + what);
        }
        // The app keeps polling in the background, so wait for states instead of assuming one read decides them.
        static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 8000)
        {
            for (int waited = 0; !condition(); waited += 50)
            {
                if (waited > timeoutMs) throw new TimeoutException("timed out waiting for " + what);
                await Task.Delay(50);
            }
        }

        await Step("start", () =>
        {
            host = new AppHost(new LaunchOptions(Background: true, SelfTestFolder: folder),
                new HostEnvironment(settingsFolder, _ => new DemoDriver(speaker), SelfTest: true));
            return Task.CompletedTask;
        });
        if (host == null)
        {
            Finish(folder, report, failures);
            return;
        }
        var c = host.Controller;

        await Step("flyout, not set up", async () =>
        {
            host.ShowFlyout();
            await Capture(host.Flyout.Root, "01-flyout-setup.png");
        });
        await Step("connect", async () =>
        {
            await host.ApplyConnectionAsync("192.168.0.20", "");
            Expect(c.Phase == SpeakerPhase.On, "on after connect, got " + c.Phase);
            host.ShowFlyout();
            await Capture(host.Flyout.Root, "02-flyout-on.png");
        });
        await Step("volume within limit", async () =>
        {
            await WaitFor(() => !c.Busy, "idle");
            await c.SetVolumeTargetAsync(28);
            Expect(speaker.Volume == 28, "volume 28, got " + speaker.Volume);
            await c.SetVolumeTargetAsync(60);
            Expect(speaker.Volume == 30, "capped at 30, got " + speaker.Volume);
            await Capture(host.Flyout.Root, "03-flyout-at-limit.png");
        });
        await Step("approach from above", async () =>
        {
            host.Settings.ApproachFromAbove = true;
            host.SaveSettings();
            await c.SetVolumeTargetAsync(10);
            speaker.Writes.Clear();
            await c.SetVolumeTargetAsync(20);
            Expect(speaker.Writes.SequenceEqual([22, 20]), "writes 22 then 20, got " + string.Join(",", speaker.Writes));
            host.Settings.ApproachFromAbove = false;
            host.SaveSettings();
        });
        await Step("input switch in progress", async () =>
        {
            speaker.SwitchDelayMs = 2500;
            var switching = host.RunWhenIdle(() => c.SelectSourceAsync("wifi"));
            await WaitFor(() => c.Operation == SpeakerOperation.Source, "switch to start");
            await Capture(host.Flyout.Root, "04-flyout-switching.png");
            await switching;
            speaker.SwitchDelayMs = 0;
            Expect(speaker.Source == "wifi" && !speaker.Muted, "wifi and unmuted");
        });
        await Step("osd", async () =>
        {
            host.VolumeKeys.Handle(HotkeyAction.VolumeUp);
            await Task.Delay(300);
            await Capture(host.Osd.Root, "05-osd.png");
            await c.SetVolumeTargetAsync(30);
            host.VolumeKeys.Handle(HotkeyAction.VolumeUp);
            await Task.Delay(300);
            await Capture(host.Osd.Root, "06-osd-limit.png");
        });
        await Step("notice", async () =>
        {
            speaker.Volume = 40;
            speaker.Muted = true;
            await WaitFor(() => c.State.Volume == 40 && c.State.Muted, "poll to see volume 40");
            await host.RunWhenIdle(c.ToggleMuteAsync);
            Expect(speaker.Muted && c.Notice != null, "still muted with a notice");
            await Capture(host.Flyout.Root, "07-flyout-notice.png");
            speaker.Volume = 20;
            speaker.Muted = false;
            await WaitFor(() => c.State.Volume == 20 && !c.State.Muted, "poll to see volume 20");
            c.ClearNotice();
        });
        await Step("power off", async () =>
        {
            await host.RunWhenIdle(c.PowerOffAsync);
            Expect(c.Phase == SpeakerPhase.Standby, "standby, got " + c.Phase);
            await Capture(host.Flyout.Root, "08-flyout-standby.png");
        });
        await Step("power on to input", async () =>
        {
            await host.RunWhenIdle(() => c.PowerOnAsync("usb"));
            Expect(c.Phase == SpeakerPhase.On && speaker.Source == "usb", "on with usb");
        });
        await Step("offline", async () =>
        {
            speaker.Offline = true;
            await WaitFor(() => c.Phase == SpeakerPhase.Offline, "offline");
            await Capture(host.Flyout.Root, "09-flyout-offline.png");
            speaker.Offline = false;
            await host.ReconnectAsync();
            Expect(c.Phase == SpeakerPhase.On, "back on");
        });
        foreach (var (page, file) in new[]
                 {
                     (SettingsPage.General, "10-settings-general.png"),
                     (SettingsPage.Speaker, "11-settings-speaker.png"),
                     (SettingsPage.Volume, "12-settings-volume.png"),
                     (SettingsPage.Hotkeys, "13-settings-hotkeys.png"),
                     (SettingsPage.About, "14-settings-about.png"),
                 })
        {
            await Step("settings " + page, async () =>
            {
                var settings = host.OpenSettings(page);
                await Capture(settings.Root, file);
            });
        }
        await Step("hotkeys registered", () =>
        {
            Expect(host.Hotkeys.Status.Count == 3, "three hotkey entries");
            return Task.CompletedTask;
        });

        Finish(folder, report, failures);
        host.Exit();
    }

    static void Finish(string folder, List<string> report, int failures)
    {
        report.Add(failures == 0 ? "OK" : $"{failures} FAILED");
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        App.ExitCode = failures == 0 ? 0 : 1;
        if (failures > 0 && Application.Current != null) Application.Current.Exit();
    }

    static async Task SavePngAsync(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        using (var reader = DataReader.FromBuffer(pixels))
        {
            var bytes = new byte[pixels.Length];
            reader.ReadBytes(bytes);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        }
        await encoder.FlushAsync();
        stream.Seek(0);
        using var file = File.Create(path);
        using var input = stream.GetInputStreamAt(0);
        using var reader2 = new DataReader(input);
        uint size = (uint)stream.Size;
        await reader2.LoadAsync(size);
        var data = new byte[size];
        reader2.ReadBytes(data);
        await file.WriteAsync(data);
    }
}

/// <summary>In-memory LSX II LT for the self-test. Power, inputs and volume behave like the real one, instantly.</summary>
sealed class DemoSpeaker
{
    public bool On = true;
    public string Source = "usb";
    public int Volume = 25;
    public bool Muted;
    public bool Offline;
    public int SwitchDelayMs;
    public List<int> Writes { get; } = [];
}

sealed class DemoDriver(DemoSpeaker speaker) : ISpeakerDriver
{
    static readonly KefModel Model = KefModels.Resolve("SP4077", "LSXIILT_V2186");

    public DriverCapabilities Capabilities => DriverCapabilities.Volume | DriverCapabilities.Mute | DriverCapabilities.Source | DriverCapabilities.Power;
    public IReadOnlyList<SourceOption> Sources { get; } = KefModels.SourceOptions(Model);

    void Online()
    {
        if (speaker.Offline) throw new HttpRequestException("demo speaker offline");
    }

    public Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken = default)
    {
        Online();
        return Task.FromResult(new DeviceInfo("책상 스피커", Model.DisplayName, "LSXIILT_V2186"));
    }

    public Task<SpeakerState> ReadAsync(CancellationToken cancellationToken = default)
    {
        Online();
        return Task.FromResult(new SpeakerState
        {
            Power = speaker.On ? PowerState.On : PowerState.Standby,
            SourceId = speaker.On ? speaker.Source : null,
            Volume = speaker.Volume,
            Muted = speaker.Muted,
            Valid = true,
        });
    }

    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
    {
        Online();
        speaker.Volume = volume;
        speaker.Writes.Add(volume);
        return Task.CompletedTask;
    }

    public Task SetMuteAsync(bool muted, CancellationToken cancellationToken = default)
    {
        Online();
        speaker.Muted = muted;
        return Task.CompletedTask;
    }

    public async Task SetSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        Online();
        if (speaker.SwitchDelayMs > 0) await Task.Delay(speaker.SwitchDelayMs, cancellationToken);
        speaker.Source = sourceId;
    }

    public Task SetPowerAsync(bool on, CancellationToken cancellationToken = default)
    {
        Online();
        speaker.On = on;
        return Task.CompletedTask;
    }

    public int? ApproachOvershoot(int current, int target) => target > current ? KefVolumeCurve.Overshoot(target) : null;

    public void Dispose()
    {
    }
}
