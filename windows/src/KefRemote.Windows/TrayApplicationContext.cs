using KefRemote.Core;
using KefRemote.Drivers;

namespace KefRemote.Windows;

/// <summary>
/// Application lifetime. Lives in the tray; the window is created only when needed.
/// Closing the window hides it, Exit in the tray menu quits.
/// </summary>
sealed class TrayApplicationContext : ApplicationContext
{
    readonly SpeakerController controller = new();
    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip menu = new();
    readonly System.Windows.Forms.Timer pollTimer = new();
    readonly EventWaitHandle showEvent = new(false, EventResetMode.AutoReset, Program.ShowEventName);
    readonly RegisteredWaitHandle showWait;
    readonly SynchronizationContext ui;
    MainForm? form;
    bool connecting;

    public AppSettings Settings { get; }
    public SpeakerController Controller => controller;

    public TrayApplicationContext(bool showWindow)
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Settings = SettingsStore.Load();
        controller.AppVolumeLimit = Settings.AppVolumeLimit;
        controller.Changed += OnChanged;

        tray.Icon = SystemIcons.Application;
        tray.Text = "Remote for KEF";
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        menu.Opening += (_, _) => BuildMenu();
        tray.Visible = true;

        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);

        pollTimer.Tick += async (_, _) => await PollAsync();
        if (showWindow) ShowWindow();
        _ = ConnectAsync();
    }

    public async Task ConnectAsync()
    {
        if (connecting) return;
        if (string.IsNullOrWhiteSpace(Settings.Host))
        {
            controller.Disconnect("스피커 주소를 입력하세요");
            ShowWindow();
            return;
        }
        connecting = true;
        pollTimer.Stop();
        try
        {
            var descriptor = BuiltInDrivers.Find(Settings.DriverId);
            ISpeakerDriver driver;
            try
            {
                driver = descriptor.Create(new DriverOptions(Settings.Host, Settings.Password));
            }
            catch (SpeakerException e)
            {
                controller.Disconnect(e.Message);
                return;
            }
            await controller.ConnectAsync(driver);
        }
        finally
        {
            connecting = false;
            ScheduleNextPoll();
        }
    }

    /// <summary>Called when the user edits connection settings. Invalidates the old connection at once.</summary>
    public Task ApplyConnectionAsync(string driverId, string host, string password)
    {
        Settings.DriverId = driverId;
        Settings.Host = host.Trim();
        Settings.Password = password;
        SettingsStore.Save(Settings);
        return ConnectAsync();
    }

    public void SaveSettings()
    {
        controller.AppVolumeLimit = Settings.AppVolumeLimit;
        SettingsStore.Save(Settings);
    }

    async Task PollAsync()
    {
        pollTimer.Stop();
        if (!connecting)
        {
            if (controller.Driver == null && !string.IsNullOrWhiteSpace(Settings.Host)) await ConnectAsync();
            else await controller.PollAsync();
        }
        ScheduleNextPoll();
    }

    void ScheduleNextPoll()
    {
        bool visible = form is { Visible: true };
        pollTimer.Interval = (int)controller.NextPollDelay(visible).TotalMilliseconds;
        pollTimer.Start();
    }

    public void ShowWindow()
    {
        if (form == null || form.IsDisposed)
        {
            form = new MainForm(this);
            form.VisibleChanged += (_, _) => ScheduleNextPoll();
        }
        form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    void OnChanged()
    {
        var s = controller.State;
        string name = controller.Device?.Name ?? "Remote for KEF";
        string text = controller.Fresh
            ? $"{name} · {SourceLabel(s.SourceId)} · {s.Volume}{(s.Muted ? " · 음소거" : "")}"
            : $"{name} · {controller.Status}";
        tray.Text = text.Length > 127 ? text[..127] : text;
    }

    public string SourceLabel(string? id) =>
        controller.Driver?.Sources.FirstOrDefault(x => x.Id == id)?.Label ?? id ?? "-";

    void BuildMenu()
    {
        menu.Items.Clear();
        menu.Items.Add("열기", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());

        bool ready = controller.CanControl;
        if (controller.Supports(DriverCapabilities.Source))
        {
            foreach (var source in controller.Driver!.Sources)
            {
                var item = new ToolStripMenuItem(source.Label, null, async (_, _) => await controller.SelectSourceAsync(source.Id))
                {
                    Checked = controller.State.SourceId == source.Id,
                    Enabled = ready,
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
        }
        if (controller.Supports(DriverCapabilities.Mute))
        {
            menu.Items.Add(new ToolStripMenuItem(controller.State.Muted ? "음소거 해제" : "음소거", null,
                async (_, _) => await controller.ToggleMuteAsync()) { Enabled = ready });
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitThread());
    }

    protected override void ExitThreadCore()
    {
        pollTimer.Stop();
        showWait.Unregister(null);
        tray.Visible = false;
        form?.Close(force: true);
        controller.Dispose();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Dispose();
            menu.Dispose();
            pollTimer.Dispose();
            showEvent.Dispose();
        }
        base.Dispose(disposing);
    }
}
