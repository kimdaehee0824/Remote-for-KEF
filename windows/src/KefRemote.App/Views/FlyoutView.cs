using KefRemote.App.Native;
using KefRemote.Core;
using KefRemote.Desktop;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace KefRemote.App.Views;

/// <summary>
/// The tray flyout (design W1–W5): opens above the tray icon on any click, closes on Esc or when focus leaves.
/// One place per fact: power is the switch, volume the big number, input the filled tile. The line under the
/// name is the model unless something is out of the ordinary.
/// </summary>
sealed class FlyoutView
{
    const double FlyoutWidth = 360, MinHeight = 120;
    const int EdgeMarginDip = 12;
    const long ReopenGuardMs = 350, UserVolumeHoldMs = 900, NoticeLifetimeMs = 12000;

    readonly AppHost host;
    readonly Window window;
    readonly AppWindow appWindow;
    readonly IntPtr hwnd;
    readonly StackPanel root;
    readonly Grid surface;

    readonly Border appIcon;
    readonly TextBlock title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock subtitle = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    readonly ToggleSwitch power = new() { OnContent = "", OffContent = "", MinWidth = 0 };

    readonly ProgressBar connecting = new() { IsIndeterminate = true };
    readonly InfoBar problem = new() { IsClosable = false };
    readonly Button retryButton = new() { Content = "다시 연결" };
    readonly Button setupButton = new() { Content = "스피커 설정" };
    readonly InfoBar notice = new() { IsClosable = true };

