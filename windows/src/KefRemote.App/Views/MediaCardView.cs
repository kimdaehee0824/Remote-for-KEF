using KefRemote.App.Native;
using KefRemote.Core;
using KefRemote.Desktop;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;

namespace KefRemote.App.Views;

/// <summary>
/// What the speaker is playing, in its own card above the tray flyout like the media controls above Windows 11
/// Quick Settings: source, title and artist next to the cover, and previous, play/pause and next under them.
/// Shown only with the flyout, only on a streaming input and only while the setting is on. Never takes focus, so
/// clicking it leaves the flyout open.
/// </summary>
sealed class MediaCardView
{
    const double CardWidth = 360, CoverSize = 64;
    const int GapDip = 12;

    readonly AppHost host;
    readonly Window window;
    readonly AppWindow appWindow;
    readonly Grid surface;
    readonly StackPanel root;

    readonly FontIcon sourceIcon = Ui.Icon(Glyphs.Audio, 14);
    readonly TextBlock service = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    readonly TextBlock artist = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    readonly Border cover;
    readonly Image coverImage = new() { Stretch = Stretch.UniformToFill };
    readonly FontIcon coverPlaceholder = Ui.Icon(Glyphs.Audio, 24);
    readonly Button previous, playPause, next;
    readonly FontIcon playPauseIcon = Ui.Icon(Glyphs.Play, 16);

    string? coverUrl;
    bool visible;
    bool closing;
    SizeInt32 lastClientSize;

    /// <summary>The window content with its background, for self-test captures.</summary>
    public FrameworkElement Root => surface;

    public bool IsVisible => visible;

    public MediaCardView(AppHost host)
    {
        this.host = host;
        service.Style = Ui.Style("KefSecondaryText");
        service.TextWrapping = TextWrapping.NoWrap;
        service.FontSize = 12;
        artist.Style = Ui.Style("KefSecondaryText");
        artist.TextWrapping = TextWrapping.NoWrap;
        artist.FontSize = 12;
        sourceIcon.Style = Ui.Style("KefSecondaryIcon");
        sourceIcon.FontSize = 14;

        coverPlaceholder.Style = Ui.Style("KefTertiaryIcon");
        coverPlaceholder.FontSize = 24;
        coverPlaceholder.HorizontalAlignment = HorizontalAlignment.Center;
        coverPlaceholder.VerticalAlignment = VerticalAlignment.Center;
        // A cover that can't be loaded (offline, or a link the speaker no longer serves) leaves the placeholder.
        coverImage.ImageFailed += (_, _) =>
        {
            coverImage.Source = null;
            coverPlaceholder.Visibility = Visibility.Visible;
        };
        cover = new Border
        {
            Style = Ui.Style("KefCoverTile"),
            Width = CoverSize,
            Height = CoverSize,
            Child = new Grid { Children = { coverPlaceholder, coverImage } },
        };

        var header = Ui.Row(8, sourceIcon, service);
        var text = Ui.Column(2, title, artist);
        text.VerticalAlignment = VerticalAlignment.Center;
        var info = Ui.Columns(12, (Ui.Star, text), (Ui.Auto, cover));

        previous = ControlButton(Ui.Icon(Glyphs.Previous, 16), Loc.T("이전 곡", "Previous", "前の曲"), PlaybackCommand.Previous);
        playPause = ControlButton(playPauseIcon, Loc.T("재생", "Play", "再生"), PlaybackCommand.TogglePause);
        next = ControlButton(Ui.Icon(Glyphs.Next, 16), Loc.T("다음 곡", "Next", "次の曲"), PlaybackCommand.Next);
        var controls = Ui.Row(8, previous, playPause, next);
        controls.HorizontalAlignment = HorizontalAlignment.Center;

        root = new StackPanel
        {
            Width = CardWidth,
            Spacing = 10,
            Padding = new Thickness(16, 14, 16, 10),
            Children = { header, info, controls },
        };

        surface = new Grid { Children = { root } };
        bool acrylic = !host.SelfTest && AlwaysActiveAcrylic.IsSupported;
        if (!acrylic) surface.Style = Ui.Style("KefSolidSurface");
        surface.Language = Loc.Tag;
        window = new Window { Title = Loc.T("Remote for KEF 재생 정보", "Remote for KEF Now playing", "Remote for KEF 再生中"), Content = surface };
        if (acrylic) window.SystemBackdrop = new AlwaysActiveAcrylic();
        appWindow = WindowHelpers.AppWindowOf(window);
        WindowHelpers.MakePopup(window, activates: false);
        appWindow.Closing += (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            HideNow();
        };

        host.Changed += Refresh;
        host.Flyout.Placed += Place;
        host.Flyout.VisibilityChanged += Refresh;
    }

