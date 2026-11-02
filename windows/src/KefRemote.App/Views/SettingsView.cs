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
    Hotkeys,
    About,
}

/// <summary>
/// Settings window in the shape of Windows 11 Settings (design W6): navigation on the left, rows as cards.
/// Changes apply at once; only the speaker address waits for Connect. The speaker page lists the speakers and opens
/// one per page with everything that belongs to it: address, model and finish, volume limit and correction.
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
    // On the speaker page: the speaker shown, or adding one when null with adding set, else the list.
    SpeakerSlot? speakerDetail;
    bool addingSpeaker;
    Action? refreshPage;
    bool updating;
    bool navigating;

    public event Action? Closed;

    /// <summary>The window content, for self-test captures.</summary>
    public FrameworkElement Root => rootGrid;

    public SettingsView(AppHost host, SettingsPage page, SpeakerSlot? speaker = null)
    {
        this.host = host;

        var titleBar = new Grid { Height = 32, Padding = new Thickness(14, 0, 0, 0) };
        titleBar.Children.Add(Ui.Row(10, Ui.AppGlyph(16), new TextBlock { Text = Loc.T("Remote for KEF 설정", "Remote for KEF Settings", "Remote for KEF 設定"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }));

        nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            IsPaneToggleButtonVisible = false,
            OpenPaneLength = 220,
            Content = scroller,
        };
        AddItem(SettingsPage.General, Loc.T("일반", "General", "一般"), Glyphs.Home);
        AddItem(SettingsPage.Speaker, Loc.T("스피커", "Speakers", "スピーカー"), Glyphs.Speaker);
        AddItem(SettingsPage.Hotkeys, Loc.T("단축키", "Shortcuts", "ショートカット"), Glyphs.Keyboard);
        AddItem(SettingsPage.About, Loc.T("정보", "About", "バージョン情報"), Glyphs.Info);
        nav.SelectionChanged += (_, e) =>
        {
            if (!navigating && e.SelectedItem is NavigationViewItem { Tag: SettingsPage p }) Navigate(p);
        };
        // Picking "Speakers" while a speaker's page is open goes back to the list, as in Windows Settings.
        nav.ItemInvoked += (_, e) =>
        {
            if (e.InvokedItemContainer is NavigationViewItem { Tag: SettingsPage.Speaker } && current == SettingsPage.Speaker && (speakerDetail != null || addingSpeaker))
                ShowSpeakerList();
        };

        rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = Ui.Star });
        rootGrid.Children.Add(titleBar);
        Grid.SetRow(nav, 1);
        rootGrid.Children.Add(nav);
        if (host.SelfTest || !MicaController.IsSupported()) rootGrid.Style = Ui.Style("KefSolidSurface");

        rootGrid.Language = Loc.Tag;
        window = new Window { Title = Loc.T("Remote for KEF 설정", "Remote for KEF Settings", "Remote for KEF 設定"), Content = rootGrid, ExtendsContentIntoTitleBar = true };
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
        current = page;
        speakerDetail = speaker;
        Render();
    }

    void AddItem(SettingsPage page, string text, string glyph)
    {
        var item = new NavigationViewItem { Content = text, Icon = new FontIcon { Glyph = glyph }, Tag = page };
        items[page] = item;
        nav.MenuItems.Add(item);
    }

    void CenterOnPrimary()
    {
        var work = WindowHelpers.FreeArea(DisplayArea.Primary).Area;
        double scale = WindowHelpers.ScaleAt(new PointInt32(work.X + work.Width / 2, work.Y + work.Height / 2));
        int w = Math.Min((int)(WindowWidth * scale), work.Width), h = Math.Min((int)(WindowHeight * scale), work.Height);
        appWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
    }

    public void Show(SettingsPage page, SpeakerSlot? speaker = null)
    {
        current = page;
        speakerDetail = page == SettingsPage.Speaker ? speaker : null;
        addingSpeaker = false;
        Render();
        appWindow.Show(true);
        window.Activate();
    }

    public void Close() => window.Close();

    public SettingsPage Current => current;

    /// <summary>A page from the navigation pane. The speaker page opens on its list.</summary>
    public void Navigate(SettingsPage page)
    {
        if (page != current)
        {
            speakerDetail = null;
            addingSpeaker = false;
        }
        current = page;
        Render();
    }

    void Render()
    {
        var page = current;
        navigating = true;
        nav.SelectedItem = items[page];
        navigating = false;
        var (content, refresh) = page switch
        {
            SettingsPage.Speaker => SpeakerPage(),
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
        // Picks up a maximum volume changed in KEF Connect, and reads a speaker that isn't the selected one.
        if (page == SettingsPage.Speaker && speakerDetail != null) _ = host.RefreshSpeakerAsync(speakerDetail);
    }

    void ShowSpeakerList() => ShowSpeakerPage(null, adding: false);

    void ShowSpeaker(SpeakerSlot slot) => ShowSpeakerPage(slot, adding: false);

    void ShowAddSpeaker() => ShowSpeakerPage(null, adding: true);

    void ShowSpeakerPage(SpeakerSlot? slot, bool adding)
    {
        current = SettingsPage.Speaker;
        speakerDetail = slot;
        addingSpeaker = adding;
        Render();
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

    // ---------- General ----------

    (UIElement, Action?) GeneralPage()
    {
        var startup = new ToggleSwitch { OnContent = Loc.T("켬", "On", "オン"), OffContent = Loc.T("끔", "Off", "オフ") };
        startup.Toggled += (_, _) =>
        {
            if (!updating) host.SetStartWithWindows(startup.IsOn);
        };
        var unstable = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsClosable = false,
            Title = Loc.T("앱 폴더를 먼저 옮겨 주세요", "Move the app folder first", "先にアプリのフォルダーを移動してください"),
            Message = Korean.KeepWords(Loc.T("다운로드 폴더나 임시 폴더에서 실행 중입니다. 이 폴더를 정리하면 시작 시 실행이 동작하지 않습니다. 앱 폴더를 다른 곳으로 옮긴 뒤 켜세요. 옮긴 뒤에는 이 설정을 다시 켜야 합니다.", "The app is running from the Downloads folder or a temporary folder, and starting with Windows stops working once that folder is cleaned up. Move the app folder somewhere else before turning this on. If you move it later, turn this setting on again.", "ダウンロードフォルダーか一時フォルダーから実行しています。このフォルダーが整理されると、Windows起動時の実行が動作しなくなります。アプリのフォルダーを別の場所に移動してからオンにしてください。移動した後は、この設定をもう一度オンにする必要があります。")),
            IsOpen = !host.SelfTest && StartupManager.IsUnstableLocation,
        };
        unstable.Visibility = unstable.IsOpen ? Visibility.Visible : Visibility.Collapsed;

        var nowPlaying = new ToggleSwitch { OnContent = Loc.T("켬", "On", "オン"), OffContent = Loc.T("끔", "Off", "オフ") };
        nowPlaying.Toggled += (_, _) =>
        {
            if (updating) return;
            host.Settings.ShowNowPlaying = nowPlaying.IsOn;
            host.SaveSettings();
        };

        var openTaskbar = new Button { Content = Loc.T("작업 표시줄 설정 열기", "Open taskbar settings", "タスクバーの設定を開く") };
        openTaskbar.Click += async (_, _) => await Launcher.LaunchUriAsync(new Uri("ms-settings:taskbar"));

        var (updates, refreshUpdates) = UpdateCards();
        var panel = Ui.Column(4,
            Ui.PageTitle(Loc.T("일반", "General", "一般")),
            Ui.Card(Glyphs.Play, Loc.T("Windows 시작 시 실행", "Start with Windows", "Windows起動時に実行"), Loc.T("로그인하면 알림 영역에서 조용히 시작합니다", "Starts quietly in the notification area when you sign in", "サインインすると通知領域で静かに起動します"), startup),
            unstable,
            Ui.GroupHeader(Loc.T("플라이아웃", "Flyout", "フライアウト")),
            Ui.Card(Glyphs.Audio, Loc.T("재생 중인 곡 표시", "Show what's playing", "再生中の曲を表示"),
                Loc.T("Wi-Fi나 블루투스로 재생할 때 플라이아웃 위에 곡 정보와 재생 버튼을 띄웁니다", "When the speaker plays over Wi-Fi or Bluetooth, shows the track and playback buttons above the flyout", "Wi-FiやBluetoothで再生しているとき、フライアウトの上に曲の情報と再生ボタンを表示します"),
                nowPlaying),
            Ui.GroupHeader(Loc.T("알림 영역 아이콘", "Notification area icon", "通知領域のアイコン")),
            Ui.Card(Glyphs.Pin, Loc.T("아이콘을 항상 보이게 두기", "Keep the icon visible", "アイコンを常に表示する"),
                Loc.T("Windows 11은 새 아이콘을 작업 표시줄의 ^ 안에 숨깁니다. 작업 표시줄 설정의 '기타 시스템 트레이 아이콘'에서 Remote for KEF를 켜 두면 한 번에 누를 수 있습니다.", "Windows 11 hides new icons behind the ^ on the taskbar. Turn on Remote for KEF under 'Other system tray icons' in taskbar settings to reach it in one click.", "Windows 11は新しいアイコンをタスクバーの ^ の中に隠します。タスクバーの設定の「その他のシステム トレイ アイコン」でRemote for KEFをオンにすると、ワンクリックで開けます。"),
                openTaskbar),
            Ui.GroupHeader(Loc.T("업데이트", "Updates", "アップデート")),
            updates);
        return (panel, () =>
        {
            startup.IsOn = host.SelfTest ? host.Settings.StartWithWindows : StartupManager.IsEnabled;
            nowPlaying.IsOn = host.Settings.ShowNowPlaying;
            refreshUpdates();
        });
    }

    /// <summary>Version with the check button, and the automatic update switch (the PowerToys General page layout).</summary>
    (UIElement, Action) UpdateCards()
    {
        var updater = host.Updates;
        var check = new Button { Content = Loc.T("업데이트 확인", "Check for updates", "アップデートを確認") };
        check.Click += async (_, _) => await host.CheckForUpdatesAsync(userInitiated: true);
        var restart = Ui.AccentButton(Loc.T("다시 시작해 업데이트", "Restart to update", "再起動してアップデート"));
        restart.Click += (_, _) => host.RestartToUpdate();
        var ring = new ProgressRing { Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, IsActive = false, Visibility = Visibility.Collapsed };
        var releases = new HyperlinkButton { Content = Loc.T("릴리스 열기", "Open releases", "リリースを開く"), NavigateUri = new Uri(AppUpdater.RepositoryUrl + "/releases/latest") };
        var versionCard = Ui.Card(Glyphs.Update, Ui.Text("Remote for KEF " + AppVersion()), "", Ui.Row(12, ring, check, restart, releases), out var status);

        var auto = new ToggleSwitch { OnContent = Loc.T("켬", "On", "オン"), OffContent = Loc.T("끔", "Off", "オフ") };
        auto.Toggled += (_, _) =>
        {
            if (!updating) host.SetAutoUpdate(auto.IsOn);
        };
        var autoCard = Ui.Card(Glyphs.Download, Loc.T("자동으로 업데이트", "Update automatically", "自動でアップデート"),
            Loc.T("하루에 한 번 GitHub에서 새 버전을 확인해 내려받고, 다음에 앱을 시작할 때 적용합니다", "Checks GitHub for a new version once a day, downloads it and applies it the next time the app starts", "1日に1回GitHubで新しいバージョンを確認してダウンロードし、次にアプリを起動したときに適用します"),
            auto);

        void Refresh()
        {
            var stage = updater.Stage;
            bool installed = stage != UpdateStage.Unavailable;
            string? v = updater.NewVersion;
            string text = stage switch
            {
                UpdateStage.Unavailable => Loc.T("이 복사본은 스스로 업데이트하지 않습니다. 릴리스 페이지에서 설치 파일이나 zip을 받아 주세요.", "This copy doesn't update itself. Get the installer or the zip from the releases page.", "このコピーは自動でアップデートしません。リリースページからインストーラーかzipを入手してください。"),
                UpdateStage.Checking => Loc.T("확인하는 중", "Checking", "確認中"),
                UpdateStage.UpToDate => Loc.T("최신 버전입니다", "You're up to date", "最新のバージョンです"),
                UpdateStage.Downloading => Loc.T($"{v} 버전을 내려받는 중 ({updater.Progress}%)", $"Downloading version {v} ({updater.Progress}%)", $"バージョン {v} をダウンロード中（{updater.Progress}%）"),
                UpdateStage.Ready => Loc.T($"{v} 버전을 받았습니다. 다시 시작하면 적용됩니다.", $"Version {v} is ready. Restart the app to apply it.", $"バージョン {v} の準備ができました。再起動すると適用されます。"),
                UpdateStage.Failed => Loc.T("업데이트를 확인하지 못했습니다. 인터넷 연결을 확인하고 다시 시도하세요.", "Couldn't check for updates. Check the internet connection and try again.", "アップデートを確認できませんでした。インターネット接続を確認して、もう一度お試しください。"),
                _ => host.Settings.LastUpdateCheck is DateTime last
                    ? Loc.T("마지막 확인 ", "Last checked ", "最終確認 ") + last.ToLocalTime().ToString("g", System.Globalization.CultureInfo.GetCultureInfo(Loc.Tag))
                    : Loc.T("아직 확인하지 않았습니다", "Not checked yet", "まだ確認していません"),
            };
            if (status != null) status.Text = Korean.KeepWords(text);
            bool busy = stage is UpdateStage.Checking or UpdateStage.Downloading;
            ring.IsActive = busy;
            ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            check.IsEnabled = updater.CanCheck;
            check.Visibility = installed && stage != UpdateStage.Ready ? Visibility.Visible : Visibility.Collapsed;
            restart.Visibility = stage == UpdateStage.Ready ? Visibility.Visible : Visibility.Collapsed;
            releases.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
            auto.IsOn = host.Settings.AutoUpdate;
            auto.IsEnabled = installed;
        }
        return (Ui.Column(4, versionCard, autoCard), Refresh);
    }

    static string AppVersion()
    {
        string version = typeof(SettingsView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }

    // ---------- Speakers ----------

    (UIElement, Action?) SpeakerPage()
    {
        if (speakerDetail != null && host.Speakers.Contains(speakerDetail)) return SpeakerDetailPage(speakerDetail);
        speakerDetail = null;
        // One speaker: its page is the speaker page, as before there could be several.
        if (!addingSpeaker && host.Speakers.Count == 1) return SpeakerDetailPage(host.Speakers[0]);
        // With no speaker yet the page is the form for the first one, as before there could be several.
        if (addingSpeaker || host.Speakers.Count == 0) return AddSpeakerPage(first: host.Speakers.Count == 0);
        return SpeakerListPage();
    }

    static string SpeakersTitle => Loc.T("스피커", "Speakers", "スピーカー");

    /// <summary>Windows Settings' "Sound > Output" list: one row per speaker, each opening its own page.</summary>
    (UIElement, Action?) SpeakerListPage()
    {
        var add = new Button { Content = Ui.Row(8, Ui.Icon(Glyphs.Add, 14), new TextBlock { Text = Loc.T("스피커 추가", "Add a speaker", "スピーカーを追加") }) };
        add.Click += (_, _) => ShowAddSpeaker();
        var rows = Ui.Column(4);
        var panel = Ui.Column(4,
            Ui.PageTitle(SpeakersTitle),
            Ui.Card(Glyphs.Speaker, Loc.T("선택한 스피커", "Selected speaker", "選択中のスピーカー"),
                Loc.T("플라이아웃, 알림 영역 아이콘, 단축키는 선택한 스피커를 조작합니다. 플라이아웃에서 스피커 이름을 눌러 바꿉니다.", "The flyout, the notification area icon and the shortcuts work on the selected speaker. Select the speaker's name in the flyout to switch.", "フライアウト、通知領域のアイコン、ショートカットは選択中のスピーカーを操作します。フライアウトでスピーカー名を押すと切り替えられます。"),
                add),
            Ui.GroupHeader(Loc.T("등록한 스피커", "Your speakers", "登録したスピーカー")),
            rows);

        var built = new List<(SpeakerSlot slot, ContentPresenter icon, TextBlock name, TextBlock detail, TextBlock state)>();
        foreach (var slot in host.Speakers)
        {
            var icon = new ContentPresenter { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = Ui.Secondary("");
            var state = Ui.Secondary("");
            state.VerticalAlignment = VerticalAlignment.Center;
            var chevronIcon = Ui.Icon(Glyphs.ChevronRight, 12);
            chevronIcon.VerticalAlignment = VerticalAlignment.Center;
            var text = Ui.Column(2, name, detail);
            text.VerticalAlignment = VerticalAlignment.Center;
            var grid = Ui.Columns(16, (Ui.Auto, icon), (Ui.Star, text), (Ui.Auto, state), (Ui.Auto, chevronIcon));
            var button = new Button
            {
                Content = grid,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 12, 16, 12),
                MinHeight = 68,
            };
            button.Click += (_, _) => ShowSpeaker(slot);
            rows.Children.Add(button);
            built.Add((slot, icon, name, detail, state));
        }

        void Refresh()
        {
            foreach (var (slot, icon, name, detail, state) in built)
            {
                name.Text = slot.Name;
                var c = slot.Controller;
                bool reachable = c.Phase is SpeakerPhase.On or SpeakerPhase.Standby;
                // The model, else the address unless the address is already the name.
                string model = c.Device?.Model ?? slot.Entry.Model ?? (slot.Name == slot.Entry.Host ? "" : slot.Entry.Host);
                detail.Text = slot.Unreachable && !reachable ? SpeakerPresentation.NotReachable : model;
                detail.Visibility = detail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                detail.Style = Ui.Style("KefSecondaryText");
                detail.FontSize = 12;
                state.Text = slot == host.Selected ? Loc.T("선택됨", "Selected", "選択中") : "";
                icon.Content = Ui.AppGlyph(32, slot.Finish, slot.ShownModel);
                AutomationPropertiesName(icon, slot.Name);
            }
        }
        return (panel, Refresh);
    }

    static void AutomationPropertiesName(UIElement element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);

    /// <summary>The page title as Windows Settings writes a subpage: "Speakers > name", the first part going back.</summary>
    BreadcrumbBar Breadcrumb(string here)
    {
        var bar = new BreadcrumbBar { ItemsSource = new[] { SpeakersTitle, here }, Margin = new Thickness(0, 4, 0, 16) };
        bar.Resources["BreadcrumbBarItemThemeFontSize"] = 28.0;
        bar.Resources["BreadcrumbBarChevronFontSize"] = 16.0;
        bar.Resources["BreadcrumbBarItemFontWeight"] = FontWeights.SemiBold;
        bar.ItemClicked += (_, e) =>
        {
            if (e.Index == 0) ShowSpeakerList();
        };
        return bar;
    }

    /// <summary>Address and password, then Connect. The first speaker's form is the whole page.</summary>
    (UIElement, Action?) AddSpeakerPage(bool first)
    {
        var (form, hostBox, passwordBox, connect, inputError) = ConnectionCards(Loc.T("추가", "Add", "追加"), "", "");
        connect.Click += async (_, _) => await Add();
        KeyEventHandler enter = async (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            await Add();
        };
        hostBox.KeyDown += enter;
        passwordBox.KeyDown += enter;

        async Task Add()
        {
            if (!CheckAddress(hostBox.Text, inputError, null)) return;
            connect.IsEnabled = false;
            try
            {
                var slot = await host.AddSpeakerAsync(hostBox.Text, passwordBox.Password);
                ShowSpeaker(slot);
            }
            finally
            {
                connect.IsEnabled = true;
            }
        }

        var panel = Ui.Column(4,
            first ? Ui.PageTitle(SpeakersTitle) : Breadcrumb(Loc.T("스피커 추가", "Add a speaker", "スピーカーを追加")));
        foreach (var card in form) panel.Children.Add(card);
        panel.Children.Add(AddressHint());
        return (panel, null);
    }

    static TextBlock AddressHint()
    {
        var hint = Ui.Secondary(Loc.T("IP 주소는 KEF Connect 앱의 스피커 정보나 공유기 관리 화면에서 확인할 수 있습니다. 공유기에서 이 스피커에 고정 IP를 지정해 두면 주소가 바뀌지 않습니다.", "You can find the IP address in the speaker info of the KEF Connect app or on your router's admin page. Reserve a fixed IP for this speaker on the router so the address doesn't change.", "IPアドレスはKEF Connectアプリのスピーカー情報やルーターの管理画面で確認できます。ルーターでこのスピーカーに固定IPを割り当てておくと、アドレスが変わりません。"), 12.5);
        hint.Margin = new Thickness(2, 12, 0, 0);
        return hint;
    }

    bool CheckAddress(string address, TextBlock error, SpeakerSlot? self)
    {
        string? problem = null;
        try
        {
            HostAddress.ParseLocal(address);
        }
        catch (SpeakerException e)
        {
            problem = e.Message;
        }
        if (problem == null && SpeakerList.WithHost(host.Settings, address, self?.Id) is { } other)
            problem = Loc.T($"이미 추가한 스피커입니다: {SpeakerList.DisplayName(other)}", $"Already added: {SpeakerList.DisplayName(other)}", $"すでに追加したスピーカーです: {SpeakerList.DisplayName(other)}");
        error.Text = Korean.KeepWords(problem ?? "");
        error.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
        return problem == null;
    }

    (List<UIElement> cards, TextBox hostBox, PasswordBox passwordBox, Button connect, TextBlock inputError) ConnectionCards(string action, string address, string password, UIElement? status = null)
    {
        var hostBox = new TextBox { Text = address, PlaceholderText = Loc.T("예: 192.168.0.20", "e.g. 192.168.0.20", "例: 192.168.0.20"), Width = 240 };
        var passwordBox = new PasswordBox { Password = password, Width = 240, PlaceholderText = Loc.T("설정하지 않았다면 비워 두세요", "Leave empty if you haven't set one", "設定していなければ空欄") };
        var connect = Ui.AccentButton(action);
        var inputError = Ui.Text("", 12, style: "KefCriticalText");
        inputError.Visibility = Visibility.Collapsed;
        var cards = new List<UIElement>
        {
            Ui.Card(Glyphs.Network, Loc.T("IP 주소", "IP address", "IPアドレス"), Loc.T("스피커의 사설 네트워크 IPv4 주소", "The speaker's private network IPv4 address", "スピーカーのプライベートネットワークのIPv4アドレス"), hostBox),
            Ui.Card(Glyphs.Limit, Loc.T("기기 웹페이지 암호", "Device web page password", "機器のWebページのパスワード"), Loc.T("KEF Connect에서 기기 암호를 설정한 경우에만 입력합니다. 이 PC의 사용자 계정으로 암호화해 저장합니다.", "Enter it only if you set a device password in KEF Connect. It's stored encrypted with your user account on this PC.", "KEF Connectで機器のパスワードを設定した場合にだけ入力します。このPCのユーザーアカウントで暗号化して保存します。"), passwordBox),
            new Border
            {
                Style = Ui.Style("KefCard"),
                Child = status == null ? connect : Ui.Columns(12, (Ui.Auto, connect), (Ui.Star, status)),
            },
            inputError,
        };
        return (cards, hostBox, passwordBox, connect, inputError);
    }

    /// <summary>One speaker: connection, device, volume limit and correction, and removing it.</summary>
    (UIElement, Action?) SpeakerDetailPage(SpeakerSlot slot)
    {
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var ring = new ProgressRing { Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, IsActive = false, Visibility = Visibility.Collapsed };
        var statusText = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        var (form, hostBox, passwordBox, connect, inputError) = ConnectionCards(Loc.T("연결", "Connect", "接続"),
            slot.Entry.Host, Secrets.Unprotect(slot.Entry.ProtectedPassword), Ui.Row(8, ring, dot, statusText));
        var deviceCards = Ui.Column(4);

        async Task Connect()
        {
            if (!CheckAddress(hostBox.Text, inputError, slot)) return;
            connect.IsEnabled = false;
            try
            {
                await host.UpdateConnectionAsync(slot, hostBox.Text, passwordBox.Password);
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

        var breadcrumb = Breadcrumb(slot.Name);
        var panel = Ui.Column(4, host.HasSeveral ? breadcrumb : Ui.PageTitle(SpeakersTitle));
        if (!host.HasSeveral)
        {
            // One speaker: the page reads as before, with adding another at the top.
            var add = new Button { Content = Ui.Row(8, Ui.Icon(Glyphs.Add, 14), new TextBlock { Text = Loc.T("스피커 추가", "Add a speaker", "スピーカーを追加") }) };
            add.Click += (_, _) => ShowAddSpeaker();
            panel.Children.Add(Ui.Card(Glyphs.Speaker, Loc.T("다른 방의 스피커", "Speakers in other rooms", "ほかの部屋のスピーカー"),
                Loc.T("스피커를 더 추가하면 플라이아웃에서 이름을 눌러 바꿀 수 있습니다", "Add more speakers to switch between them from the name in the flyout", "スピーカーを追加すると、フライアウトで名前を押して切り替えられます"), add));
            panel.Children.Add(Ui.GroupHeader(Loc.T("연결", "Connection", "接続")));
        }
        foreach (var card in form) panel.Children.Add(card);
        panel.Children.Add(Ui.GroupHeader(Loc.T("기기 정보", "Device info", "機器情報")));
        panel.Children.Add(deviceCards);
        var (volumeCards, refreshVolume) = VolumeCards(slot);
        panel.Children.Add(Ui.GroupHeader(Loc.T("음량", "Volume", "音量")));
        panel.Children.Add(volumeCards);
        panel.Children.Add(Ui.GroupHeader(Loc.T("스피커 삭제", "Remove speaker", "スピーカーの削除")));
        panel.Children.Add(RemoveCard(slot));
        panel.Children.Add(AddressHint());

        string deviceSignature = "-";
        var finishSlot = Ui.Column(0);
        string? finishModel = null;
        void Refresh()
        {
            if (!host.Speakers.Contains(slot))
            {
                window.DispatcherQueue.TryEnqueue(ShowSpeakerList);
                return;
            }
            var c = slot.Controller;
            breadcrumb.ItemsSource = new[] { SpeakersTitle, slot.Name };
            ring.IsActive = slot.Connecting;
            ring.Visibility = slot.Connecting ? Visibility.Visible : Visibility.Collapsed;
            dot.Visibility = slot.Connecting ? Visibility.Collapsed : Visibility.Visible;
            (string style, string text) = slot.Connecting ? ("KefDotNeutral", Loc.T("연결하는 중", "Connecting", "接続中"))
                : c.Phase switch
                {
                    SpeakerPhase.On => ("KefDotSuccess", Loc.T("연결됨", "Connected", "接続済み")),
                    SpeakerPhase.Standby => ("KefDotSuccess", Loc.T("연결됨, 스피커는 대기 상태", "Connected, the speaker is in standby", "接続済み、スピーカーはスタンバイ中")),
                    SpeakerPhase.Connecting when !slot.Unreachable && c.Driver != null => ("KefDotNeutral", Loc.T("연결하는 중", "Connecting", "接続中")),
                    _ when c.Driver == null && !slot.Unreachable => ("KefDotNeutral", Loc.T("아직 연결하지 않았습니다", "Not connected yet", "まだ接続していません")),
                    _ => ("KefDotCritical", c.Status),
                };
            dot.Style = Ui.Style(style);
            statusText.Text = Korean.KeepWords(text);

            var d = c.Device;
            string signature = d == null ? "" : $"{d.Name}|{d.Model}|{d.Firmware}|{d.DeviceVolumeLimit}";
            if (signature != deviceSignature)
            {
                deviceSignature = signature;
                deviceCards.Children.Clear();
                finishSlot.Children.Clear();
                finishModel = null;
                if (d == null)
                {
                    deviceCards.Children.Add(Ui.Secondary(Loc.T("연결하면 이름, 모델, 펌웨어가 표시됩니다.", "The name, model and firmware appear once connected.", "接続すると名前、モデル、ファームウェアが表示されます。")));
                }
                else
                {
                    deviceCards.Children.Add(Ui.Card(Glyphs.Speaker, d.Name, d.Model, null));
                    if (!KefModels.Names.Contains(d.Model)) deviceCards.Children.Add(ModelCard(slot));
                    deviceCards.Children.Add(finishSlot);
                    deviceCards.Children.Add(Ui.Card(Glyphs.Info, Loc.T("펌웨어", "Firmware", "ファームウェア"), d.Firmware, null));
                }
            }
            // The finish list follows the picked model without rebuilding the model list above it.
            if (d != null && (finishSlot.Children.Count == 0 || slot.ShownModel != finishModel))
            {
                finishModel = slot.ShownModel;
                finishSlot.Children.Clear();
                finishSlot.Children.Add(FinishCard(slot, finishModel));
            }
            refreshVolume();
        }
        return (panel, Refresh);
    }

    Border RemoveCard(SpeakerSlot slot)
    {
        var remove = new Button { Content = Loc.T("삭제", "Remove", "削除") };
        var confirm = Ui.AccentButton(Loc.T("삭제", "Remove", "削除"));
        var flyout = new Flyout
        {
            Content = Ui.Column(12,
                Ui.Text(Loc.T("이 스피커를 목록에서 지울까요? 주소, 암호, 상한 같은 이 스피커의 설정도 함께 지웁니다. 스피커 자체는 바뀌지 않습니다.", "Remove this speaker from the list? Its address, password, limit and other settings go with it. Nothing changes on the speaker itself.", "このスピーカーを一覧から削除しますか？ アドレス、パスワード、上限などこのスピーカーの設定も削除します。スピーカー自体は変わりません。"), 13),
                confirm),
        };
        ((FrameworkElement)flyout.Content).MaxWidth = 300;
        ((FrameworkElement)flyout.Content).Language = Loc.Tag;
        remove.Flyout = flyout;
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            host.RemoveSpeaker(slot);
            ShowSpeakerList();
        };
        return Ui.Card(Glyphs.Delete, Loc.T("이 스피커 삭제", "Remove this speaker", "このスピーカーを削除"),
            Loc.T("이 PC의 목록에서만 지웁니다", "Removes it from the list on this PC only", "このPCの一覧からだけ削除します"), remove);
    }

    /// <summary>
    /// For a speaker that reports a model the app does not know: the model to draw it as and list finishes for.
    /// The inputs stay those of the reported model, so a wrong pick never hides an input the speaker has.
    /// </summary>
    Border ModelCard(SpeakerSlot slot)
    {
        var models = KefModels.Names;
        var box = new ComboBox { MinWidth = 200 };
        box.Items.Add(GlyphItem(Ui.AppGlyph(20), Loc.T("고르지 않음", "None", "選択しない")));
        foreach (var m in models) box.Items.Add(GlyphItem(Ui.AppGlyph(20, null, m), m));
        box.SelectedIndex = slot.Entry.PickedModel is string picked && models.Contains(picked) ? models.ToList().IndexOf(picked) + 1 : 0;
        box.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            slot.Entry.PickedModel = box.SelectedIndex > 0 ? models[box.SelectedIndex - 1] : null;
            host.SaveSettings();
        };
        return Ui.Card(Glyphs.Speaker, Loc.T("스피커 모델", "Speaker model", "スピーカーのモデル"), Loc.T("앱이 아직 모르는 모델입니다. 플라이아웃 그림과 색상 목록에 쓸 모델을 고르세요. 입력 목록은 바뀌지 않습니다.", "The app doesn't know this model yet. Pick the model to use for the flyout drawing and the finish list. The inputs stay as they are.", "アプリがまだ知らないモデルです。フライアウトの絵とカラーの一覧に使うモデルを選んでください。入力の一覧は変わりません。"), box);
    }

    /// <summary>The finish the flyout draws the speaker in. Speakers do not report it, so the user picks it.</summary>
    Border FinishCard(SpeakerSlot slot, string? model)
    {
        var finishes = KefFinishes.For(model);
        var box = new ComboBox { MinWidth = 200 };
        box.Items.Add(GlyphItem(Ui.AppGlyph(20, null, model), Loc.T("앱 기본 색상", "App default", "アプリの既定")));
        foreach (var f in finishes) box.Items.Add(GlyphItem(Ui.AppGlyph(20, f), f.Name));
        var current = KefFinishes.Find(slot.Entry.Finish, model);
        box.SelectedIndex = current == null ? 0 : finishes.ToList().IndexOf(current) + 1;
        box.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            slot.Entry.Finish = box.SelectedIndex > 0 ? finishes[box.SelectedIndex - 1].Id : null;
            host.SaveSettings();
        };
        return Ui.Card(Glyphs.Color, Loc.T("스피커 색상", "Speaker finish", "スピーカーのカラー"), Loc.T("플라이아웃의 스피커 그림을 이 색으로 그립니다. 스피커가 색상을 알려 주지 않아 직접 고릅니다.", "The flyout draws the speaker in this finish. Speakers don't report their color, so pick it here.", "フライアウトのスピーカーの絵をこの色で描きます。スピーカーは色を知らせないため、ここで選びます。"), box);
    }

    static StackPanel GlyphItem(UIElement glyph, string name) =>
        Ui.Row(10, glyph, new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });

    /// <summary>The volume limit and correction of one speaker, so each room keeps its own.</summary>
    (UIElement, Action) VolumeCards(SpeakerSlot slot)
    {
        var limitBox = new NumberBox
        {
            Minimum = 1,
            Maximum = 100,
            SmallChange = 1,
            LargeChange = 10,
            Value = slot.Entry.AppVolumeLimit,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 130,
        };
        limitBox.ValueChanged += (_, e) =>
        {
            if (updating) return;
            if (double.IsNaN(e.NewValue))
            {
                limitBox.Value = slot.Entry.AppVolumeLimit;
                return;
            }
            slot.Entry.AppVolumeLimit = Math.Clamp((int)Math.Round(e.NewValue), 1, 100);
            host.SaveSettings();
        };
        var deviceLimit = new TextBlock { Style = Ui.Style("KefSecondaryText"), FontSize = 14 };
        var effective = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };

        var approach = new ToggleSwitch { OnContent = Loc.T("켬", "On", "オン"), OffContent = Loc.T("끔", "Off", "オフ") };
        approach.Toggled += (_, _) =>
        {
            if (updating) return;
            slot.Entry.ApproachFromAbove = approach.IsOn;
            host.SaveSettings();
        };
        string approachName = Loc.T("정확한 음량 맞추기", "Accurate volume", "正確な音量合わせ");
        var help = Ui.HelpButton(approachName,
            Loc.T("같은 숫자라도 아래에서 올라오면 더 작게 들리는 KEF 특성을 보정합니다.", "Makes up for KEF speakers sounding quieter at the same number when the volume comes up from below.", "同じ数値でも下から上げると小さく聞こえるKEFの特性を補正します。"),
            out var tip,
            approachName,
            Loc.T("KEF 스피커는 같은 숫자라도 아래에서 올라올 때 더 작게 들리는 구간이 있습니다. 켜면 음량을 올릴 때 목표보다 조금 높은 값을 먼저 보내고 0.1초 뒤 목표 값을 보내, 위에서 내려와 맞춘 것과 같은 크기로 들리게 합니다.", "In some ranges a KEF speaker sounds quieter at the same number when the volume comes up from below. When this is on, raising the volume first sends a slightly higher value and then the target 0.1 seconds later, so it sounds as loud as coming down to the target from above.", "KEFスピーカーには、同じ数値でも下から上げたときに小さく聞こえる範囲があります。オンにすると、音量を上げるときに目標より少し高い値を先に送り、0.1秒後に目標の値を送ることで、上から下げて合わせたときと同じ大きさに聞こえるようにします。"),
            Loc.T("예: 20으로 올리면 22를 거쳐 20, 12로 올리면 17을 거쳐 12가 됩니다. 5 이하와 41 이상은 바로 보냅니다. 잠깐 거치는 값도 상한을 넘지 않고, 내릴 때는 그대로 보냅니다. LSX II(USB 입력)에서 잰 표를 쓰며 LSX II LT에서는 아직 확인하지 않았습니다.", "For example, raising to 20 goes through 22, and raising to 12 goes through 17. Values of 5 or less and 41 or more are sent directly. The value in between never goes past the limit, and lowering is sent as is. The table was measured on an LSX II (USB input) and hasn't been checked on the LSX II LT yet.", "例: 20に上げると22を経て20に、12に上げると17を経て12になります。5以下と41以上はそのまま送ります。途中で経由する値も上限を超えず、下げるときはそのまま送ります。LSX II（USB入力）で測った表を使っており、LSX II LTではまだ確認していません。"));
        var approachTitle = Ui.Row(0, Ui.Text(approachName), help);

        var panel = Ui.Column(4,
            Ui.Card(Glyphs.Limit, Loc.T("앱 음량 상한", "App volume limit", "アプリの音量上限"), Loc.T("이 앱에서 이 스피커를 올릴 수 있는 최대 음량입니다. 내리는 것은 언제나 됩니다.", "The highest volume this app raises this speaker to. Lowering always works.", "このアプリでこのスピーカーを上げられる最大の音量です。下げる操作はいつでもできます。"), limitBox),
            Ui.Card(Glyphs.Speaker, Loc.T("KEF 기기 최대 음량", "KEF device maximum volume", "KEF機器の最大音量"), Loc.T("KEF Connect에서 정한 값입니다. 앱 상한과 둘 중 낮은 값을 씁니다.", "Set in KEF Connect. The lower of this and the app limit applies.", "KEF Connectで設定した値です。アプリの上限と比べて低いほうを使います。"), deviceLimit),
            Ui.Card(Glyphs.Volume, Loc.T("지금 적용되는 상한", "Limit in effect", "現在の上限"), Loc.T("플라이아웃 슬라이더에 눈금으로 표시됩니다.", "Shown as the tick on the flyout slider.", "フライアウトのスライダーに目盛りで表示されます。"), effective),
            Ui.Card(Glyphs.Volume, approachTitle, Loc.T("올릴 때 목표보다 살짝 높게 보낸 뒤 목표로 내립니다", "When raising, sends a slightly higher value first and then settles on the target", "上げるときは目標より少し高く送ってから目標に下げます"), approach, out _),
            tip);

        void Refresh()
        {
            var c = slot.Controller;
            if (!limitBox.IsLoaded || limitBox.FocusState == FocusState.Unfocused) limitBox.Value = slot.Entry.AppVolumeLimit;
            deviceLimit.Text = c.Device == null ? Loc.T("연결 후 표시", "Shown once connected", "接続後に表示") : c.Device.DeviceVolumeLimit is int d ? d.ToString() : Loc.T("제한 없음", "No limit", "制限なし");
            effective.Text = $"{c.EffectiveLimit} ({SpeakerPresentation.LimitSource(c)})";
            approach.IsOn = slot.Entry.ApproachFromAbove;
        }
        return (panel, Refresh);
    }

    // ---------- Shortcuts ----------

    (UIElement, Action?) HotkeysPage()
    {
        var panel = Ui.Column(4, Ui.PageTitle(Loc.T("단축키", "Shortcuts", "ショートカット")));
        panel.Children.Add(Ui.Secondary(Loc.T("칸을 누른 뒤 새 키 조합을 누르세요. Esc는 취소, Backspace는 끄기입니다.", "Select a box, then press the new key combination. Esc cancels and Backspace turns it off.", "ボックスを選んでから新しいキーの組み合わせを押してください。Escでキャンセル、Backspaceでオフになります。"), 13));
        ((FrameworkElement)panel.Children[^1]).Margin = new Thickness(2, 0, 0, 8);

        var osd = new ToggleSwitch { OnContent = Loc.T("켬", "On", "オン"), OffContent = Loc.T("끔", "Off", "オフ") };
        osd.Toggled += (_, _) =>
        {
            if (updating) return;
            host.Settings.ShowVolumeOsd = osd.IsOn;
            host.SaveSettings();
        };
        string perceptualName = Loc.T("체감 음량 기준", "Perceived loudness", "体感音量");
        var step = new ComboBox { Width = 170 };
        foreach (var (label, value) in new[] { ("1", 1), ("2", 2), ("3", 3), ("5", 5), (perceptualName, VolumeSteps.Perceptual) })
            step.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        step.SelectionChanged += (_, _) =>
        {
            if (updating || step.SelectedItem is not ComboBoxItem { Tag: int value }) return;
            host.Settings.HotkeyVolumeStep = value;
            host.SaveSettings();
        };
        var help = Ui.HelpButton(perceptualName,
            Loc.T("숫자 간격 대신 실제 소리 크기가 비슷하게 늘도록 움직입니다.", "Moves so the loudness, not the number, rises in even steps.", "数値の間隔ではなく、実際の音の大きさが均等に増えるように動きます。"),
            out var tip,
            perceptualName,
            Loc.T("숫자를 일정하게 더하는 대신, 소리 크기가 비슷한 간격으로 늘도록 미리 잰 단계(12, 15, 19, 22, 25, 29 …)를 따라 움직입니다. 낮은 음량에서는 숫자가 크게, 높은 음량에서는 작게 움직입니다.", "Instead of adding the same number each time, it follows measured steps (12, 15, 19, 22, 25, 29 …) chosen so the loudness rises evenly. The number moves more at low volume and less at high volume.", "一定の数値を足す代わりに、音の大きさが均等に増えるよう測っておいた段階（12、15、19、22、25、29 …）に沿って動きます。低い音量では数値が大きく、高い音量では小さく動きます。"),
            Loc.T("65보다 위에서는 4씩 움직이고, 12 아래에서 올리면 12로, 내리면 0으로 갑니다. 상한은 그대로 지킵니다.", "Above 65 it moves by 4. Below 12, raising goes to 12 and lowering goes to 0. The limit still applies.", "65より上では4ずつ動き、12未満から上げると12に、下げると0になります。上限はそのまま守ります。"));

        var rows = new List<(HotkeyAction action, HotkeyCaptureBox box, TextBlock status)>();
        foreach (var binding in host.HotkeyBindings)
        {
            var box = new HotkeyCaptureBox(binding, host.Hotkeys);
            string glyph = binding.Action switch
            {
                HotkeyAction.VolumeUp => Glyphs.Up,
                HotkeyAction.VolumeDown => Glyphs.Down,
                HotkeyAction.NextSpeaker => Glyphs.Speaker,
                _ => Glyphs.Mute,
            };
            var card = Ui.Card(glyph, Ui.Text(HotkeyBinding.ActionName(binding.Action)), "", box.View, out var status);
            box.Captured += next => OnCaptured(next, status!);
            rows.Add((binding.Action, box, status!));
            panel.Children.Add(card);
        }

        var reset = new Button { Content = Loc.T("기본값으로 되돌리기", "Restore defaults", "既定値に戻す"), Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(reset);

        panel.Children.Add(Ui.GroupHeader(Loc.T("동작", "Behavior", "動作")));
        panel.Children.Add(Ui.Card(Glyphs.Sort, Ui.Row(0, Ui.Text(Loc.T("한 번 누를 때 음량", "Volume per press", "1回押したときの音量")), help), Loc.T("단축키와 음량 표시창의 마우스 휠에 적용됩니다", "Applies to the shortcuts and to the mouse wheel on the volume indicator", "ショートカットと音量表示のマウスホイールに適用されます"), step, out _));
        panel.Children.Add(tip);
        panel.Children.Add(Ui.Card(Glyphs.Message, Loc.T("음량을 바꿀 때 화면 오른쪽 아래에 표시", "Show in the bottom-right corner when the volume changes", "音量を変えたときに画面の右下に表示"), Loc.T("꺼 두어도 연결 문제나 상한 같은 오류는 표시합니다", "Errors such as connection problems or the limit still show when this is off", "オフでも接続の問題や上限などのエラーは表示します"), osd));

        panel.Children.Add(Ui.GroupHeader(Loc.T("다른 앱과 겹칠 수 있는 조합", "Combinations other apps may use", "ほかのアプリと重なる可能性がある組み合わせ")));
        panel.Children.Add(Ui.Secondary(
            Loc.T(
                "이 앱이 켜져 있는 동안에는 등록한 조합을 이 앱이 먼저 받습니다. 기본값 Ctrl + Alt + ↑ / ↓는 VS Code(커서 추가), JetBrains IDE(이전·다음 검색 결과), Visual Studio(Ctrl + Alt + ↓, 열린 파일 목록)와 같고, Ctrl + Alt + M은 JetBrains(메서드 추출)와 Word(메모 삽입)와 같습니다. Intel 그래픽 드라이버의 화면 회전 단축키(Ctrl + Alt + 화살표)를 켜 둔 PC에서도 겹칩니다. 이런 프로그램을 자주 쓰면 다른 조합으로 바꾸세요. 다른 앱이 먼저 등록한 조합은 위에 '등록하지 못했습니다'로 표시됩니다.",
                "While this app runs, it receives the combinations you set before other apps do. The default Ctrl + Alt + ↑ / ↓ is also used by VS Code (add cursor), JetBrains IDEs (previous and next search result) and Visual Studio (Ctrl + Alt + ↓, open files list), and Ctrl + Alt + M by JetBrains (extract method) and Word (insert comment). It also clashes on PCs with the Intel graphics driver's screen rotation shortcuts (Ctrl + Alt + arrow) turned on. If you use these programs often, choose other combinations. A combination another app registered first shows 'couldn't be registered' above.",
                "このアプリの起動中は、登録した組み合わせをこのアプリが先に受け取ります。既定のCtrl + Alt + ↑ / ↓はVS Code（カーソルの追加）、JetBrains IDE（前・次の検索結果）、Visual Studio（Ctrl + Alt + ↓、開いているファイルの一覧）と同じで、Ctrl + Alt + MはJetBrains（メソッドの抽出）やWord（コメントの挿入）と同じです。Intelグラフィックスドライバーの画面回転のショートカット（Ctrl + Alt + 矢印）を有効にしたPCでも重なります。これらのプログラムをよく使う場合は、別の組み合わせに変更してください。ほかのアプリが先に登録した組み合わせは、上に「登録できませんでした」と表示されます。"),
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
                    HotkeyRegistration.Registered => (Loc.T("사용 중", "Active", "有効"), Ui.Style("KefSecondaryText")),
                    HotkeyRegistration.InUse => (Loc.T("다른 앱이 이미 쓰고 있어 등록하지 못했습니다. 다른 조합으로 바꿔 주세요.", "Another app already uses it, so it couldn't be registered. Choose another combination.", "ほかのアプリがすでに使っているため登録できませんでした。別の組み合わせに変更してください。"), Ui.Style("KefCriticalText")),
                    _ when action == HotkeyAction.NextSpeaker => (Loc.T("꺼짐. 스피커가 2대 이상일 때 선택한 스피커를 차례로 바꿉니다", "Off. With two or more speakers, it moves to the next one in the list", "オフ。スピーカーが2台以上のとき、選択中のスピーカーを順に切り替えます"), Ui.Style("KefSecondaryText")),
                    _ => (Loc.T("꺼짐", "Off", "オフ"), Ui.Style("KefSecondaryText")),
                };
            }
            osd.IsOn = host.Settings.ShowVolumeOsd;
            step.SelectedItem = step.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int v && v == host.Settings.HotkeyVolumeStep)
                ?? step.Items[0];
        }
        return (panel, Refresh);
    }

    // ---------- About ----------

    (UIElement, Action?) AboutPage()
    {
        string version = AppVersion();

        var github = new HyperlinkButton { Content = Loc.T("GitHub에서 열기", "Open on GitHub", "GitHubで開く"), NavigateUri = new Uri(RepositoryUrl) };
        var openFolder = new Button { Content = Loc.T("폴더 열기", "Open folder", "フォルダーを開く") };
        openFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(host.SettingsFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{host.SettingsFolder}\"") { UseShellExecute = true });
        };

        var heading = Ui.Row(16,
            Ui.AppGlyph(56),
            Ui.Column(2,
                new TextBlock { Text = "Remote for KEF", FontSize = 20, FontWeight = FontWeights.SemiBold },
                Ui.Secondary(Loc.T("버전 ", "Version ", "バージョン ") + version, 13),
                Ui.Secondary("A Very Revolutionary Way to Control Your KEF Speakers.", 13)));
        heading.Margin = new Thickness(0, 0, 0, 12);

        var panel = Ui.Column(4,
            Ui.PageTitle(Loc.T("정보", "About", "バージョン情報")),
            heading,
            Ui.Card(Glyphs.Link, Loc.T("소스 코드와 문제 신고", "Source code and issues", "ソースコードと問題の報告"), RepositoryUrl, github),
            Ui.Card(Glyphs.Info, Loc.T("라이선스", "License", "ライセンス"), "MIT License", null),
            Ui.GroupHeader(Loc.T("오픈소스 고지", "Open source notices", "オープンソースライセンス")),
            Ui.Card(null, "kef-lsx-volume", Loc.T("음량 보정 표와 체감 음량 단계의 측정값을 사용합니다. ", "Its measurements are used for the volume correction table and the perceived loudness steps. ", "音量補正の表と体感音量の段階に、その測定値を使っています。") + "MIT License, Copyright (c) 2026 Robert Oberdorfer", null),
            Ui.Card(null, "Velopack", Loc.T("앱 설치와 자동 업데이트에 사용합니다. ", "Installs and updates the app. ", "アプリのインストールと自動アップデートに使っています。") + "MIT License, Copyright (c) 2024 Velopack Ltd.", null),
            Ui.Card(null, "Windows App SDK", "MIT License, Copyright (c) Microsoft Corporation", null),
            Ui.Card(null, ".NET", "MIT License, Copyright (c) .NET Foundation and Contributors", null),
            Ui.GroupHeader(Loc.T("문제 해결", "Troubleshooting", "トラブルシューティング")),
            Ui.Card(Glyphs.Folder, Loc.T("설정과 로그 폴더", "Settings and log folder", "設定とログのフォルダー"), host.SettingsFolder, openFolder));
        return (panel, null);
    }
}
