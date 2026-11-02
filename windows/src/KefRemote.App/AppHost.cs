using KefRemote.App.Native;
using KefRemote.App.Views;
using KefRemote.Core;
using KefRemote.Desktop;
using KefRemote.Drivers;
using KefRemote.Drivers.Kef;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace KefRemote.App;

/// <param name="SettingsFolder">Where settings.json and the log live.</param>
/// <param name="CreateDriver">Replaces the built-in drivers (self-test).</param>
/// <param name="SelfTest">Screens stay open without focus and use solid backgrounds so they can be captured.</param>
/// <param name="Demo">The flyout stays open without focus, with its real backdrop (--demo).</param>
sealed record HostEnvironment(string SettingsFolder, Func<DriverOptions, ISpeakerDriver>? CreateDriver = null, bool SelfTest = false, bool Demo = false)
{
    public static HostEnvironment Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KefRemote"));
}

/// <summary>
/// One registered speaker while the app runs: its saved entry, its own controller and how connecting to it went.
/// Only the selected speaker listens for change reports and is polled; the others are read when the speaker list
/// opens, so a house full of speakers costs no more network traffic than one.
/// </summary>
sealed class SpeakerSlot(SpeakerEntry entry)
{
    public SpeakerEntry Entry { get; } = entry;
    public SpeakerController Controller { get; } = new() { ListenForChanges = false, PlaybackEnabled = false };
    public string Id => Entry.Id;

    /// <summary>The name for lists: what the speaker calls itself, else the last name it reported, else its address.</summary>
    public string Name => Controller.Device?.Name is { Length: > 0 } name ? name : SpeakerList.DisplayName(Entry);

    public bool Connecting { get; set; }

    /// <summary>The address can't be used (not a private IPv4 address). Retrying won't help until it is changed.</summary>
    public bool ConfigurationError { get; set; }

    /// <summary>Consecutive failed connection attempts.</summary>
    public int ConnectFailures { get; set; }

    public long LastDeviceRefresh { get; set; }

    /// <summary>The model the speaker is drawn as and whose finishes are listed (<see cref="KefModels.Shown"/>).</summary>
    public string? ShownModel => KefModels.Shown(Controller.Device?.Model ?? Entry.Model, Entry.PickedModel);

    /// <summary>The picked finish, if it is one of the shown model's.</summary>
    public KefFinish? Finish => KefFinishes.Find(Entry.Finish, ShownModel);

    /// <summary>Not reachable for now, as opposed to not tried yet.</summary>
    public bool Unreachable => ConfigurationError || ConnectFailures > 0 || Controller.Phase == SpeakerPhase.Offline;
}

/// <summary>
/// Application lifetime: owns the registered speakers, settings, tray icon, hotkeys and the surfaces
/// (tray flyout with its media card and speaker list, volume display, settings window). Everything runs on the UI
/// thread. The flyout, tray, shortcuts and volume display all work on the selected speaker.
/// </summary>
sealed class AppHost : IDisposable
{
    static readonly int[] ReconnectBackoff = [2000, 5000, 10000, 30000];
    const int DeviceRefreshMs = 30000, FreshOnOpenMs = 1000, IdleWaitMs = 3000, ListPollMs = 3000;

    readonly HostEnvironment environment;
    readonly SettingsStore store;
    readonly DispatcherQueue queue;
    readonly DispatcherQueueTimer pollTimer;
    readonly DispatcherQueueTimer listTimer;
    readonly DispatcherQueueTimer updateTimer;
    readonly long startedAt = Environment.TickCount64;
    readonly MessageWindow messages;
    readonly TrayIcon tray;
    readonly HotkeyVolumeCoordinator volumeKeys;
    readonly EventWaitHandle? showEvent;
    readonly RegisteredWaitHandle? showWait;
    readonly HashSet<DispatcherQueueTimer> laterTimers = [];
    readonly FlyoutView flyout;
    readonly OsdView osd;
    readonly MediaCardView mediaCard;
    readonly List<SpeakerSlot> slots = [];
    // Stands in while no speaker is registered, so the surfaces always have a controller to show.
    readonly SpeakerController none = new() { ListenForChanges = false, PlaybackEnabled = false };
    SpeakerSlot? selected;
    SettingsView? settingsWindow;
    bool listOpen;
    bool hotkeyWarningShown;
    bool exiting;

