using KefRemote.App.Native;
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
        var room = DemoSpeaker.Room();
        var speaker = room[DemoSpeaker.DeskAddress];
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
                new HostEnvironment(settingsFolder, o => DemoSpeaker.Driver(room, o), SelfTest: true));
            return Task.CompletedTask;
        });
        if (host == null)
        {
            Finish(folder, report, failures);
            return;
        }
        var c = host.Controller;
        // The first speaker's controller, kept while other speakers are added and selected.
        SpeakerController First() => host.Speakers[0].Controller;

        await Step("flyout, not set up", async () =>
        {
            host.ShowFlyout();
            await Capture(host.Flyout.Root, "01-flyout-setup.png");
        });
        await Step("connect", async () =>
        {
            await host.ApplyConnectionAsync(DemoSpeaker.DeskAddress, "");
            c = host.Controller;
            Expect(host.Speakers.Count == 1 && !host.HasSeveral, "one speaker");
            Expect(c.Phase == SpeakerPhase.On, "on after connect, got " + c.Phase);
            host.ShowFlyout();
            await Capture(host.Flyout.Root, "02-flyout-on.png");
        });
        // The popup windows are sized to their content, with no empty band below it.
        await Step("flyout fits its content", async () =>
        {
            await Task.Delay(300);
            var (window, content) = host.Flyout.Heights;
            Expect(Math.Abs(window - content) <= 2, $"flyout window {window:0.#} tall for content {content:0.#}");
        });
        // Same breathing room above the header as at the sides (16 DIPs).
        await Step("flyout top margin", () =>
        {
            double top = host.Flyout.IconTop;
            Expect(top >= 14, $"app icon {top:0.#} below the top of the flyout");
            return Task.CompletedTask;
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
            host.Speakers[0].Entry.ApproachFromAbove = true;
            host.SaveSettings();
            await c.SetVolumeTargetAsync(10);
            speaker.Writes.Clear();
            await c.SetVolumeTargetAsync(20);
            Expect(speaker.Writes.SequenceEqual([22, 20]), "writes 22 then 20, got " + string.Join(",", speaker.Writes));
            host.Speakers[0].Entry.ApproachFromAbove = false;
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
        // Wi-Fi carries track information: the card above the flyout shows it and takes play/pause.
        await Step("now playing", async () =>
        {
            await WaitFor(() => c.ShowsPlayback, "the track on wifi");
            Expect(host.MediaCard.IsVisible, "media card shown with the flyout");
            await Capture(host.MediaCard.Root, "30-media-card.png");
            await host.RunWhenIdle(() => c.PlaybackAsync(PlaybackCommand.TogglePause));
            Expect(speaker.Commands.SequenceEqual([PlaybackCommand.TogglePause]), "one play/pause");
            await WaitFor(() => c.Playback.Status == PlaybackStatus.Paused, "paused");
            await Capture(host.MediaCard.Root, "31-media-card-paused.png");
            host.Settings.ShowNowPlaying = false;
            host.SaveSettings();
            Expect(!host.MediaCard.IsVisible, "media card hidden when turned off");
            host.Settings.ShowNowPlaying = true;
            host.SaveSettings();
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
            var (window, content) = host.Osd.Heights;
            Expect(Math.Abs(window - content) <= 2, $"OSD window {window:0.#} tall for content {content:0.#}");
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
        await Step("speaker finish", async () =>
        {
            host.Speakers[0].Entry.Finish = "lsx-ii-lt/sage-green";
            host.SaveSettings();
            host.ShowFlyout();
            await Capture(host.Flyout.Root, "15-flyout-finish.png");
        });
        foreach (var (page, file) in new[]
                 {
                     (SettingsPage.General, "10-settings-general.png"),
                     (SettingsPage.Speaker, "11-settings-speaker.png"),
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
        await Step("unknown model", async () =>
        {
            speaker.ModelName = "SP4099";
            speaker.Release = "NEWMODEL_V0100";
            await host.ReconnectAsync();
            Expect(c.Device?.Model == "KEF NEWMODEL", "a model the app does not know");
            Expect(host.ShownModel == "KEF NEWMODEL" && host.Finish == null, "the app icon's drawing and colors");
            var settings = host.OpenSettings(SettingsPage.Speaker);
            await Capture(settings.Root, "16-settings-unknown-model.png");
        });
        int shot = 17;
        foreach (var model in KefModels.Names)
        {
            string file = $"{shot++}-flyout-{model.ToLowerInvariant().Replace(' ', '-')}.png";
            await Step("picked model " + model, async () =>
            {
                host.PickedModel = model;
                host.Speakers[0].Entry.Finish = KefFinishes.For(model)[0].Id;
                host.SaveSettings();
                Expect(host.ShownModel == model && host.Finish?.Model == model, "drawn as " + model);
                Expect(c.Driver?.Sources.Count == 7, "every input, as for any model the app does not know");
                host.ShowFlyout();
                await Capture(host.Flyout.Root, file);
            });
        }
        await Step("hotkeys registered", () =>
        {
            Expect(host.Hotkeys.Status.Count == 4, "four hotkey entries");
            Expect(host.Hotkeys.Status.GetValueOrDefault(HotkeyAction.NextSpeaker) == HotkeyRegistration.Off, "next speaker off by default");
            return Task.CompletedTask;
        });

        // ---------- several speakers ----------

        SpeakerSlot? desk = null, living = null, bedroom = null, study = null;
        await Step("back to the LSX II LT", async () =>
        {
            speaker.ModelName = "SP4077";
            speaker.Release = "LSXIILT_V2186";
            host.PickedModel = null;
            host.Speakers[0].Entry.Finish = "lsx-ii-lt/sage-green";
            host.SaveSettings();
            await host.ReconnectAsync();
            desk = host.Speakers[0];
            Expect(desk.Controller.Device?.Model == "LSX II LT", "LSX II LT again");
            Expect(desk.Entry.Name == desk.Controller.Device?.Name && desk.Entry.Model == "LSX II LT", "name and model kept for the list");
        });
        await Step("add speakers", async () =>
        {
            living = await host.AddSpeakerAsync(DemoSpeaker.LivingAddress, "");
            bedroom = await host.AddSpeakerAsync(DemoSpeaker.BedroomAddress, "");
            study = await host.AddSpeakerAsync(DemoSpeaker.StudyAddress, "");
            living.Entry.Finish = "ls60-wireless/royal-blue";
            bedroom.Entry.Finish = "ls50-wireless-ii/mineral-white";
            host.SaveSettings();
            Expect(host.Speakers.Count == 4 && host.HasSeveral, "four speakers");
            Expect(host.Selected == desk, "the first speaker stays selected");
            Expect(host.Controller == desk!.Controller, "the flyout works on the selected speaker");
            Expect(living.Controller.Phase == SpeakerPhase.On && bedroom.Controller.Phase == SpeakerPhase.Standby, "living on, bedroom in standby");
            Expect(study.Unreachable, "study unreachable");
            Expect(desk.Controller.ListenForChanges && !living.Controller.ListenForChanges, "only the selected speaker listens");
            Expect(!living.Controller.PlaybackEnabled, "only the selected speaker reads what is playing");
            Expect(host.Settings.Speakers?.Count == 4, "saved");
        });
        await Step("flyout with several speakers", async () =>
        {
            host.ShowFlyout();
            await Capture(host.Flyout.Root, "40-flyout-several.png");
        });
        await Step("speaker list", async () =>
        {
            host.Flyout.OpenSpeakerList();
            await WaitFor(() => host.Flyout.SpeakerListOpen, "the list to open");
            await host.RefreshListAsync();
            Expect(SpeakerPresentation.ListStatus(living!.Controller, living.Unreachable) == Loc.T("음량 18", "Volume 18", "音量 18"), "living room at 18");
            Expect(SpeakerPresentation.ListStatus(study!.Controller, study.Unreachable) == SpeakerPresentation.NotReachable, "study not connected");
            await Capture(host.Flyout.SpeakerListRoot, "41-speaker-list.png");
            await Capture(host.Flyout.Root, "42-flyout-list-open.png");
        });
        await Step("power from the list", async () =>
        {
            var livingRoom = room[DemoSpeaker.LivingAddress];
            await host.SetPowerAsync(living!, false);
            Expect(!livingRoom.On && host.Selected == desk, "living room off, desk still selected");
            await host.SetPowerAsync(living!, true);
            Expect(livingRoom.On, "living room back on");
            host.Flyout.CloseSpeakerList();
            await WaitFor(() => !host.Flyout.SpeakerListOpen, "the list to close");
        });
        await Step("select another speaker", async () =>
        {
            int deskVolume = speaker.Volume;
            await host.SelectAsync(living!);
            Expect(host.Selected == living && host.Controller == living!.Controller, "living room selected");
            Expect(host.Settings.SelectedSpeaker == living!.Id, "selection saved");
            Expect(living.Controller.ListenForChanges && !desk!.Controller.ListenForChanges, "listening moved with the selection");
            host.ShowFlyout();
            await Capture(host.Flyout.Root, "43-flyout-living-room.png");
            host.VolumeKeys.Handle(HotkeyAction.VolumeUp);
            await WaitFor(() => room[DemoSpeaker.LivingAddress].Volume == 19, "the key to raise the living room");
            Expect(speaker.Volume == deskVolume, "the desk untouched");
            await Task.Delay(300);
            await Capture(host.Osd.Root, "44-osd-speaker-name.png");
        });
        await Step("limit per speaker", async () =>
        {
            living!.Entry.AppVolumeLimit = 20;
            host.SaveSettings();
            await WaitFor(() => !living.Controller.Busy, "idle");
            await living.Controller.SetVolumeTargetAsync(60);
            Expect(room[DemoSpeaker.LivingAddress].Volume == 20, "living room capped at its own 20");
            Expect(desk!.Controller.EffectiveLimit == 30, "desk keeps 30");
        });
        await Step("next speaker shortcut", async () =>
        {
            host.SelectNext();
            await WaitFor(() => host.Selected == bedroom, "the bedroom to be next");
            host.SelectNext();
            await WaitFor(() => host.Selected == study, "the study to be next");
            host.SelectNext();
            await WaitFor(() => host.Selected == desk, "back to the desk");
        });
        await Step("settings, speaker list", async () =>
        {
            var settings = host.OpenSettings(SettingsPage.Speaker);
            await Capture(settings.Root, "45-settings-speakers.png");
            settings = host.OpenSettings(SettingsPage.Speaker, living);
            await Capture(settings.Root, "46-settings-speaker-page.png");
        });
        await Step("turn all off", async () =>
        {
            await host.TurnAllOffAsync();
            Expect(room.Values.Where(r => !r.Offline).All(r => !r.On), "every reachable speaker in standby");
            await host.RunWhenIdle(() => First().PowerOnAsync("usb"));
            Expect(speaker.On, "desk on again");
        });
        await Step("remove speakers", async () =>
        {
            host.RemoveSpeaker(study!);
            Expect(host.Speakers.Count == 3 && host.Settings.Speakers?.Count == 3, "three left");
            await host.SelectAsync(bedroom!);
            host.RemoveSpeaker(bedroom!);
            Expect(host.Selected == living, "the one before the removed selected speaker takes over");
            host.RemoveSpeaker(living!);
            Expect(host.Selected == desk && !host.HasSeveral, "back to one speaker");
            host.ShowFlyout();
            await Capture(host.Flyout.Root, "47-flyout-one-again.png");
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

/// <summary>
/// In-memory KEF speaker for the self-test and --demo. Power, inputs and volume behave like the real one, instantly.
/// The model it reports can be changed to one the app does not know.
/// </summary>
sealed class DemoSpeaker
{
    public const string DeskAddress = "192.168.0.20", LivingAddress = "192.168.0.21", BedroomAddress = "192.168.0.22", StudyAddress = "192.168.0.23";

    /// <summary>
    /// Four rooms: the LSX II LT on the desk (the speaker the app was tried on), an LS60 that is on, an LS50 Wireless II
    /// in standby and an LSX II that is unplugged.
    /// </summary>
    public static Dictionary<string, DemoSpeaker> Room() => new()
    {
        [DeskAddress] = new(),
        [LivingAddress] = new() { Name = Loc.T("거실 스피커", "Living room", "リビングのスピーカー"), ModelName = "SP4017", Release = "LS60_V2130", Source = "wifi", Volume = 18 },
        [BedroomAddress] = new() { Name = Loc.T("침실 스피커", "Bedroom", "寝室のスピーカー"), ModelName = "SP4025", Release = "LS50W2_V2130", On = false, Source = "tv", Volume = 15 },
        [StudyAddress] = new() { Name = Loc.T("서재 스피커", "Study", "書斎のスピーカー"), ModelName = "SP4041", Release = "LSXII_V2130", Offline = true },
    };

    public static ISpeakerDriver Driver(Dictionary<string, DemoSpeaker> room, DriverOptions options) =>
        new DemoDriver(room.TryGetValue(options.Host, out var speaker) ? speaker : new DemoSpeaker { Offline = true });

    public string Name = Loc.T("책상 스피커", "Desk speakers", "デスクのスピーカー");
    public bool On = true;
    public string Source = "usb";
    public int Volume = 25;
    public bool Muted;
    public bool Offline;
    public int SwitchDelayMs;
    public string ModelName = "SP4077";
    public string Release = "LSXIILT_V2186";
    public List<int> Writes { get; } = [];
    public PlaybackInfo Playing = new()
    {
        Status = PlaybackStatus.Playing,
        Title = Loc.T("조용한 오후", "Quiet Afternoon", "静かな午後"),
        Artist = "Remote for KEF",
        Service = "AirPlay",
        DurationMs = 214_000,
        PositionMs = 62_000,
        CanTogglePause = true,
        CanNext = true,
        CanPrevious = true,
    };
    public List<PlaybackCommand> Commands { get; } = [];
}

sealed class DemoDriver(DemoSpeaker speaker) : ISpeakerDriver
{
    KefModel Model => KefModels.Resolve(speaker.ModelName, speaker.Release);

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Volume | DriverCapabilities.Mute | DriverCapabilities.Source | DriverCapabilities.Power | DriverCapabilities.Playback;
    public IReadOnlyList<SourceOption> Sources => KefModels.SourceOptions(Model);

    void Online()
    {
        if (speaker.Offline) throw new HttpRequestException("demo speaker offline");
    }

    public Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken = default)
    {
        Online();
        return Task.FromResult(new DeviceInfo(speaker.Name, Model.DisplayName, speaker.Release));
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

    public bool SourceHasPlayback(string? sourceId) => sourceId is "wifi" or "bluetooth";

    public Task<PlaybackInfo> ReadPlaybackAsync(CancellationToken cancellationToken = default)
    {
        Online();
        return Task.FromResult(speaker.Playing);
    }

    public Task SendPlaybackAsync(PlaybackCommand command, CancellationToken cancellationToken = default)
    {
        Online();
        speaker.Commands.Add(command);
        if (command == PlaybackCommand.TogglePause)
            speaker.Playing = speaker.Playing with { Status = speaker.Playing.Status == PlaybackStatus.Playing ? PlaybackStatus.Paused : PlaybackStatus.Playing };
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
