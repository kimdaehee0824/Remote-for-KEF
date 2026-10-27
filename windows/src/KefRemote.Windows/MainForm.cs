using KefRemote.Core;
using KefRemote.Drivers;

namespace KefRemote.Windows;

/// <summary>
/// Settings and control window built from standard WinForms controls.
/// It only turns user input into controller commands; values written by the program
/// (device updates) never produce writes.
/// </summary>
sealed class MainForm : Form
{
    readonly TrayApplicationContext app;
    SpeakerController C => app.Controller;

    readonly Label nameLabel = new() { AutoSize = true };
    readonly Label statusLabel = new() { AutoSize = true, Anchor = AnchorStyles.Right };
    readonly Label detailLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    readonly FlowLayoutPanel sourcePanel = new() { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill };
    readonly GroupBox sourceGroup = new() { Text = "입력", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8) };
    readonly Label volumeLabel = new() { AutoSize = true, Text = "-" };
    readonly Button muteButton = new() { AutoSize = true, Text = "음소거" };
    readonly TrackBar slider = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 1, LargeChange = 5, Dock = DockStyle.Fill };
    readonly Label limitHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    readonly ComboBox driverBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly TextBox hostBox = new() { Width = 160, PlaceholderText = "192.168.0.14" };
    readonly TextBox passwordBox = new() { Width = 160, UseSystemPasswordChar = true, PlaceholderText = "없으면 비워 두세요" };
    readonly Button connectButton = new() { AutoSize = true, Text = "연결" };
    readonly Label driverNote = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(460, 0) };
    readonly NumericUpDown limitBox = new() { Minimum = 1, Maximum = 100, Width = 64 };
    readonly CheckBox startupBox = new() { AutoSize = true, Text = "Windows 로그인 시 트레이로 조용히 실행" };
    readonly System.Windows.Forms.Timer sendTimer = new() { Interval = 200 };

    bool updatingFromDevice;
    bool dragging;
    int? pendingTarget;
    long pendingSince;
    bool forceClose;
    ISpeakerDriver? renderedDriver;

    public MainForm(TrayApplicationContext app)
    {
        this.app = app;
        Text = "Remote for KEF";
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Icon = SystemIcons.Application;

        nameLabel.Font = new Font(Font.FontFamily, Font.Size * 1.5f, FontStyle.Bold);
        volumeLabel.Font = new Font(Font.FontFamily, Font.Size * 2.2f, FontStyle.Bold);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16), Dock = DockStyle.Fill };
        Controls.Add(root);

        var header = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(nameLabel, 0, 0);
        header.Controls.Add(statusLabel, 1, 0);
        root.Controls.Add(header);
        root.Controls.Add(detailLabel);

        sourceGroup.Controls.Add(sourcePanel);
        root.Controls.Add(sourceGroup);

        var volumeGroup = new GroupBox { Text = "음량", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8) };
        var volumeLayout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        volumeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        volumeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        volumeLayout.Controls.Add(volumeLabel, 0, 0);
        muteButton.Anchor = AnchorStyles.Right;
        volumeLayout.Controls.Add(muteButton, 1, 0);
        volumeLayout.Controls.Add(slider, 0, 1);
        volumeLayout.SetColumnSpan(slider, 2);
        volumeLayout.Controls.Add(limitHint, 0, 2);
        volumeLayout.SetColumnSpan(limitHint, 2);
        volumeGroup.Controls.Add(volumeLayout);
        root.Controls.Add(volumeGroup);

        var connectGroup = new GroupBox { Text = "연결 설정", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8) };
        var connectLayout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        var driverCaption = AddRow(connectLayout, "기기 종류", driverBox);
        // With a single bundled driver there is nothing to choose.
        driverCaption.Visible = driverBox.Visible = BuiltInDrivers.All.Count > 1;
        AddRow(connectLayout, "IP 주소", hostBox);
        AddRow(connectLayout, "웹페이지 암호", passwordBox);
        connectLayout.Controls.Add(driverNote);
        connectLayout.SetColumnSpan(driverNote, 2);
        connectLayout.Controls.Add(connectButton);
        connectGroup.Controls.Add(connectLayout);
        root.Controls.Add(connectGroup);

        var prefs = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
        var limitRow = new FlowLayoutPanel { AutoSize = true };
        limitRow.Controls.Add(new Label { Text = "이 앱에서 올릴 수 있는 최대 음량", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        limitRow.Controls.Add(limitBox);
        prefs.Controls.Add(limitRow);
        prefs.Controls.Add(startupBox);
        root.Controls.Add(prefs);

        foreach (var d in BuiltInDrivers.All)
            driverBox.Items.Add(new DriverItem(d));
        driverBox.SelectedItem = driverBox.Items.Cast<DriverItem>().First(i => i.Descriptor == BuiltInDrivers.Find(app.Settings.DriverId));
        hostBox.Text = app.Settings.Host;
        passwordBox.Text = app.Settings.Password;
        limitBox.Value = Math.Clamp(app.Settings.AppVolumeLimit, 1, 100);
        startupBox.Checked = StartupManager.IsEnabled;
        UpdateDriverNote();

        driverBox.SelectedIndexChanged += (_, _) => UpdateDriverNote();
        connectButton.Click += async (_, _) => await ConnectAsync();
        hostBox.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ConnectAsync(); } };
        passwordBox.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ConnectAsync(); } };
        muteButton.Click += async (_, _) => await C.ToggleMuteAsync();
        limitBox.ValueChanged += (_, _) => { app.Settings.AppVolumeLimit = (int)limitBox.Value; app.SaveSettings(); };
        startupBox.CheckedChanged += (_, _) => SetStartup(startupBox.Checked);

        slider.MouseDown += (_, _) => dragging = true;
        slider.MouseUp += (_, _) => { dragging = false; SendPending(); };
        slider.Scroll += (_, _) => OnSliderMoved();
        sendTimer.Tick += (_, _) => SendPending();

        C.Changed += RenderState;
        FormClosed += (_, _) => C.Changed -= RenderState;
        RenderState();
    }

    public void Close(bool force)
    {
        forceClose = force;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!forceClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        sendTimer.Stop();
        base.OnFormClosing(e);
    }

    static Label AddRow(TableLayoutPanel layout, string label, Control control)
    {
        var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
        layout.Controls.Add(caption);
        layout.Controls.Add(control);
        return caption;
    }

    async Task ConnectAsync()
    {
        var item = (DriverItem)driverBox.SelectedItem!;
        connectButton.Enabled = false;
        try
        {
            await app.ApplyConnectionAsync(item.Descriptor.Id, hostBox.Text, passwordBox.Text);
        }
        finally
        {
            connectButton.Enabled = true;
        }
    }

    void UpdateDriverNote()
    {
        var d = ((DriverItem)driverBox.SelectedItem!).Descriptor;
        passwordBox.Enabled = d.UsesPassword;
        driverNote.Text = d.Status == DriverStatus.Verified
            ? $"지원: {d.SupportedModels}"
            : $"실험적 드라이버입니다. 실제 기기에서 검증되지 않았습니다. ({d.SupportedModels})";
    }

    void SetStartup(bool enabled)
    {
        if (enabled && StartupManager.IsUnstableLocation)
        {
            MessageBox.Show(this, "다운로드 또는 임시 폴더에서 실행 중입니다. 앱을 고정된 폴더로 옮긴 뒤 다시 켜 주세요.", "Remote for KEF", MessageBoxButtons.OK, MessageBoxIcon.Information);
            startupBox.Checked = false;
            return;
        }
        try
        {
            StartupManager.Set(enabled);
            app.Settings.StartWithWindows = enabled;
            app.SaveSettings();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            startupBox.Checked = StartupManager.IsEnabled;
        }
    }

    void OnSliderMoved()
    {
        if (updatingFromDevice) return;
        pendingTarget = slider.Value;
        pendingSince = Environment.TickCount64;
        volumeLabel.Text = slider.Value.ToString();
        sendTimer.Stop();
        sendTimer.Start();
    }

    /// <summary>
    /// Sends only the latest target, once the user stops moving the slider. If a poll is
    /// in progress the send waits briefly; a target older than two seconds is dropped.
    /// </summary>
    async void SendPending()
    {
        sendTimer.Stop();
        if (dragging || pendingTarget is not int target) return;
        if (C.Busy && Environment.TickCount64 - pendingSince < 2000)
        {
            sendTimer.Interval = 100;
            sendTimer.Start();
            return;
        }
        sendTimer.Interval = 200;
        pendingTarget = null;
        if (C.CanControl) await C.SetVolumeTargetAsync(target);
        RenderState();
    }

    void RenderState()
    {
        if (IsDisposed) return;
        var s = C.State;
        bool fresh = C.Fresh;
        bool ready = C.CanControl;

        nameLabel.Text = C.Device?.Name ?? "스피커 연결 전";
        statusLabel.Text = (fresh && s.Active ? "● " : "○ ") + C.Status;
        statusLabel.ForeColor = fresh && s.Active ? Color.SeaGreen : SystemColors.GrayText;
        detailLabel.Text = C.Device == null ? app.Settings.Host : $"{C.Device.Model} · {C.Device.Firmware} · {app.Settings.Host}";

        if (!ReferenceEquals(renderedDriver, C.Driver)) RebuildSources();
        sourceGroup.Visible = C.Supports(DriverCapabilities.Source);
        foreach (RadioButton b in sourcePanel.Controls)
        {
            b.Checked = fresh && (string)b.Tag! == s.SourceId;
            b.Enabled = ready;
        }

        muteButton.Visible = C.Supports(DriverCapabilities.Mute);
        muteButton.Text = s.Muted ? "음소거 해제" : "음소거";
        muteButton.Enabled = ready;
        slider.Enabled = fresh && s.Active && C.Supports(DriverCapabilities.Volume);

        if (!dragging && pendingTarget == null)
        {
            updatingFromDevice = true;
            slider.Value = fresh ? Math.Clamp(s.Volume, 0, 100) : 0;
            updatingFromDevice = false;
            volumeLabel.Text = fresh ? s.Volume + (s.Muted ? "  (음소거)" : "") : "-";
        }

        int limit = C.EffectiveLimit;
        limitHint.Text = fresh && s.Volume > limit
            ? $"앱 상한 {limit} · 현재 음량이 상한보다 높습니다. 낮추는 것만 가능합니다"
            : $"앱 상한 {limit}" + (C.Device?.DeviceVolumeLimit is int d ? $" (기기 상한 {d})" : "");
    }

    void RebuildSources()
    {
        renderedDriver = C.Driver;
        sourcePanel.Controls.Clear();
        if (C.Driver == null) return;
        foreach (var source in C.Driver.Sources)
        {
            var button = new RadioButton
            {
                Text = source.Label,
                Tag = source.Id,
                Appearance = Appearance.Button,
                AutoSize = true,
                AutoCheck = false,
                Padding = new Padding(8, 4, 8, 4),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            button.Click += async (_, _) => await C.SelectSourceAsync(source.Id);
            sourcePanel.Controls.Add(button);
        }
    }

    sealed record DriverItem(DriverDescriptor Descriptor)
    {
        public override string ToString() =>
            Descriptor.DisplayName + (Descriptor.Status == DriverStatus.Experimental ? " (실험적)" : "");
    }
}