    public AppSettings Settings { get; }

    /// <summary>The selected speaker's controller.</summary>
    public SpeakerController Controller => selected?.Controller ?? none;

    /// <summary>The registered speakers, in the order they were added.</summary>
    public IReadOnlyList<SpeakerSlot> Speakers => slots;
    public SpeakerSlot? Selected => selected;

    /// <summary>More than one speaker: the surfaces say which one they work on and offer to switch.</summary>
    public bool HasSeveral => slots.Count > 1;

    /// <summary>The model picked in settings for the selected speaker, or null.</summary>
    public string? PickedModel
    {
        get => selected?.Entry.PickedModel;
        set
        {
            if (selected != null) selected.Entry.PickedModel = value;
        }
    }

    public string? ShownModel => selected?.ShownModel;
    public KefFinish? Finish => selected?.Finish;
    public HotkeyManager Hotkeys { get; }
    public AppUpdater Updates { get; }

    /// <summary>Failed connection attempts of the selected speaker. The flyout shows "offline" instead of "connecting" while above 0.</summary>
    public int ConnectFailures => selected?.ConnectFailures ?? 0;

    public bool IsConnecting => selected?.Connecting ?? false;
    public bool ConfigurationError => selected?.ConfigurationError ?? false;
    public bool SelfTest => environment.SelfTest;
    public bool Demo => environment.Demo;
    public string SettingsFolder => store.Directory;
    public IReadOnlyList<HotkeyBinding> HotkeyBindings => HotkeyBinding.Normalize(Settings.Hotkeys);
    public FlyoutView Flyout => flyout;
    public OsdView Osd => osd;
    public MediaCardView MediaCard => mediaCard;
    public HotkeyVolumeCoordinator VolumeKeys => volumeKeys;

    /// <summary>Controller state, settings or hotkey registration changed.</summary>
    public event Action? Changed;

    public AppHost(LaunchOptions options, HostEnvironment? env = null)
    {
        environment = env ?? HostEnvironment.Default();
        Log.Folder = environment.SettingsFolder;
        Log.Write($"started {typeof(AppHost).Assembly.GetName().Version}{(environment.SelfTest ? " (self-test)" : "")}, Windows {Environment.OSVersion.Version}");
        store = new SettingsStore(environment.SettingsFolder);
        Settings = store.Load();
        if (SpeakerList.Migrate(Settings)) store.Save(Settings);
        queue = DispatcherQueue.GetForCurrentThread();

        foreach (var entry in SpeakerList.All(Settings)) slots.Add(CreateSlot(entry));
        selected = Find(SpeakerList.Selected(Settings)?.Id);
        if (selected != null) selected.Controller.ListenForChanges = true;

        messages = new MessageWindow();
        tray = new TrayIcon(messages, queue);
        Hotkeys = new HotkeyManager(messages);

        osd = new OsdView(this);
        flyout = new FlyoutView(this);
        flyout.VisibilityChanged += OnFlyoutVisibilityChanged;
        mediaCard = new MediaCardView(this);
        tray.Activated += (x, y) => flyout.Toggle(TrayAnchor(new PointInt32(x, y)));

        volumeKeys = new HotkeyVolumeCoordinator(
            snapshot: () => new VolumeSnapshot(
                Ready: Controller.IsReady && Controller.Supports(DriverCapabilities.Volume),
                Volume: Controller.State.Volume,
                Muted: Controller.State.Muted,
                Limit: Controller.EffectiveLimit,
                Status: Controller.IsReady ? Controller.Notice : Controller.Status),
            setVolumeTarget: target => Controller.SetVolumeTargetAsync(target),
            setMute: muted => Controller.SetMuteAsync(muted),
            controllerBusy: () => Controller.Busy,
            osd: osd,
            runLater: RunLater);
        osd.Input += volumeKeys.Handle;

        Hotkeys.Pressed += action =>
        {
            if (action == HotkeyAction.NextSpeaker) SelectNext();
            else volumeKeys.Handle(action);
        };
        Hotkeys.StatusChanged += () => Changed?.Invoke();
        ApplyPreferences();
        ApplyHotkeys();

        pollTimer = queue.CreateTimer();
        pollTimer.IsRepeating = false;
        pollTimer.Tick += async (_, _) => await PollTickAsync();
        listTimer = queue.CreateTimer();
        listTimer.IsRepeating = false;
        listTimer.Tick += async (_, _) =>
        {
            listTimer.Stop();
            await RefreshListAsync();
        };

        Updates = environment.SelfTest || environment.Demo ? AppUpdater.ForScreens(queue) : new AppUpdater(queue, enabled: true);
        Updates.Changed += () => Changed?.Invoke();
        Updates.ReadyInBackground += OnUpdateReady;
        updateTimer = queue.CreateTimer();
        updateTimer.IsRepeating = false;
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Stop();
            await CheckForUpdatesAsync(userInitiated: false);
        };

