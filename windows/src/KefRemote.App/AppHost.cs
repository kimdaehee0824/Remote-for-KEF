using KefRemote.App.Native;
using KefRemote.App.Views;
using KefRemote.Core;
using KefRemote.Desktop;
using KefRemote.Drivers;
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
/// Application lifetime: owns the speaker controller, settings, tray icon, hotkeys and the three surfaces
/// (tray flyout, volume display, settings window). Everything runs on the UI thread.
/// </summary>
sealed class AppHost : IDisposable
{
    static readonly int[] ReconnectBackoff = [2000, 5000, 10000, 30000];
    const int DeviceRefreshMs = 30000, FreshOnOpenMs = 1000, IdleWaitMs = 3000;

    readonly HostEnvironment environment;
    readonly SettingsStore store;
    readonly DispatcherQueue queue;
    readonly DispatcherQueueTimer pollTimer;
    readonly MessageWindow messages;
    readonly TrayIcon tray;
    readonly HotkeyVolumeCoordinator volumeKeys;
    readonly EventWaitHandle? showEvent;
    readonly RegisteredWaitHandle? showWait;
    readonly HashSet<DispatcherQueueTimer> laterTimers = [];
    readonly FlyoutView flyout;
    readonly OsdView osd;
    SettingsView? settingsWindow;
    bool connecting;
    bool configurationError;
    long lastDeviceRefresh;
    bool hotkeyWarningShown;
    bool exiting;

    public AppSettings Settings { get; }
    public SpeakerController Controller { get; } = new();
    public HotkeyManager Hotkeys { get; }

    /// <summary>Consecutive failed connection attempts. The flyout shows "offline" instead of "connecting" while above 0.</summary>
    public int ConnectFailures { get; private set; }

    public bool IsConnecting => connecting;
    public bool ConfigurationError => configurationError;
    public bool SelfTest => environment.SelfTest;
    public bool Demo => environment.Demo;
    public string SettingsFolder => store.Directory;
    public IReadOnlyList<HotkeyBinding> HotkeyBindings => HotkeyBinding.Normalize(Settings.Hotkeys);
    public FlyoutView Flyout => flyout;
    public OsdView Osd => osd;
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
        queue = DispatcherQueue.GetForCurrentThread();

        Controller.AppVolumeLimit = Settings.AppVolumeLimit;
        Controller.ApproachFromAbove = Settings.ApproachFromAbove;
        Controller.Changed += OnControllerChanged;

        messages = new MessageWindow();
        tray = new TrayIcon(messages, queue);
        Hotkeys = new HotkeyManager(messages);

        osd = new OsdView(this);
        flyout = new FlyoutView(this);
        flyout.VisibilityChanged += OnFlyoutVisibilityChanged;
        tray.Activated += (x, y) => flyout.Toggle(TrayAnchor(new PointInt32(x, y)));

        volumeKeys = new HotkeyVolumeCoordinator(
            snapshot: () => new VolumeSnapshot(
                Ready: Controller.IsReady && Controller.Supports(DriverCapabilities.Volume),
                Volume: Controller.State.Volume,
                Muted: Controller.State.Muted,
                Limit: Controller.EffectiveLimit,
                Status: Controller.IsReady ? Controller.Notice : Controller.Status),
            setVolumeTarget: Controller.SetVolumeTargetAsync,
            setMute: Controller.SetMuteAsync,
            controllerBusy: () => Controller.Busy,
            osd: osd,
            runLater: RunLater);
        osd.Input += volumeKeys.Handle;

        Hotkeys.Pressed += volumeKeys.Handle;
        Hotkeys.StatusChanged += () => Changed?.Invoke();
        ApplyPreferences();
        ApplyHotkeys();

