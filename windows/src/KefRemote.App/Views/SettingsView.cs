using System.Diagnostics;
using System.Reflection;
using KefRemote.App.Controls;
using KefRemote.App.Native;
using KefRemote.Core;
using KefRemote.Desktop;
using KefRemote.Drivers.Kef;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace KefRemote.App.Views;

public enum SettingsPage
{
    General,
    Speaker,
    Volume,
    Hotkeys,
    About,
}

/// <summary>
/// Settings window in the shape of Windows 11 Settings (design W6): navigation on the left, rows as cards.
/// Changes apply at once; only the speaker address waits for "연결".
/// </summary>
sealed class SettingsView
{
    const double WindowWidth = 880, WindowHeight = 660;
    const string RepositoryUrl = "https://github.com/kimdaehee0824/Remote-for-KEF";

    readonly AppHost host;
    readonly Window window;
    readonly AppWindow appWindow;
    readonly NavigationView nav;
    readonly ScrollViewer scroller = new() { HorizontalScrollMode = ScrollMode.Disabled };
    readonly Dictionary<SettingsPage, NavigationViewItem> items = [];
    readonly Grid rootGrid;
    SettingsPage current;
    Action? refreshPage;
    bool updating;
    bool navigating;

    public event Action? Closed;

    /// <summary>The window content, for self-test captures.</summary>
    public FrameworkElement Root => rootGrid;

    public SettingsView(AppHost host, SettingsPage page)
    {
        this.host = host;

        var titleBar = new Grid { Height = 32, Padding = new Thickness(14, 0, 0, 0) };
        titleBar.Children.Add(Ui.Row(10, Ui.AppGlyph(16), new TextBlock { Text = "Remote for KEF 설정", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }));

        nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            IsPaneToggleButtonVisible = false,
            OpenPaneLength = 220,
            Content = scroller,
        };
        AddItem(SettingsPage.General, "일반", Glyphs.Home);
        AddItem(SettingsPage.Speaker, "스피커", Glyphs.Speaker);
        AddItem(SettingsPage.Volume, "음량", Glyphs.Volume);
        AddItem(SettingsPage.Hotkeys, "단축키", Glyphs.Keyboard);
        AddItem(SettingsPage.About, "정보", Glyphs.Info);
        nav.SelectionChanged += (_, e) =>
        {
            if (!navigating && e.SelectedItem is NavigationViewItem { Tag: SettingsPage p }) Navigate(p);
        };

        rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = Ui.Star });
        rootGrid.Children.Add(titleBar);
        Grid.SetRow(nav, 1);
        rootGrid.Children.Add(nav);
        if (host.SelfTest || !MicaController.IsSupported()) rootGrid.Style = Ui.Style("KefSolidSurface");

        window = new Window { Title = "Remote for KEF 설정", Content = rootGrid, ExtendsContentIntoTitleBar = true };
        if (!host.SelfTest && MicaController.IsSupported()) window.SystemBackdrop = new MicaBackdrop();
        window.SetTitleBar(titleBar);
        appWindow = WindowHelpers.AppWindowOf(window);
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        CenterOnPrimary();

        host.Changed += OnHostChanged;
        window.Closed += (_, _) =>
        {
            host.Changed -= OnHostChanged;
            Closed?.Invoke();
        };
        Navigate(page);
    }

    void AddItem(SettingsPage page, string text, string glyph)
    {
        var item = new NavigationViewItem { Content = text, Icon = new FontIcon { Glyph = glyph }, Tag = page };
        items[page] = item;
        nav.MenuItems.Add(item);
    }

    void CenterOnPrimary()
    {
        var work = DisplayArea.Primary.WorkArea;
        double scale = WindowHelpers.ScaleAt(new PointInt32(work.X + work.Width / 2, work.Y + work.Height / 2));
        int w = Math.Min((int)(WindowWidth * scale), work.Width), h = Math.Min((int)(WindowHeight * scale), work.Height);
        appWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
    }

    public void Show(SettingsPage page)
    {
        Navigate(page);
        appWindow.Show(true);
        window.Activate();
    }

    public void Close() => window.Close();

    public SettingsPage Current => current;

    public void Navigate(SettingsPage page)
    {
        current = page;
        navigating = true;
        nav.SelectedItem = items[page];
        navigating = false;
        var (content, refresh) = page switch
        {
            SettingsPage.Speaker => SpeakerPage(),
            SettingsPage.Volume => VolumePage(),
            SettingsPage.Hotkeys => HotkeysPage(),
            SettingsPage.About => AboutPage(),
            _ => GeneralPage(),
        };
        refreshPage = refresh;
        var page_ = new StackPanel { Spacing = 4, MaxWidth = 1000, Padding = new Thickness(32, 24, 32, 32), HorizontalAlignment = HorizontalAlignment.Stretch };
        page_.Children.Add(content);
        scroller.Content = page_;
        scroller.ChangeView(null, 0, null, true);
        Refresh();
        if (page == SettingsPage.Volume) _ = host.RefreshNowAsync();
    }

    void OnHostChanged() => Refresh();

    void Refresh()
    {
        updating = true;
        try
        {
            refreshPage?.Invoke();
        }
        finally
        {
            updating = false;
        }
    }

    // ---------- 일반 ----------

    (UIElement, Action?) GeneralPage()
    {
        var startup = new ToggleSwitch { OnContent = "켬", OffContent = "끔" };
        startup.Toggled += (_, _) =>
        {
            if (!updating) host.SetStartWithWindows(startup.IsOn);
        };
        var unstable = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsClosable = false,
            Title = "앱 폴더를 먼저 옮겨 주세요",
            Message = Korean.KeepWords("다운로드 폴더나 임시 폴더에서 실행 중입니다. 이 폴더를 정리하면 시작 시 실행이 동작하지 않습니다. 앱 폴더를 다른 곳으로 옮긴 뒤 켜세요. 옮긴 뒤에는 이 설정을 다시 켜야 합니다."),
            IsOpen = !host.SelfTest && StartupManager.IsUnstableLocation,
        };
        unstable.Visibility = unstable.IsOpen ? Visibility.Visible : Visibility.Collapsed;

        var openTaskbar = new Button { Content = "작업 표시줄 설정 열기" };
        openTaskbar.Click += async (_, _) => await Launcher.LaunchUriAsync(new Uri("ms-settings:taskbar"));

        var panel = Ui.Column(4,
            Ui.PageTitle("일반"),
            Ui.Card(Glyphs.Play, "Windows 시작 시 실행", "로그인하면 알림 영역에서 조용히 시작합니다", startup),
            unstable,
            Ui.GroupHeader("알림 영역 아이콘"),
            Ui.Card(Glyphs.Pin, "아이콘을 항상 보이게 두기",
                "Windows 11은 새 아이콘을 작업 표시줄의 ^ 안에 숨깁니다. 작업 표시줄 설정의 '기타 시스템 트레이 아이콘'에서 Remote for KEF를 켜 두면 한 번에 누를 수 있습니다.",
                openTaskbar));
        return (panel, () => startup.IsOn = host.SelfTest ? host.Settings.StartWithWindows : StartupManager.IsEnabled);
    }

    // ---------- 스피커 ----------

    (UIElement, Action?) SpeakerPage()
    {
        var hostBox = new TextBox { Text = host.Settings.Host, PlaceholderText = "예: 192.168.0.20", Width = 240 };
        var passwordBox = new PasswordBox { Password = Secrets.Unprotect(host.Settings.ProtectedPassword), Width = 240, PlaceholderText = "설정하지 않았다면 비워 두세요" };
        var connect = Ui.AccentButton("연결");
        var inputError = Ui.Text("", 12, style: "KefCriticalText");
        inputError.Visibility = Visibility.Collapsed;
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var ring = new ProgressRing { Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, IsActive = false, Visibility = Visibility.Collapsed };
        var statusText = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        var deviceCards = Ui.Column(4);

        async Task Connect()
        {
            try
            {
                HostAddress.ParseLocal(hostBox.Text);
            }
            catch (SpeakerException e)
            {
                inputError.Text = Korean.KeepWords(e.Message);
                inputError.Visibility = Visibility.Visible;
                return;
            }
            inputError.Visibility = Visibility.Collapsed;
            connect.IsEnabled = false;
            try
            {
                await host.ApplyConnectionAsync(hostBox.Text, passwordBox.Password);
            }
            finally
            {
                connect.IsEnabled = true;
            }
        }
        connect.Click += async (_, _) => await Connect();
        KeyEventHandler enter = async (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            await Connect();
        };
        hostBox.KeyDown += enter;
        passwordBox.KeyDown += enter;

        var panel = Ui.Column(4,
            Ui.PageTitle("스피커"),
            Ui.Card(Glyphs.Network, "IP 주소", "스피커의 사설 네트워크 IPv4 주소", hostBox),
            Ui.Card(Glyphs.Limit, "기기 웹페이지 암호", "KEF Connect에서 기기 암호를 설정한 경우에만 입력합니다. 이 PC의 사용자 계정으로 암호화해 저장합니다.", passwordBox),
            new Border
            {
                Style = Ui.Style("KefCard"),
                Child = Ui.Columns(12, (Ui.Auto, connect), (Ui.Star, Ui.Row(8, ring, dot, statusText))),
            },
            inputError,
            Ui.GroupHeader("기기 정보"),
            deviceCards,
            Ui.Secondary("IP 주소는 KEF Connect 앱의 스피커 정보나 공유기 관리 화면에서 확인할 수 있습니다. 공유기에서 이 스피커에 고정 IP를 지정해 두면 주소가 바뀌지 않습니다.", 12.5));
        ((FrameworkElement)panel.Children[^1]).Margin = new Thickness(2, 12, 0, 0);

        string deviceSignature = "-";
        void Refresh()
        {
            var c = host.Controller;
            bool configured = host.Settings.IsConfigured;
            ring.IsActive = host.IsConnecting;
            ring.Visibility = host.IsConnecting ? Visibility.Visible : Visibility.Collapsed;
            dot.Visibility = host.IsConnecting ? Visibility.Collapsed : Visibility.Visible;
            (string style, string text) = host.IsConnecting ? ("KefDotNeutral", "연결하는 중")
                : !configured ? ("KefDotNeutral", "주소를 입력하고 연결을 누르세요")
                : c.Phase switch
                {
                    SpeakerPhase.On => ("KefDotSuccess", "연결됨"),
                    SpeakerPhase.Standby => ("KefDotSuccess", "연결됨, 스피커는 대기 상태"),
                    _ => ("KefDotCritical", c.Status),
                };
            dot.Style = Ui.Style(style);
            statusText.Text = Korean.KeepWords(text);

            var d = c.Device;
            string signature = d == null ? "" : $"{d.Name}|{d.Model}|{d.Firmware}|{d.DeviceVolumeLimit}";
            if (signature == deviceSignature) return;
            deviceSignature = signature;
            deviceCards.Children.Clear();
            if (d == null)
            {
                deviceCards.Children.Add(Ui.Secondary("연결하면 이름, 모델, 펌웨어가 표시됩니다."));
                return;
            }
            deviceCards.Children.Add(Ui.Card(Glyphs.Speaker, d.Name, d.Model, null));
            deviceCards.Children.Add(FinishCard(d.Model));
            deviceCards.Children.Add(Ui.Card(Glyphs.Info, "펌웨어", d.Firmware, null));
        }
        return (panel, Refresh);
    }

    /// <summary>The finish the flyout draws the speaker in. Speakers do not report it, so the user picks it.</summary>
    Border FinishCard(string model)
    {
        var finishes = KefFinishes.For(model);
        bool otherModels = finishes.Any(f => f.Model != model);
        var box = new ComboBox { MinWidth = 200 };
        box.Items.Add(FinishItem(null, "앱 기본 색상"));
        foreach (var f in finishes) box.Items.Add(FinishItem(f, otherModels ? $"{f.Model} {f.Name}" : f.Name));
        var current = KefFinishes.Find(host.Settings.SpeakerFinish, model);
        box.SelectedIndex = current == null ? 0 : finishes.ToList().IndexOf(current) + 1;
        box.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            host.Settings.SpeakerFinish = box.SelectedIndex > 0 ? finishes[box.SelectedIndex - 1].Id : null;
            host.SaveSettings();
        };
        return Ui.Card(Glyphs.Color, "스피커 색상", "플라이아웃의 스피커 그림을 이 색으로 그립니다. 스피커가 색상을 알려 주지 않아 직접 고릅니다.", box);
    }

    static StackPanel FinishItem(KefFinish? finish, string name) =>
        Ui.Row(10, Ui.AppGlyph(20, finish), new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });

    // ---------- 음량 ----------

    (UIElement, Action?) VolumePage()
    {
        var limitBox = new NumberBox
        {
            Minimum = 1,
            Maximum = 100,
            SmallChange = 1,
            LargeChange = 10,
            Value = host.Settings.AppVolumeLimit,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 130,
        };
        limitBox.ValueChanged += (_, e) =>
        {
            if (updating) return;
            if (double.IsNaN(e.NewValue))
            {
                limitBox.Value = host.Settings.AppVolumeLimit;
                return;
            }
            host.Settings.AppVolumeLimit = Math.Clamp((int)Math.Round(e.NewValue), 1, 100);
            host.SaveSettings();
        };
        var deviceLimit = new TextBlock { Style = Ui.Style("KefSecondaryText"), FontSize = 14 };
        var effective = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };

        var approach = new ToggleSwitch { OnContent = "켬", OffContent = "끔" };
        approach.Toggled += (_, _) =>
        {
            if (updating) return;
            host.Settings.ApproachFromAbove = approach.IsOn;
            host.SaveSettings();
        };
        var help = Ui.HelpButton("정확한 음량 맞추기",
            "같은 숫자라도 아래에서 올라오면 더 작게 들리는 KEF 특성을 보정합니다.",
            out var tip,
            "정확한 음량 맞추기",
            "KEF 스피커는 같은 숫자라도 아래에서 올라올 때 더 작게 들리는 구간이 있습니다. 켜면 음량을 올릴 때 목표보다 조금 높은 값을 먼저 보내고 0.1초 뒤 목표 값을 보내, 위에서 내려와 맞춘 것과 같은 크기로 들리게 합니다.",
            "예: 20으로 올리면 22를 거쳐 20, 12로 올리면 17을 거쳐 12가 됩니다. 5 이하와 41 이상은 바로 보냅니다. 잠깐 거치는 값도 상한을 넘지 않고, 내릴 때는 그대로 보냅니다. LSX II(USB 입력)에서 잰 표를 쓰며 LSX II LT에서는 아직 확인하지 않았습니다.");
        var approachTitle = Ui.Row(0, Ui.Text("정확한 음량 맞추기"), help);

        var panel = Ui.Column(4,
            Ui.PageTitle("음량"),
            Ui.Card(Glyphs.Limit, "앱 음량 상한", "이 앱에서 올릴 수 있는 최대 음량입니다. 내리는 것은 언제나 됩니다.", limitBox),
            Ui.Card(Glyphs.Speaker, "KEF 기기 최대 음량", "KEF Connect에서 정한 값입니다. 앱 상한과 둘 중 낮은 값을 씁니다.", deviceLimit),
            Ui.Card(Glyphs.Volume, "지금 적용되는 상한", "플라이아웃 슬라이더의 주황색 눈금입니다.", effective),
            Ui.GroupHeader("음량 보정"),
            Ui.Card(Glyphs.Volume, approachTitle, "올릴 때 목표보다 살짝 높게 보낸 뒤 목표로 내립니다", approach, out _),
            tip);

        void Refresh()
        {
            var c = host.Controller;
            if (!limitBox.IsLoaded || limitBox.FocusState == FocusState.Unfocused) limitBox.Value = host.Settings.AppVolumeLimit;
            deviceLimit.Text = c.Device == null ? "연결 후 표시" : c.Device.DeviceVolumeLimit is int d ? d.ToString() : "제한 없음";
            effective.Text = $"{c.EffectiveLimit} ({SpeakerPresentation.LimitSource(c)})";
            approach.IsOn = host.Settings.ApproachFromAbove;
        }
        return (panel, Refresh);
    }

    // ---------- 단축키 ----------

    (UIElement, Action?) HotkeysPage()
    {
        var panel = Ui.Column(4, Ui.PageTitle("단축키"));
        panel.Children.Add(Ui.Secondary("칸을 누른 뒤 새 키 조합을 누르세요. Esc는 취소, Backspace는 끄기입니다.", 13));
        ((FrameworkElement)panel.Children[^1]).Margin = new Thickness(2, 0, 0, 8);

        var osd = new ToggleSwitch { OnContent = "켬", OffContent = "끔" };
        osd.Toggled += (_, _) =>
        {
            if (updating) return;
            host.Settings.ShowVolumeOsd = osd.IsOn;
            host.SaveSettings();
        };
        var step = new ComboBox { Width = 170 };
        foreach (var (label, value) in new[] { ("1", 1), ("2", 2), ("3", 3), ("5", 5), ("체감 음량 기준", VolumeSteps.Perceptual) })
            step.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        step.SelectionChanged += (_, _) =>
        {
            if (updating || step.SelectedItem is not ComboBoxItem { Tag: int value }) return;
            host.Settings.HotkeyVolumeStep = value;
            host.SaveSettings();
        };
        var help = Ui.HelpButton("체감 음량 기준",
            "숫자 간격 대신 실제 소리 크기가 비슷하게 늘도록 움직입니다.",
            out var tip,
            "체감 음량 기준",
            "숫자를 일정하게 더하는 대신, 소리 크기가 비슷한 간격으로 늘도록 미리 잰 단계(12, 15, 19, 22, 25, 29 …)를 따라 움직입니다. 낮은 음량에서는 숫자가 크게, 높은 음량에서는 작게 움직입니다.",
            "65보다 위에서는 4씩 움직이고, 12 아래에서 올리면 12로, 내리면 0으로 갑니다. 상한은 그대로 지킵니다.");

        var rows = new List<(HotkeyAction action, HotkeyCaptureBox box, TextBlock status)>();
        foreach (var binding in host.HotkeyBindings)
        {
            var box = new HotkeyCaptureBox(binding, host.Hotkeys);
            string glyph = binding.Action switch
            {
                HotkeyAction.VolumeUp => Glyphs.Up,
                HotkeyAction.VolumeDown => Glyphs.Down,
                _ => Glyphs.Mute,
            };
            var card = Ui.Card(glyph, Ui.Text(HotkeyBinding.ActionName(binding.Action)), "", box.View, out var status);
            box.Captured += next => OnCaptured(next, status!);
            rows.Add((binding.Action, box, status!));
            panel.Children.Add(card);
        }

        var reset = new Button { Content = "기본값으로 되돌리기", Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(reset);

        panel.Children.Add(Ui.GroupHeader("동작"));
        panel.Children.Add(Ui.Card(Glyphs.Sort, Ui.Row(0, Ui.Text("한 번 누를 때 음량"), help), "단축키와 음량 표시창의 마우스 휠에 적용됩니다", step, out _));
        panel.Children.Add(tip);
        panel.Children.Add(Ui.Card(Glyphs.Message, "음량을 바꿀 때 화면 오른쪽 아래에 표시", "꺼 두어도 연결 문제나 상한 같은 오류는 표시합니다", osd));

        panel.Children.Add(Ui.GroupHeader("다른 앱과 겹칠 수 있는 조합"));
        panel.Children.Add(Ui.Secondary(
            "이 앱이 켜져 있는 동안에는 등록한 조합을 이 앱이 먼저 받습니다. 기본값 Ctrl + Alt + ↑ / ↓는 VS Code(커서 추가), JetBrains IDE(이전·다음 검색 결과), Visual Studio(Ctrl + Alt + ↓, 열린 파일 목록)와 같고, Ctrl + Alt + M은 JetBrains(메서드 추출)와 Word(메모 삽입)와 같습니다. Intel 그래픽 드라이버의 화면 회전 단축키(Ctrl + Alt + 화살표)를 켜 둔 PC에서도 겹칩니다. 이런 프로그램을 자주 쓰면 다른 조합으로 바꾸세요. 다른 앱이 먼저 등록한 조합은 위에 '등록하지 못했습니다'로 표시됩니다.",
            12.5));

        reset.Click += (_, _) =>
        {
            host.Settings.Hotkeys = [];
            host.SaveSettings();
            host.ApplyHotkeys();
            foreach (var (action, box, _) in rows) box.SetBinding(host.HotkeyBindings.First(b => b.Action == action));
            Refresh();
        };

        void OnCaptured(HotkeyBinding next, TextBlock status)
        {
            var proposed = host.HotkeyBindings.Select(b => b.Action == next.Action ? next : b).ToList();
            string? error = HotkeyBinding.ValidateSet(proposed);
            if (error != null)
            {
                status.Text = Korean.KeepWords(error);
                status.Style = Ui.Style("KefCriticalText");
                return;
            }
            host.Settings.Hotkeys = proposed;
            host.SaveSettings();
            host.ApplyHotkeys();
            foreach (var (action, box, _) in rows) box.SetBinding(proposed.First(b => b.Action == action));
            Refresh();
        }

        void Refresh()
        {
            foreach (var (action, _, status) in rows)
            {
                var registration = host.Hotkeys.Status.GetValueOrDefault(action);
                (status.Text, status.Style) = registration switch
                {
                    HotkeyRegistration.Registered => ("사용 중", Ui.Style("KefSecondaryText")),
                    HotkeyRegistration.InUse => ("다른 앱이 이미 쓰고 있어 등록하지 못했습니다. 다른 조합으로 바꿔 주세요.", Ui.Style("KefCriticalText")),
                    _ => ("꺼짐", Ui.Style("KefSecondaryText")),
                };
            }
            osd.IsOn = host.Settings.ShowVolumeOsd;
            step.SelectedItem = step.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int v && v == host.Settings.HotkeyVolumeStep)
                ?? step.Items[0];
        }
        return (panel, Refresh);
    }

    // ---------- 정보 ----------

    (UIElement, Action?) AboutPage()
    {
        string version = typeof(SettingsView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];

        var github = new HyperlinkButton { Content = "GitHub에서 열기", NavigateUri = new Uri(RepositoryUrl) };
        var openFolder = new Button { Content = "폴더 열기" };
        openFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(host.SettingsFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{host.SettingsFolder}\"") { UseShellExecute = true });
        };

        var heading = Ui.Row(16,
            Ui.AppGlyph(56),
            Ui.Column(2,
                new TextBlock { Text = "Remote for KEF", FontSize = 20, FontWeight = FontWeights.SemiBold },
                Ui.Secondary("버전 " + version, 13),
                Ui.Secondary("A Very Revolutionary Way to Control Your KEF Speakers.", 13)));
        heading.Margin = new Thickness(0, 0, 0, 12);

        var panel = Ui.Column(4,
            Ui.PageTitle("정보"),
            heading,
            Ui.Card(Glyphs.Link, "소스 코드와 문제 신고", RepositoryUrl, github),
            Ui.Card(Glyphs.Info, "라이선스", "MIT License", null),
            Ui.GroupHeader("오픈소스 고지"),
            Ui.Card(null, "kef-lsx-volume", "음량 보정 표와 체감 음량 단계의 측정값을 사용합니다. MIT License, Copyright (c) 2026 Robert Oberdorfer", null),
            Ui.Card(null, "Windows App SDK", "MIT License, Copyright (c) Microsoft Corporation", null),
            Ui.Card(null, ".NET", "MIT License, Copyright (c) .NET Foundation and Contributors", null),
            Ui.GroupHeader("문제 해결"),
            Ui.Card(Glyphs.Folder, "설정과 로그 폴더", host.SettingsFolder, openFolder));
        return (panel, null);
    }
}