        if (!environment.SelfTest)
        {
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            showWait = ThreadPool.RegisterWaitForSingleObject(showEvent,
                (_, _) => queue.TryEnqueue(ShowFromLaunch), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        UpdateTray();
        if (!options.Background) ShowFromLaunch();
        _ = ConnectAsync();

        if (Hotkeys.AnyInUse) WarnHotkeysOnce();
        if (Program.UpdatedTo is string updatedTo && !environment.SelfTest && !environment.Demo)
        {
            tray.ShowBalloon(Loc.T("Remote for KEF 업데이트 완료", "Remote for KEF updated", "Remote for KEFをアップデートしました"),
                Loc.T($"{updatedTo} 버전으로 업데이트했습니다.", $"Now on version {updatedTo}.", $"バージョン {updatedTo} にアップデートしました。"));
        }
        ScheduleUpdateCheck();
    }

    // ---------- speakers ----------

    SpeakerSlot CreateSlot(SpeakerEntry entry)
    {
        var slot = new SpeakerSlot(entry);
        slot.Controller.AppVolumeLimit = entry.AppVolumeLimit;
        slot.Controller.ApproachFromAbove = entry.ApproachFromAbove;
        slot.Controller.Changed += () => OnControllerChanged(slot);
        slot.Controller.LastActiveSourceChanged += source => OnLastActiveSourceChanged(slot, source);
        return slot;
    }

    public SpeakerSlot? Find(string? id) => slots.FirstOrDefault(s => s.Id == id);

    /// <summary>Makes <paramref name="slot"/> the speaker the flyout, tray and shortcuts work on.</summary>
    public Task SelectAsync(SpeakerSlot slot)
    {
        if (slot == selected || !slots.Contains(slot)) return Task.CompletedTask;
        if (selected != null)
        {
            selected.Controller.ListenForChanges = false;
            selected.Controller.PlaybackEnabled = false;
        }
        selected = slot;
        Settings.SelectedSpeaker = slot.Id;
        store.Save(Settings);
        slot.Controller.ListenForChanges = true;
        slot.Controller.PlaybackEnabled = Settings.ShowNowPlaying;
        volumeKeys.SpeakerChanged();
        volumeKeys.Tolerance = slot.Controller.Driver?.VolumeTolerance ?? 0;
        flyout.SpeakerChanged();
        UpdateTray();
        // Polling moves to this speaker; reading it now below comes first when it is stale.
        ScheduleNextPoll();
        Changed?.Invoke();
        Log.Write("selected speaker " + slot.Id);
        return slot.Controller.Device == null ? ConnectAsync(slot) : RefreshNowAsync();
    }

    /// <summary>The "next speaker" shortcut: switches and says so in the volume display.</summary>
    public async void SelectNext()
    {
        if (SpeakerList.Next(Settings) is not { } entry || Find(entry.Id) is not { } next) return;
        var refresh = SelectAsync(next);
        bool known = next.Controller.IsReady;
        ShowSelectedInOsd();
        await refresh;
        // Read for the first time just now: show the volume it is at.
        if (!known && selected == next && next.Controller.IsReady) ShowSelectedInOsd();
    }

    void ShowSelectedInOsd()
    {
        var c = Controller;
        // Shown even with the volume display turned off: it is the only sign that the shortcut did something.
        osd.ShowLevel(new OsdContent(
            Volume: c.IsReady ? c.State.Volume : null,
            Muted: c.State.Muted,
            Limit: c.EffectiveLimit,
            Pending: false,
            Message: c.IsReady || c.IsStandby ? null : SpeakerPresentation.ListStatus(c, selected?.Unreachable ?? false),
            IsError: false));
    }

    /// <summary>Registers a speaker and connects to it. The first one becomes the selected one.</summary>
    public async Task<SpeakerSlot> AddSpeakerAsync(string address, string password)
    {
        var entry = SpeakerList.Add(Settings, address, Secrets.Protect(password));
        store.Save(Settings);
        var slot = CreateSlot(entry);
        slots.Add(slot);
        Log.Write($"added speaker {entry.Id}, {slots.Count} registered");
        if (selected == null)
        {
            selected = slot;
            slot.Controller.ListenForChanges = true;
            slot.Controller.PlaybackEnabled = Settings.ShowNowPlaying;
            Settings.SelectedSpeaker = slot.Id;
            store.Save(Settings);
        }
        Changed?.Invoke();
        await ConnectAsync(slot);
        return slot;
    }

    /// <summary>Saves a new address and password for a speaker and reconnects at once.</summary>
    public Task UpdateConnectionAsync(SpeakerSlot slot, string address, string password)
    {
        slot.Entry.Host = address.Trim();
        slot.Entry.ProtectedPassword = Secrets.Protect(password);
        store.Save(Settings);
        slot.ConnectFailures = 0;
        slot.ConfigurationError = false;
        return ConnectAsync(slot);
    }

    /// <summary>Saves a new address and password for the selected speaker, or adds the first one.</summary>
    public Task ApplyConnectionAsync(string address, string password) =>
        selected == null ? AddSpeakerAsync(address, password) : UpdateConnectionAsync(selected, address, password);

    /// <summary>Forgets a speaker. When it was the selected one, the one before it takes over.</summary>
    public void RemoveSpeaker(SpeakerSlot slot)
    {
        if (!SpeakerList.Remove(Settings, slot.Id)) return;
        slots.Remove(slot);
        store.Save(Settings);
        Log.Write($"removed speaker {slot.Id}, {slots.Count} registered");
        slot.Controller.Dispose();
        if (selected == slot)
        {
            selected = null;
            volumeKeys.SpeakerChanged();
            pollTimer.Stop();
            if (Find(SpeakerList.Selected(Settings)?.Id) is { } next)
            {
                _ = SelectAsync(next);
                return;
            }
            none.Disconnect(EnterAddress);
            flyout.SpeakerChanged();
            UpdateTray();
        }
        Changed?.Invoke();
    }

    static string EnterAddress => Loc.T("스피커 주소를 입력하세요", "Enter the speaker address", "スピーカーのアドレスを入力してください");

    // ---------- connection ----------

    /// <summary>Connects to the selected speaker.</summary>
    public Task ConnectAsync()
    {
        if (selected != null) return ConnectAsync(selected);
        none.Disconnect(EnterAddress);
        return Task.CompletedTask;
    }

    public async Task ConnectAsync(SpeakerSlot slot)
    {
        if (slot.Connecting) return;
        var c = slot.Controller;
        if (!slot.Entry.IsConfigured)
        {
            c.Disconnect(EnterAddress);
            return;
        }
        slot.Connecting = true;
        if (slot == selected) pollTimer.Stop();
        try
        {
            ISpeakerDriver driver;
            try
            {
                var driverOptions = new DriverOptions(HostAddress.ParseLocal(slot.Entry.Host), Secrets.Unprotect(slot.Entry.ProtectedPassword));
                driver = environment.CreateDriver?.Invoke(driverOptions) ?? BuiltInDrivers.Find(Settings.DriverId).Create(driverOptions);
            }
            catch (SpeakerException e)
            {
                // A bad address will not fix itself; wait for the user to change the settings.
                slot.ConfigurationError = true;
                c.Disconnect(e.Message);
                return;
            }
            slot.ConfigurationError = false;
            bool ok = await c.ConnectAsync(driver, slot.Entry.LastInput);
            slot.ConnectFailures = ok ? 0 : slot.ConnectFailures + 1;
            if (ok) slot.LastDeviceRefresh = Environment.TickCount64;
        }
        finally
        {
            slot.Connecting = false;
            if (slot == selected) ScheduleNextPoll();
            Changed?.Invoke();
        }
    }

    /// <summary>"Try again" from the flyout or settings: skip the backoff.</summary>
    public Task ReconnectAsync() => selected == null ? Task.CompletedTask : ReconnectAsync(selected);

    public Task ReconnectAsync(SpeakerSlot slot)
    {
        slot.ConnectFailures = 0;
        return ConnectAsync(slot);
    }

    async Task PollTickAsync()
    {
        pollTimer.Stop();
        if (selected is { } slot && !slot.Connecting && !exiting)
        {
            if (slot.Controller.Driver == null || slot.Controller.Device == null)
            {
                if (slot.Entry.IsConfigured && !slot.ConfigurationError) await ConnectAsync(slot);
            }
            else
            {
                await slot.Controller.PollAsync();
            }
        }
        ScheduleNextPoll();
    }

    void ScheduleNextPoll()
    {
        pollTimer.Stop();
        if (exiting || selected is not { } slot) return;
        bool visible = flyout.IsVisible || settingsWindow != null;
        TimeSpan delay = slot.Controller.Device == null
            ? TimeSpan.FromMilliseconds(ReconnectBackoff[Math.Clamp(slot.ConnectFailures - 1, 0, ReconnectBackoff.Length - 1)])
            : slot.Controller.NextPollDelay(visible);
        pollTimer.Interval = delay;
        pollTimer.Start();
    }

    /// <summary>Read now when the numbers on screen might be old, e.g. when the flyout opens.</summary>
    public async Task RefreshNowAsync()
    {
        if (selected is not { } slot) return;
        var c = slot.Controller;
        if (slot.Connecting || c.Busy || c.Device == null) return;
        if (Environment.TickCount64 - slot.LastDeviceRefresh > DeviceRefreshMs)
        {
            // Picks up a maximum volume changed in KEF Connect. Read only.
            slot.LastDeviceRefresh = Environment.TickCount64;
            await c.RefreshDeviceAsync();
        }
        if (c.Busy || selected != slot) return;
        long age = c.Clock() - c.State.SeenAt;
        if (!c.State.Valid || age > FreshOnOpenMs)
        {
            pollTimer.Stop();
            await c.PollAsync();
            if (selected == slot) ScheduleNextPoll();
        }
    }

    /// <summary>
    /// The speaker list opened or closed. While it is open the speakers that are not selected are read every few
    /// seconds so their rows stay true; while it is closed they are left alone.
    /// </summary>
    public void SetListOpen(bool open)
    {
        listOpen = open;
        listTimer.Stop();
        if (open) _ = RefreshListAsync();
    }

    /// <summary>Connects to or reads every speaker but the selected one, which is kept up to date anyway.</summary>
    public async Task RefreshListAsync()
    {
        listTimer.Stop();
        var others = slots.Where(s => s != selected).ToList();
        await Task.WhenAll(others.Select(RefreshOtherAsync));
        if (listOpen && !exiting)
        {
            listTimer.Interval = TimeSpan.FromMilliseconds(ListPollMs);
            listTimer.Start();
        }
    }

    /// <summary>Reads one speaker now, e.g. when its settings page opens: the device too, for a changed maximum volume.</summary>
    public async Task RefreshSpeakerAsync(SpeakerSlot slot)
    {
        if (slot == selected)
        {
            if (slot.Controller.Device == null) await ConnectAsync(slot);
            else await RefreshNowAsync();
            return;
        }
        var c = slot.Controller;
        if (c.Device != null && !c.Busy)
        {
            slot.LastDeviceRefresh = Environment.TickCount64;
            await c.RefreshDeviceAsync();
        }
        await RefreshOtherAsync(slot);
    }

    async Task RefreshOtherAsync(SpeakerSlot slot)
    {
        var c = slot.Controller;
        if (slot.Connecting || slot.ConfigurationError || c.Busy) return;
        if (c.Driver == null || c.Device == null) await ConnectAsync(slot);
        else await c.PollAsync();
    }

    /// <summary>The power switch of a row in the speaker list. Leaves the selection as it is.</summary>
    public async Task SetPowerAsync(SpeakerSlot slot, bool on)
    {
        var c = slot.Controller;
        if (c.Device == null) await ConnectAsync(slot);
        await RunWhenIdle(c, () => on ? c.PowerOnAsync() : c.PowerOffAsync());
    }

    /// <summary>"Turn all off": puts every speaker that is on in standby, the ones not read yet included.</summary>
    public async Task TurnAllOffAsync()
    {
        await Task.WhenAll(slots.ToList().Select(async slot =>
        {
            var c = slot.Controller;
            if (c.Device == null && !slot.ConfigurationError) await ConnectAsync(slot);
            if (c.Phase == SpeakerPhase.On && c.Supports(DriverCapabilities.Power)) await RunWhenIdle(c, c.PowerOffAsync);
        }));
    }

    /// <summary>
    /// Runs a command from a click. A background read makes the controller drop commands, so wait for it briefly
    /// instead of losing the click. Another running command still wins.
    /// </summary>
    public Task RunWhenIdle(Func<Task> command) => RunWhenIdle(Controller, command);

    public async Task RunWhenIdle(SpeakerController c, Func<Task> command)
    {
        for (int waited = 0; c.Busy && c.Operation == SpeakerOperation.None; waited += 40)
        {
            if (waited > IdleWaitMs) return;
            await Task.Delay(40);
        }
        if (c.Busy) return;
        try
        {
            await command();
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    // ---------- settings ----------

    public void SaveSettings()
    {
        ApplyPreferences();
        store.Save(Settings);
        Changed?.Invoke();
    }

    void ApplyPreferences()
    {
        foreach (var slot in slots)
        {
            slot.Entry.AppVolumeLimit = Math.Clamp(slot.Entry.AppVolumeLimit, 1, 100);
            slot.Controller.AppVolumeLimit = slot.Entry.AppVolumeLimit;
            slot.Controller.ApproachFromAbove = slot.Entry.ApproachFromAbove;
            slot.Controller.PlaybackEnabled = slot == selected && Settings.ShowNowPlaying;
        }
        volumeKeys.Step = Settings.HotkeyVolumeStep;
        volumeKeys.OsdEnabled = Settings.ShowVolumeOsd;
    }

    /// <summary>Registers the saved shortcuts. Returns the outcome per action.</summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistration> ApplyHotkeys() => Hotkeys.Apply(HotkeyBindings);

    public void SetStartWithWindows(bool enabled)
    {
        if (!environment.SelfTest) StartupManager.Set(enabled);
        Settings.StartWithWindows = enabled;
        SaveSettings();
    }

    // ---------- updates ----------

    /// <summary>Looks for a newer release and downloads it: once a day while automatic updates are on, or from the button in settings.</summary>
    public async Task CheckForUpdatesAsync(bool userInitiated)
    {
        updateTimer.Stop();
        bool checkedOk = await Updates.CheckAndDownloadAsync(userInitiated);
        if (checkedOk)
        {
            Settings.LastUpdateCheck = DateTime.UtcNow;
            store.Save(Settings);
        }
        // Offline or GitHub unreachable: try again in an hour instead of waiting a day.
        ScheduleUpdateCheck(checkedOk ? null : TimeSpan.FromHours(1));
    }

    public void SetAutoUpdate(bool enabled)
    {
        Settings.AutoUpdate = enabled;
        SaveSettings();
        ScheduleUpdateCheck();
    }

    /// <summary>Applies the downloaded version now: the app exits and Velopack starts the new one in the tray.</summary>
    public void RestartToUpdate()
    {
        if (Updates.RestartToUpdate()) Exit();
    }

    void ScheduleUpdateCheck(TimeSpan? after = null)
    {
        updateTimer.Stop();
        if (exiting || !Settings.AutoUpdate || Updates.Stage is UpdateStage.Unavailable or UpdateStage.Ready) return;
        TimeSpan delay = after ?? UpdateSchedule.NextCheck(Settings.LastUpdateCheck, DateTime.UtcNow, TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt));
        updateTimer.Interval = delay > TimeSpan.FromSeconds(1) ? delay : TimeSpan.FromSeconds(1);
        updateTimer.Start();
    }

    void OnUpdateReady(string version)
    {
        if (Settings.UpdateNoticeShownFor == version) return;
        Settings.UpdateNoticeShownFor = version;
        store.Save(Settings);
        tray.ShowBalloon(Loc.T("Remote for KEF 업데이트 준비 완료", "Remote for KEF update ready", "Remote for KEFのアップデートの準備ができました"),
            Loc.T($"{version} 버전은 다음에 앱을 시작할 때 적용됩니다. 설정 > 일반에서 바로 다시 시작해 적용할 수도 있습니다.",
                $"Version {version} is applied the next time the app starts. You can also restart now from Settings > General.",
                $"バージョン {version} は次にアプリを起動したときに適用されます。設定 > 一般からすぐに再起動して適用することもできます。"));
    }

    // ---------- surfaces ----------

    public void ShowFlyout() => flyout.Show(TrayAnchor(null));

    public SettingsView OpenSettings(SettingsPage page = SettingsPage.General, SpeakerSlot? speaker = null)
    {
        flyout.Hide();
        if (settingsWindow == null)
        {
            settingsWindow = new SettingsView(this, page, speaker);
            settingsWindow.Closed += OnSettingsClosed;
            ScheduleNextPoll();
        }
        settingsWindow.Show(page, speaker);
        return settingsWindow;
    }

    void OnSettingsClosed()
    {
        settingsWindow = null;
        if (exiting) return;
        Hotkeys.Resume();
        ScheduleNextPoll();
        if (!Settings.TrayNoticeShown && !environment.SelfTest)
        {
            Settings.TrayNoticeShown = true;
            store.Save(Settings);
            tray.ShowBalloon(Loc.T("Remote for KEF는 알림 영역에서 실행 중입니다", "Remote for KEF is running in the notification area", "Remote for KEFは通知領域で実行中です"),
                Loc.T("아이콘을 눌러 음량과 입력을 바꾸세요. 아이콘이 보이지 않으면 작업 표시줄의 ^ 안을 확인하세요.", "Select the icon to change the volume and input. If you can't see it, look behind the ^ on the taskbar.", "アイコンをクリックすると音量と入力を変えられます。アイコンが見当たらない場合は、タスクバーの ^ の中を確認してください。"));
        }
    }

    void ShowFromLaunch()
    {
        if (!Settings.IsConfigured) OpenSettings(SettingsPage.Speaker);
        else ShowFlyout();
    }

    void OnFlyoutVisibilityChanged()
    {
        if (flyout.IsVisible) _ = RefreshNowAsync();
        ScheduleNextPoll();
    }

    /// <summary>Top center of the tray icon when Windows reports it, else the click point, else the cursor.</summary>
    PointInt32 TrayAnchor(PointInt32? click)
    {
        if (tray.Bounds() is Win32.RECT r) return new PointInt32(r.Left + r.Width / 2, r.Top);
        if (click is PointInt32 p && (p.X != 0 || p.Y != 0)) return p;
        return WindowHelpers.CursorPosition();
    }

    void OnControllerChanged(SpeakerSlot slot)
    {
        // Kept so the list can name the speaker and draw its model while it is off or away.
        if (slot.Controller.Device is { } device && (slot.Entry.Name != device.Name || slot.Entry.Model != device.Model))
        {
            slot.Entry.Name = device.Name;
            slot.Entry.Model = device.Model;
            store.Save(Settings);
        }
        if (slot == selected)
        {
            volumeKeys.Tolerance = slot.Controller.Driver?.VolumeTolerance ?? 0;
            UpdateTray();
        }
        Changed?.Invoke();
    }

    void OnLastActiveSourceChanged(SpeakerSlot slot, string source)
    {
        if (slot.Entry.LastInput == source || !slots.Contains(slot)) return;
        slot.Entry.LastInput = source;
        store.Save(Settings);
    }

    void UpdateTray() => tray.Update(SpeakerPresentation.Glyph(Controller),
        SpeakerPresentation.Tooltip(Controller, Settings.IsConfigured, selected?.Name));

    void WarnHotkeysOnce()
    {
        if (hotkeyWarningShown || environment.SelfTest || environment.Demo) return;
        hotkeyWarningShown = true;
        var taken = HotkeyBindings.Where(b => Hotkeys.Status.GetValueOrDefault(b.Action) == HotkeyRegistration.InUse).Select(b => b.ToString());
        string list = string.Join(Loc.T(", ", ", ", "、"), taken);
        tray.ShowBalloon(Loc.T("단축키를 등록하지 못했습니다", "Couldn't register shortcuts", "ショートカットを登録できませんでした"),
            Loc.T(
                $"다른 앱이 이미 쓰고 있습니다: {list}. 설정 > 단축키에서 다른 조합으로 바꿀 수 있습니다.",
                $"Another app already uses {list}. You can choose other combinations in Settings > Shortcuts.",
                $"ほかのアプリがすでに使っています: {list}。設定 > ショートカットで別の組み合わせに変更できます。"));
    }

    void RunLater(int milliseconds, Action action)
    {
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            laterTimers.Remove(t);
            action();
        };
        laterTimers.Add(timer);
        timer.Start();
    }

    public void Exit()
    {
        if (exiting) return;
        exiting = true;
        Dispose();
        Application.Current.Exit();
    }

    public void Dispose()
    {
        exiting = true;
        pollTimer.Stop();
        listTimer.Stop();
        updateTimer.Stop();
        foreach (var t in laterTimers) t.Stop();
        laterTimers.Clear();
        showWait?.Unregister(null);
        showEvent?.Dispose();
        settingsWindow?.Close();
        mediaCard.Close();
        flyout.Close();
        osd.Close();
        Hotkeys.Dispose();
        volumeKeys.Dispose();
        tray.Dispose();
        messages.Dispose();
        foreach (var slot in slots) slot.Controller.Dispose();
        none.Dispose();
    }
}