        pollTimer = queue.CreateTimer();
        pollTimer.IsRepeating = false;
        pollTimer.Tick += async (_, _) => await PollTickAsync();

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
    }

    // ---------- connection ----------

    public async Task ConnectAsync()
    {
        if (connecting) return;
        if (!Settings.IsConfigured)
        {
            Controller.Disconnect(Loc.T("스피커 주소를 입력하세요", "Enter the speaker address", "スピーカーのアドレスを入力してください"));
            return;
        }
        connecting = true;
        pollTimer.Stop();
        try
        {
            ISpeakerDriver driver;
            try
            {
                var driverOptions = new DriverOptions(HostAddress.ParseLocal(Settings.Host), Secrets.Unprotect(Settings.ProtectedPassword));
                driver = environment.CreateDriver?.Invoke(driverOptions) ?? BuiltInDrivers.Find(Settings.DriverId).Create(driverOptions);
            }
            catch (SpeakerException e)
            {
                // A bad address will not fix itself; wait for the user to change the settings.
                configurationError = true;
                Controller.Disconnect(e.Message);
                return;
            }
            configurationError = false;
            bool ok = await Controller.ConnectAsync(driver);
            ConnectFailures = ok ? 0 : ConnectFailures + 1;
            if (ok) lastDeviceRefresh = Environment.TickCount64;
        }
        finally
        {
            connecting = false;
            ScheduleNextPoll();
            Changed?.Invoke();
        }
    }

    /// <summary>Saves a new address and password and reconnects at once.</summary>
    public Task ApplyConnectionAsync(string host, string password)
    {
        Settings.Host = host.Trim();
        Settings.ProtectedPassword = Secrets.Protect(password);
        store.Save(Settings);
        ConnectFailures = 0;
        configurationError = false;
        return ConnectAsync();
    }

    /// <summary>"Try again" from the flyout or settings: skip the backoff.</summary>
    public Task ReconnectAsync()
    {
        ConnectFailures = 0;
        return ConnectAsync();
    }

    async Task PollTickAsync()
    {
        pollTimer.Stop();
        if (!connecting && !exiting)
        {
            if (Controller.Driver == null || Controller.Device == null)
            {
                if (Settings.IsConfigured && !configurationError) await ConnectAsync();
            }
            else
            {
                await Controller.PollAsync();
            }
        }
        ScheduleNextPoll();
    }

    void ScheduleNextPoll()
    {
        if (exiting) return;
        bool visible = flyout.IsVisible || settingsWindow != null;
        TimeSpan delay = Controller.Device == null
            ? TimeSpan.FromMilliseconds(ReconnectBackoff[Math.Clamp(ConnectFailures - 1, 0, ReconnectBackoff.Length - 1)])
            : Controller.NextPollDelay(visible);
        pollTimer.Interval = delay;
        pollTimer.Start();
    }

    /// <summary>Read now when the numbers on screen might be old, e.g. when the flyout opens.</summary>
    public async Task RefreshNowAsync()
    {
        if (connecting || Controller.Busy || Controller.Device == null) return;
        if (Environment.TickCount64 - lastDeviceRefresh > DeviceRefreshMs)
        {
            // Picks up a maximum volume changed in KEF Connect. Read only.
            lastDeviceRefresh = Environment.TickCount64;
            await Controller.RefreshDeviceAsync();
        }
        if (Controller.Busy) return;
        long age = Controller.Clock() - Controller.State.SeenAt;
        if (!Controller.State.Valid || age > FreshOnOpenMs)
        {
            pollTimer.Stop();
            await Controller.PollAsync();
            ScheduleNextPoll();
        }
    }

    /// <summary>
    /// Runs a command from a click. A background read makes the controller drop commands, so wait for it briefly
    /// instead of losing the click. Another running command still wins.
    /// </summary>
    public async Task RunWhenIdle(Func<Task> command)
    {
        for (int waited = 0; Controller.Busy && Controller.Operation == SpeakerOperation.None; waited += 40)
        {
            if (waited > IdleWaitMs) return;
            await Task.Delay(40);
        }
        if (Controller.Busy) return;
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
        Controller.AppVolumeLimit = Settings.AppVolumeLimit;
        Controller.ApproachFromAbove = Settings.ApproachFromAbove;
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

    // ---------- surfaces ----------

    public void ShowFlyout() => flyout.Show(TrayAnchor(null));

    public SettingsView OpenSettings(SettingsPage page = SettingsPage.General)
    {
        flyout.Hide();
        if (settingsWindow == null)
        {
            settingsWindow = new SettingsView(this, page);
            settingsWindow.Closed += OnSettingsClosed;
            ScheduleNextPoll();
        }
        settingsWindow.Show(page);
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

    void OnControllerChanged()
    {
        volumeKeys.Tolerance = Controller.Driver?.VolumeTolerance ?? 0;
        UpdateTray();
        Changed?.Invoke();
    }

    void UpdateTray() => tray.Update(SpeakerPresentation.Glyph(Controller), SpeakerPresentation.Tooltip(Controller, Settings.IsConfigured));

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
        foreach (var t in laterTimers) t.Stop();
        laterTimers.Clear();
        showWait?.Unregister(null);
        showEvent?.Dispose();
        settingsWindow?.Close();
        flyout.Close();
        osd.Close();
        Hotkeys.Dispose();
        volumeKeys.Dispose();
        tray.Dispose();
        messages.Dispose();
        Controller.Dispose();
    }
}