    readonly StackPanel volumeSection = new() { Spacing = 10 };
    readonly TextBlock volumeNumber = new()
    {
        FontSize = 44,
        FontWeight = FontWeights.SemiBold,
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        LineHeight = 48,
    };
    readonly StackPanel limitPanel = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    readonly TextBlock limitValue = Ui.Text("", 12, style: "KefLimitText");
    readonly TextBlock limitSource = Ui.Secondary("");
    readonly StackPanel switchingNote;
    readonly Button muteButton;
    readonly FontIcon muteIcon = Ui.Icon(Glyphs.Volume2, 16);
    readonly Slider slider = new()
    {
        Minimum = 0,
        Maximum = 100,
        StepFrequency = 1,
        SmallChange = 1,
        LargeChange = 10,
        IsThumbToolTipEnabled = false,
        VerticalAlignment = VerticalAlignment.Center,
    };
    readonly Rectangle limitTick = new() { Width = 2, Height = 16, RadiusX = 1, RadiusY = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly Grid sliderHost = new();

    readonly TextBlock sourcesLabel = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, -4) };
    readonly Grid tiles = new() { ColumnSpacing = 6, RowSpacing = 6 };
    readonly TextBlock standbyNote = Ui.Secondary("");
    readonly List<SourceTile> tileList = [];

    readonly StackPanel keys = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    readonly HyperlinkButton hotkeyWarning = new() { Content = "단축키 확인 필요", Padding = new Thickness(4, 2, 4, 2) };

    bool updating;
    bool closing;
    bool dragging;
    long lastUserVolumeAt;
    int? sentTarget;
    string? pendingSource;
    long shownAt, hiddenAt = long.MinValue / 2;
    PointInt32 anchor;
    string tileSignature = "", keysSignature = "";
    SizeInt32 lastClientSize;

    public bool IsVisible { get; private set; }
    public event Action? VisibilityChanged;

    /// <summary>
    /// The window content with its background, for self-test captures. The stack itself has no
    /// background, and RenderTargetBitmap leaves out its empty top band.
    /// </summary>
    public FrameworkElement Root => surface;

    /// <summary>Distance from the top of the flyout to the app icon, in DIPs (self-test).</summary>
    public double IconTop => appIcon.TransformToVisual(root).TransformPoint(default).Y;

    /// <summary>Window content height against the content's own height, in DIPs (self-test).</summary>
    public (double Window, double Content) Heights => (surface.ActualHeight, Math.Max(root.DesiredSize.Height, MinHeight));

    sealed record SourceTile(string Id, Grid Container, ToggleButton Button, FontIcon Icon, ProgressRing Ring, Border LastInputRing);

    public FlyoutView(AppHost host)
    {
        this.host = host;

        // Header: app icon, name and model, power switch.
        appIcon = new Border
        {
            Style = Ui.Style("KefAppIconTile"),
            Width = 36,
            Height = 36,
            Child = Ui.AppGlyph(26),
        };
        AutomationProperties.SetName(power, "전원");
        ToolTipService.SetToolTip(power, "전원");
        power.Toggled += OnPowerToggled;
        var header = Ui.Columns(12,
            (Ui.Auto, appIcon),
            (Ui.Star, Ui.Column(0, title, subtitle)),
            (Ui.Auto, power));
        header.Padding = new Thickness(16, 16, 12, 6);
        ((FrameworkElement)header.Children[1]).VerticalAlignment = VerticalAlignment.Center;

        // Problems and notices.
        retryButton.Click += async (_, _) => await host.ReconnectAsync();
        setupButton.Click += (_, _) => host.OpenSettings(SettingsPage.Speaker);
        problem.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 12), Children = { retryButton, setupButton } };
        notice.Closed += (_, _) => host.Controller.ClearNotice();

        // Volume: the number large, limit and its source small on the right, then mute + slider.
        limitPanel.Children.Add(Ui.Row(4, Ui.Secondary("상한"), limitValue));
        limitPanel.Children.Add(limitSource);
        limitSource.HorizontalAlignment = HorizontalAlignment.Right;
        switchingNote = Ui.Column(0, Ui.Secondary("바꾸는 동안"), Ui.Secondary("잠시 음소거"));
        switchingNote.HorizontalAlignment = HorizontalAlignment.Right;
        switchingNote.VerticalAlignment = VerticalAlignment.Bottom;
        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = Ui.Star });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = Ui.Auto });
        hero.Children.Add(volumeNumber);
        Grid.SetColumn(limitPanel, 1);
        Grid.SetColumn(switchingNote, 1);
        hero.Children.Add(limitPanel);
        hero.Children.Add(switchingNote);

        muteButton = new Button { Content = muteIcon, Width = 36, Height = 32, Padding = new Thickness(0) };
        muteButton.Click += async (_, _) => await host.RunWhenIdle(host.Controller.ToggleMuteAsync);
        AutomationProperties.SetName(slider, "음량");
        slider.ValueChanged += OnSliderChanged;
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => dragging = true), true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => EndDrag()), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndDrag()), true);
        limitTick.Style = Ui.Style("KefLimitTick");
        ToolTipService.SetToolTip(limitTick, "상한");
        sliderHost.Children.Add(slider);
        sliderHost.Children.Add(limitTick);
        sliderHost.SizeChanged += (_, _) => PlaceLimitTick();
        volumeSection.Children.Add(hero);
        volumeSection.Children.Add(Ui.Columns(10, (Ui.Auto, muteButton), (Ui.Star, sliderHost)));

        var body = Ui.Column(12, connecting, problem, notice, volumeSection, sourcesLabel, tiles, standbyNote);
        body.Padding = new Thickness(16, 6, 16, 14);
        sourcesLabel.Style = Ui.Style("KefSecondaryText");
        sourcesLabel.FontWeight = FontWeights.SemiBold;

        // Footer: shortcut hint on the left, gear menu on the right (settings, about, quit).
        hotkeyWarning.Click += (_, _) => host.OpenSettings(SettingsPage.Hotkeys);
        var gear = new Button
        {
            Content = Ui.Icon(Glyphs.Settings, 16),
            Width = 36,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(gear, "설정 및 종료");
        ToolTipService.SetToolTip(gear, "설정 및 종료");
        gear.Flyout = BuildGearMenu();
        var footerGrid = Ui.Columns(8, (Ui.Star, Ui.Row(8, keys, hotkeyWarning)), (Ui.Auto, gear));
        var footer = new Border { Style = Ui.Style("KefFooter"), Padding = new Thickness(16, 6, 8, 6), Child = footerGrid };

        root = new StackPanel { Width = FlyoutWidth, Children = { header, body, footer } };
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);

        surface = new Grid { Children = { root } };
        bool acrylic = !host.SelfTest && DesktopAcrylicController.IsSupported();
        if (!acrylic) surface.Style = Ui.Style("KefSolidSurface");

        window = new Window { Title = "Remote for KEF", Content = surface };
        if (acrylic) window.SystemBackdrop = new DesktopAcrylicBackdrop();
        hwnd = WindowHelpers.Hwnd(window);
        appWindow = WindowHelpers.AppWindowOf(window);
        WindowHelpers.MakePopup(window, activates: true);
        window.Activated += OnActivated;
        // Alt+F4 hides; the app quits only from the gear menu.
        appWindow.Closing += (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            Hide();
        };

        host.Changed += () =>
        {
            if (IsVisible || host.SelfTest) Refresh();
        };
    }

    MenuFlyout BuildGearMenu()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.TopEdgeAlignedRight, ShouldConstrainToRootBounds = true };
        var settings = new MenuFlyoutItem { Text = "설정", Icon = new FontIcon { Glyph = Glyphs.Settings } };
        settings.Click += (_, _) => host.OpenSettings(SettingsPage.General);
        var about = new MenuFlyoutItem { Text = "정보", Icon = new FontIcon { Glyph = Glyphs.Info } };
        about.Click += (_, _) => host.OpenSettings(SettingsPage.About);
        var quit = new MenuFlyoutItem { Text = "Remote for KEF 종료" };
        quit.Click += (_, _) => host.Exit();
        menu.Items.Add(settings);
        menu.Items.Add(about);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(quit);
        return menu;
    }

    // ---------- show / hide ----------

    /// <summary>Tray click: open, or close when it is open. The click that just closed it by taking focus does not reopen it.</summary>
    public void Toggle(PointInt32 at)
    {
        long now = Environment.TickCount64;
        if (IsVisible)
        {
            if (now - shownAt > ReopenGuardMs) Hide();
            return;
        }
        if (now - hiddenAt < ReopenGuardMs) return;
        Show(at);
    }

    public void Show(PointInt32 at)
    {
        anchor = at;
        bool wasVisible = IsVisible;
        IsVisible = true;
        shownAt = Environment.TickCount64;
        Refresh();
        if (!wasVisible)
        {
            // Put the window on the target monitor first so it takes that monitor's DPI before sizing.
            var work = WindowHelpers.DisplayAt(anchor).WorkArea;
            appWindow.Move(new PointInt32(work.X + work.Width / 2, work.Y + work.Height / 2));
        }
        Layout(force: true);
        appWindow.Show(true);
        window.Activate();
        Win32.SetForegroundWindow(hwnd);
        FocusDefault();
        // The first layout of a never-shown window can be incomplete; size again once it has rendered.
        window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Layout(force: true));
        if (!wasVisible) VisibilityChanged?.Invoke();
    }

    public void Hide()
    {
        if (!IsVisible) return;
        IsVisible = false;
        hiddenAt = Environment.TickCount64;
        appWindow.Hide();
        VisibilityChanged?.Invoke();
    }

    public void Close()
    {
        closing = true;
        IsVisible = false;
        window.Activated -= OnActivated;
        window.Close();
    }

    void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != WindowActivationState.Deactivated || !IsVisible || host.SelfTest) return;
        if (WindowHelpers.OwnsForeground(hwnd)) return; // our own menu or tooltip
        Hide();
    }

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    void FocusDefault()
    {
        if (volumeSection.Visibility == Visibility.Visible && slider.IsEnabled) slider.Focus(FocusState.Programmatic);
        else if (power.IsEnabled) power.Focus(FocusState.Programmatic);
    }

    /// <summary>Sizes the window to its content and keeps it next to the taskbar.</summary>
    void Layout(bool force = false)
    {
        if (!IsVisible) return;
        double scale = root.XamlRoot?.RasterizationScale ?? WindowHelpers.ScaleAt(anchor);
        if (scale <= 0) scale = 1;
        root.Measure(new Size(FlyoutWidth, double.PositiveInfinity));
        double height = Math.Max(root.DesiredSize.Height, MinHeight);
        var client = new SizeInt32((int)Math.Ceiling(FlyoutWidth * scale), (int)Math.Ceiling(height * scale));
        if (!force && client.Equals(lastClientSize)) return;
        lastClientSize = client;
        WindowHelpers.SetClientSize(appWindow, client);
        var display = WindowHelpers.DisplayAt(anchor);
        appWindow.Move(WindowHelpers.PlaceNearTaskbar(display, anchor, appWindow.Size, (int)Math.Round(EdgeMarginDip * scale)));
    }

    // ---------- state → screen ----------

    public void Refresh()
    {
        var c = host.Controller;
        bool configured = host.Settings.IsConfigured;
        var phase = c.Phase;
        // Between reconnect attempts the controller briefly reports "connecting"; keep showing "offline" meanwhile.
        bool offline = phase == SpeakerPhase.Offline || (host.ConnectFailures > 0 && phase == SpeakerPhase.Connecting);
        bool setupError = configured && phase == SpeakerPhase.Disconnected && host.ConfigurationError;
        bool isConnecting = configured && !offline && !setupError && phase is SpeakerPhase.Connecting or SpeakerPhase.Disconnected;
        var op = c.Operation;
        string? switchingTo = op is SpeakerOperation.Source or SpeakerOperation.PowerOn ? c.OperationSource ?? pendingSource : pendingSource;

        updating = true;
        try
        {
            // Header
            title.Text = SpeakerPresentation.Title(c);
            if (offline)
            {
                subtitle.Text = "연결 끊김";
                subtitle.Style = Ui.Style("KefCriticalText");
            }
            else
            {
                subtitle.Text = SpeakerPresentation.Subtitle(c, configured);
                subtitle.Style = Ui.Style(SpeakerPresentation.SubtitleIsError(c, configured) || setupError ? "KefCriticalText" : "KefSecondaryText");
                subtitle.FontWeight = FontWeights.Normal;
            }
            appIcon.Opacity = phase is SpeakerPhase.On or SpeakerPhase.Standby ? 1 : 0.6;

            bool canPower = c.Supports(DriverCapabilities.Power);
            power.IsOn = op switch
            {
                SpeakerOperation.PowerOn => true,
                SpeakerOperation.PowerOff => false,
                _ => phase == SpeakerPhase.On,
            };
            // A background read does not disable it: the click waits for the read (RunWhenIdle).
            power.IsEnabled = canPower && phase is SpeakerPhase.On or SpeakerPhase.Standby && op == SpeakerOperation.None;
            power.Opacity = phase is SpeakerPhase.On or SpeakerPhase.Standby ? 1 : 0.4;

            // Problems
            connecting.Visibility = Show(isConnecting);
            ShowProblem(configured, offline, setupError);
            ShowNotice();

            // Volume
            bool on = phase == SpeakerPhase.On;
            volumeSection.Visibility = Show(on);
            if (on) RefreshVolume(switchingTo != null && op == SpeakerOperation.Source);

            // Inputs
            bool standby = phase == SpeakerPhase.Standby;
            bool showTiles = (on || standby) && c.Supports(DriverCapabilities.Source) && c.Driver!.Sources.Count > 1;
            sourcesLabel.Visibility = Show(showTiles);
            tiles.Visibility = Show(showTiles);
            sourcesLabel.Text = standby ? "이 입력으로 켜기" : "입력";
            if (showTiles) RefreshTiles(standby, switchingTo);
            standbyNote.Visibility = Show(standby);
            standbyNote.Text = Korean.KeepWords(canPower
                ? "테두리 친 타일이 마지막 입력입니다. 켜기 전에 음량이 상한 이하인지 확인합니다."
                : "이 기기는 앱에서 켤 수 없습니다. 리모컨으로 켜 주세요.");

            RefreshFooter();
        }
        finally
        {
            updating = false;
        }
        Layout();
    }

    void ShowProblem(bool configured, bool offline, bool setupError)
    {
        var c = host.Controller;
        if (!configured)
        {
            problem.Severity = InfoBarSeverity.Informational;
            problem.Title = "스피커를 연결하세요";
            problem.Message = Korean.KeepWords("설정에서 스피커의 IP 주소를 입력하면 여기서 바로 음량과 입력을 바꿀 수 있습니다.");
            retryButton.Visibility = Visibility.Collapsed;
            problem.IsOpen = true;
        }
        else if (setupError)
        {
            problem.Severity = InfoBarSeverity.Error;
            problem.Title = "연결할 수 없습니다";
            problem.Message = Korean.KeepWords(c.Status);
            retryButton.Visibility = Visibility.Collapsed;
            problem.IsOpen = true;
        }
        else if (offline)
        {
            problem.Severity = InfoBarSeverity.Error;
            problem.Title = "스피커가 응답하지 않습니다";
            problem.Message = Korean.KeepWords(Reason(c.Status) + "스피커 전원과 Wi-Fi 연결을 확인하세요. 자동으로 다시 시도합니다.");
            retryButton.Visibility = Visibility.Visible;
            retryButton.IsEnabled = !host.IsConnecting;
            problem.IsOpen = true;
        }
        else
        {
            problem.IsOpen = false;
        }
        problem.Visibility = Show(problem.IsOpen);
    }

    /// <summary>"연결 실패: 응답 시간 초과" becomes "응답 시간 초과. ".</summary>
    static string Reason(string status)
    {
        int cut = status.LastIndexOf(": ", StringComparison.Ordinal);
        string reason = (cut >= 0 ? status[(cut + 2)..] : status).Trim();
        if (reason.Length == 0 || reason == "연결 전") return "";
        return reason.EndsWith('.') ? reason + " " : reason + ". ";
    }

    void ShowNotice()
    {
        var c = host.Controller;
        bool show = c.Notice != null && c.Clock() - c.NoticeAt < NoticeLifetimeMs && c.Phase is SpeakerPhase.On or SpeakerPhase.Standby;
        if (show)
        {
            notice.Message = Korean.KeepWords(c.Notice!);
            notice.Severity = c.NoticeIsError ? InfoBarSeverity.Error
                : c.Notice!.Contains("상한", StringComparison.Ordinal) ? InfoBarSeverity.Warning
                : InfoBarSeverity.Informational;
        }
        notice.IsOpen = show;
        notice.Visibility = Show(show);
    }

    void RefreshVolume(bool switching)
    {
        var c = host.Controller;
        var s = c.State;
        int limit = c.EffectiveLimit;
        bool holdUserValue = dragging || Environment.TickCount64 - lastUserVolumeAt < UserVolumeHoldMs || sentTarget != null;
        if (!holdUserValue) slider.Value = s.Volume;
        int shown = (int)Math.Round(slider.Value);
        volumeNumber.Text = shown.ToString();
        volumeNumber.Opacity = shown != s.Volume ? 0.6 : 1;

        limitValue.Text = limit.ToString();
        limitSource.Text = SpeakerPresentation.LimitSource(c);
        limitPanel.Visibility = Show(!switching);
        switchingNote.Visibility = Show(switching);
        PlaceLimitTick();

        muteIcon.Glyph = Glyphs.ForVolume(s.Volume, s.Muted);
        string muteText = s.Muted ? "음소거 해제" : "음소거";
        ToolTipService.SetToolTip(muteButton, muteText);
        AutomationProperties.SetName(muteButton, muteText);
        muteButton.IsEnabled = !switching && c.Supports(DriverCapabilities.Mute);
        slider.IsEnabled = !switching && c.Supports(DriverCapabilities.Volume);
        volumeSection.Opacity = switching ? 0.5 : 1;
    }

    void PlaceLimitTick()
    {
        int limit = host.Controller.EffectiveLimit;
        double width = sliderHost.ActualWidth;
        limitTick.Visibility = Show(limit < 100 && width > 0);
        // The thumb (20 px) travels inside the track, so value v sits at 10 + (width - 20) * v / 100.
        limitTick.Margin = new Thickness(10 + (width - 20) * limit / 100.0 - 1, 0, 0, 0);
    }

    void RefreshTiles(bool standby, string? switchingTo)
    {
        var c = host.Controller;
        var sources = c.Driver!.Sources;
        string signature = string.Join(",", sources.Select(s => s.Id));
        if (signature != tileSignature) BuildTiles(sources, signature);

        bool busyOp = c.Operation is SpeakerOperation.Source or SpeakerOperation.PowerOn or SpeakerOperation.PowerOff;
        bool canPower = c.Supports(DriverCapabilities.Power);
        foreach (var tile in tileList)
        {
            bool target = switchingTo == tile.Id;
            bool current = !standby && c.State.SourceId == tile.Id;
            tile.Button.IsChecked = switchingTo != null ? target : current;
            tile.Button.IsEnabled = target || (!busyOp && pendingSource == null && (!standby || canPower));
            // The pressed tile's icon turns into the progress ring while the switch runs.
            tile.Ring.IsActive = target && (busyOp || pendingSource != null);
            tile.Ring.Visibility = Show(tile.Ring.IsActive);
            tile.Icon.Visibility = Show(!tile.Ring.IsActive);
            tile.LastInputRing.Visibility = Show(standby && switchingTo == null && c.LastActiveSource == tile.Id);
        }
    }

    void BuildTiles(IReadOnlyList<SourceOption> sources, string signature)
    {
        tileSignature = signature;
        tileList.Clear();
        tiles.Children.Clear();
        tiles.ColumnDefinitions.Clear();
        tiles.RowDefinitions.Clear();
        // One row of five on the LSX II LT; models with more inputs wrap to a second row.
        int columns = Math.Min(5, Math.Max(1, sources.Count));
        for (int i = 0; i < columns; i++) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = Ui.Star });
        for (int i = 0; i < (sources.Count + columns - 1) / columns; i++) tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            string label = SpeakerPresentation.ShortLabel(source.Label);
            var caption = new TextBlock { Text = label, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
            var icon = Ui.Icon(Glyphs.ForSource(source.Id), 16);
            var ring = new ProgressRing
            {
                Width = 16,
                Height = 16,
                MinWidth = 0,
                MinHeight = 0,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Style = Ui.Style("KefOnAccentRing"),
            };
            var glyph = new Grid { Height = 16, HorizontalAlignment = HorizontalAlignment.Center, Children = { icon, ring } };
            var content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { glyph, caption } };
            var button = new ToggleButton
            {
                Content = content,
                Height = 60,
                Padding = new Thickness(2, 6, 2, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(button, source.Label);
            AutomationProperties.SetName(button, source.Label);
            string id = source.Id;
            button.Click += (_, _) => OnTileClicked(id);

            var lastInput = new Border { Style = Ui.Style("KefLastInputRing"), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            var container = new Grid { Children = { button, lastInput } };
            Grid.SetColumn(container, i % columns);
            Grid.SetRow(container, i / columns);
            tiles.Children.Add(container);
            tileList.Add(new SourceTile(id, container, button, icon, ring, lastInput));
        }
    }

    async void OnTileClicked(string sourceId)
    {
        var c = host.Controller;
        bool standby = c.Phase == SpeakerPhase.Standby;
        if (!standby && c.State.SourceId == sourceId)
        {
            Refresh(); // undo the toggle on the tile that is already selected
            return;
        }
        pendingSource = sourceId;
        Refresh();
        try
        {
            await host.RunWhenIdle(() => standby ? c.PowerOnAsync(sourceId) : c.SelectSourceAsync(sourceId));
        }
        finally
        {
            pendingSource = null;
            Refresh();
        }
    }

    async void OnPowerToggled(object sender, RoutedEventArgs e)
    {
        if (updating) return;
        var c = host.Controller;
        bool turnOn = power.IsOn;
        await host.RunWhenIdle(() => turnOn ? c.PowerOnAsync() : c.PowerOffAsync());
        Refresh();
    }

    void OnSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (updating) return;
        var c = host.Controller;
        if (!c.IsReady) return;
        int requested = (int)Math.Round(e.NewValue);
        // Same rule as the controller: raising stops at the limit, lowering always works.
        int allowed = SpeakerController.ClampTarget(c.State.Volume, requested, c.EffectiveLimit);
        lastUserVolumeAt = Environment.TickCount64;
        if (allowed != requested)
        {
            updating = true;
            slider.Value = allowed;
            updating = false;
        }
        volumeNumber.Text = allowed.ToString();
        volumeNumber.Opacity = allowed != c.State.Volume ? 0.6 : 1;
        if (sentTarget == allowed) return;
        _ = SendVolume(allowed);
    }

    async Task SendVolume(int target)
    {
        sentTarget = target;
        try
        {
            // While a read runs the controller queues the newest target and applies it next.
            await host.Controller.SetVolumeTargetAsync(target);
        }
        finally
        {
            if (sentTarget == target) sentTarget = null;
            if (IsVisible) Refresh();
        }
    }

    void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        lastUserVolumeAt = Environment.TickCount64;
    }

    void RefreshFooter()
    {
        var bindings = host.HotkeyBindings;
        string? hint = SpeakerPresentation.VolumeKeysHint(bindings);
        string signature = hint ?? "";
        if (signature != keysSignature)
        {
            keysSignature = signature;
            keys.Children.Clear();
            if (hint != null)
            {
                if (hint.Contains(" / ", StringComparison.Ordinal))
                {
                    keys.Children.Add(Ui.Secondary(hint));
                }
                else
                {
                    var parts = hint.Split(" + ");
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (i > 0) keys.Children.Add(Ui.Secondary("+"));
                        foreach (var key in parts[i].Split(' ', StringSplitOptions.RemoveEmptyEntries)) keys.Children.Add(Keycap(key));
                    }
                }
                keys.Children.Add(Ui.Secondary("음량"));
                foreach (var child in keys.Children.OfType<TextBlock>()) child.VerticalAlignment = VerticalAlignment.Center;
            }
        }
        hotkeyWarning.Visibility = Show(host.Hotkeys.AnyInUse);
    }

    static Border Keycap(string key) => new()
    {
        Style = Ui.Style("KefKeycap"),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = key, FontSize = 11 },
    };

    static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