    Button ControlButton(FontIcon icon, string name, PlaybackCommand command)
    {
        var button = new Button
        {
            Content = icon,
            Width = 40,
            Height = 36,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsTabStop = false,
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += async (_, _) => await host.RunWhenIdle(() => host.Controller.PlaybackAsync(command));
        return button;
    }

    public void Refresh()
    {
        var c = host.Controller;
        bool show = host.Flyout.IsVisible && host.Settings.ShowNowPlaying && c.ShowsPlayback;
        if (!show)
        {
            HideNow();
            return;
        }
        var p = c.Playback;
        service.Text = p.Service ?? SourceName(c);
        sourceIcon.Glyph = c.State.SourceId is string id ? Glyphs.ForSource(id) : Glyphs.Audio;
        title.Text = p.Title;
        artist.Text = p.Artist;
        artist.Visibility = p.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(title, p.Title);
        ToolTipService.SetToolTip(artist, p.Artist.Length > 0 ? p.Artist : null);
        ShowCover(p.ArtworkUrl);

        bool playing = p.Status == PlaybackStatus.Playing;
        playPauseIcon.Glyph = playing ? Glyphs.Pause : Glyphs.Play;
        string playName = playing ? Loc.T("일시 정지", "Pause", "一時停止") : Loc.T("재생", "Play", "再生");
        AutomationProperties.SetName(playPause, playName);
        ToolTipService.SetToolTip(playPause, playName);
        playPause.IsEnabled = p.CanTogglePause;
        previous.IsEnabled = p.CanPrevious;
        next.IsEnabled = p.CanNext;

        Place();
        if (!visible)
        {
            visible = true;
            appWindow.Show(false);
        }
    }

    static string SourceName(SpeakerController c) =>
        c.Driver?.Sources.FirstOrDefault(s => s.Id == c.State.SourceId)?.Label ?? "";

    void ShowCover(string? url)
    {
        if (url == coverUrl) return;
        coverUrl = url;
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            coverImage.Source = null;
            coverPlaceholder.Visibility = Visibility.Visible;
            return;
        }
        double scale = host.Flyout.Placement?.Scale ?? 1;
        coverImage.Source = new BitmapImage(uri) { DecodePixelWidth = (int)Math.Ceiling(CoverSize * scale), DecodePixelType = DecodePixelType.Physical };
        coverPlaceholder.Visibility = Visibility.Collapsed;
    }

    /// <summary>Above the flyout (below it with the taskbar on top), the same width, 12 DIPs apart.</summary>
    void Place()
    {
        if (!host.Flyout.IsVisible || host.Flyout.Placement is not { } placed) return;
        var (flyout, area, edge, scale) = placed;
        root.Measure(new Size(CardWidth, double.PositiveInfinity));
        var client = new SizeInt32((int)Math.Ceiling(CardWidth * scale), (int)Math.Ceiling(root.DesiredSize.Height * scale));
        if (!client.Equals(lastClientSize))
        {
            lastClientSize = client;
            WindowHelpers.SetClientSize(appWindow, client);
        }
        var size = appWindow.Size;
        var (x, y) = TaskbarPlacement.Companion(area, edge, flyout, size.Width, size.Height, (int)Math.Round(GapDip * scale));
        appWindow.Move(new PointInt32(x, y));
    }

    void HideNow()
    {
        if (!visible) return;
        visible = false;
        appWindow.Hide();
    }

    public void Close()
    {
        closing = true;
        HideNow();
        window.Close();
    }
}
