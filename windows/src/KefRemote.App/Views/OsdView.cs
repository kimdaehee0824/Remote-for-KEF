using KefRemote.App.Native;
using KefRemote.Core;
using KefRemote.Desktop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;

namespace KefRemote.App.Views;

/// <summary>
/// Volume display at the bottom right of the monitor under the pointer (design W7). Never takes focus, so a
/// hotkey pressed while typing elsewhere does not steal the keyboard. Wheel changes the volume, the speaker
/// icon toggles mute. With more than one speaker the line under the bar names the one the keys work on.
/// Not shown at all while a game or a video is in front in full screen: the keys change the volume and nothing
/// appears over the picture (see <see cref="FullScreenApp"/>).
/// </summary>
sealed class OsdView : IVolumeOsd
{
    const double OsdWidth = 300, OsdHeight = 56, TallHeight = 74;
    const int EdgeMarginDip = 12, ShowMs = 1600, ErrorShowMs = 3200, FadeMs = 220;

    readonly AppHost host;
    readonly Window window;
    readonly AppWindow appWindow;
    readonly IntPtr hwnd;
    readonly Grid root, surface;
    readonly FontIcon icon = Ui.Icon(Glyphs.Volume2, 16);
    readonly Slider bar = new() { Minimum = 0, Maximum = 100, IsHitTestVisible = false, IsTabStop = false, IsThumbToolTipEnabled = false, VerticalAlignment = VerticalAlignment.Center };
    readonly Rectangle limitTick = new() { Width = 2, Height = 16, RadiusX = 1, RadiusY = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly Grid barHost = new();
    readonly TextBlock number = new() { FontWeight = FontWeights.SemiBold, MinWidth = 28, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock message = new() { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly DispatcherQueueTimer hideTimer, fadeTimer;
    int limit = 100;
    bool hovering;
    bool visible;
    ulong? placedOn;
    PixelRect freeArea;
    bool closing;
    bool heldBack;

    /// <summary>Wheel and icon clicks, routed like hotkeys.</summary>
    public event Action<HotkeyAction>? Input;

    /// <summary>The whole window content, background included (for self-test captures).</summary>
    public FrameworkElement Root => surface;

    /// <summary>Window content height against the height the OSD asked for, in DIPs (self-test).</summary>
    public (double Window, double Content) Heights => (surface.ActualHeight, root.Height);

    public OsdView(AppHost host)
    {
        this.host = host;
        var iconButton = new Button
        {
            Content = icon,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsTabStop = false,
        };
        AutomationProperties.SetName(iconButton, Loc.T("음소거 전환", "Toggle mute", "ミュートの切り替え"));
        iconButton.Click += (_, _) => Input?.Invoke(HotkeyAction.ToggleMute);

        limitTick.Style = Ui.Style("KefLimitTick");
        barHost.Children.Add(bar);
        barHost.Children.Add(limitTick);
        barHost.SizeChanged += (_, _) => PlaceLimitTick();

        var row = Ui.Columns(12, (Ui.Auto, iconButton), (Ui.Star, barHost), (Ui.Auto, number));
        row.Height = OsdHeight;
        row.VerticalAlignment = VerticalAlignment.Top;
        message.Margin = new Thickness(0, 0, 0, 8);
        message.VerticalAlignment = VerticalAlignment.Bottom;

        root = new Grid
        {
            Width = OsdWidth,
            Padding = new Thickness(12, 0, 16, 0),
            Children = { row, message },
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(FadeMs) },
        };
        root.PointerWheelChanged += (_, e) =>
        {
            int delta = e.GetCurrentPoint(root).Properties.MouseWheelDelta;
            if (delta != 0) Input?.Invoke(delta > 0 ? HotkeyAction.VolumeUp : HotkeyAction.VolumeDown);
            e.Handled = true;
        };

        surface = new Grid { Children = { root } };
        bool acrylic = !host.SelfTest && AlwaysActiveAcrylic.IsSupported;
        if (!acrylic) surface.Style = Ui.Style("KefSolidSurface");
        surface.Language = Loc.Tag;
        window = new Window { Title = Loc.T("Remote for KEF 음량", "Remote for KEF Volume", "Remote for KEF 音量"), Content = surface };
        if (acrylic) window.SystemBackdrop = new AlwaysActiveAcrylic();
        appWindow = WindowHelpers.AppWindowOf(window);
        hwnd = WindowHelpers.Hwnd(window);
        WindowHelpers.MakePopup(window, activates: false);
        appWindow.Closing += (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            HideNow();
        };

        var queue = DispatcherQueue.GetForCurrentThread();
        hideTimer = queue.CreateTimer();
        hideTimer.IsRepeating = false;
        hideTimer.Tick += (_, _) => FadeOut();
        fadeTimer = queue.CreateTimer();
        fadeTimer.IsRepeating = false;
        fadeTimer.Interval = TimeSpan.FromMilliseconds(FadeMs);
        fadeTimer.Tick += (_, _) => HideNow();

        // Holding the pointer over it keeps it up, e.g. while scrolling the volume.
        root.PointerEntered += (_, _) =>
        {
            hovering = true;
            hideTimer.Stop();
        };
        root.PointerExited += (_, _) =>
        {
            hovering = false;
            if (visible) StartHideTimer(ShowMs);
        };
    }

    public void ShowLevel(OsdContent content)
    {
        // A full-screen game or video keeps the whole screen and its focus; the volume has changed all the same.
        if (!host.SelfTest && WindowHelpers.FullScreenInFront())
        {
            if (!heldBack) Log.Write("volume indicator held back: a full-screen app is in front");
            heldBack = true;
            HideNow();
            return;
        }
        heldBack = false;

        limit = content.Limit;
        int level = content.Volume ?? 0;
        bar.Value = level;
        number.Text = content.Volume?.ToString() ?? "-";
        number.Opacity = content.Pending ? 0.6 : 1;
        icon.Glyph = Glyphs.ForVolume(level, content.Muted);
        PlaceLimitTick();

        // A message takes the line; otherwise, with several speakers, the name of the one being changed.
        string? text = !string.IsNullOrEmpty(content.Message) ? content.Message : host.HasSeveral ? host.Selected?.Name : null;
        bool hasMessage = !string.IsNullOrEmpty(text);
        message.Text = Korean.KeepWords(text ?? "");
        message.Style = Ui.Style(content.IsError && !string.IsNullOrEmpty(content.Message) ? "KefCriticalText" : "KefSecondaryText");
        message.FontSize = 11.5;
        message.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;

        double height = hasMessage ? TallHeight : OsdHeight;
        root.Height = height;
        Place(height);
        fadeTimer.Stop();
        if (!visible)
        {
            visible = true;
            WindowHelpers.ShowInactive(hwnd);
        }
        root.Opacity = 1;
        if (!hovering) StartHideTimer(content.IsError ? ErrorShowMs : ShowMs);
    }

    void Place(double height)
    {
        var cursor = WindowHelpers.CursorPosition();
        var display = WindowHelpers.DisplayAt(cursor);
        // Asking the shell for the taskbar once per appearance (or monitor) is enough while keys repeat.
        if (!visible || placedOn != display.DisplayId.Value)
        {
            freeArea = WindowHelpers.FreeArea(display).Area;
            placedOn = display.DisplayId.Value;
        }
        double scale = WindowHelpers.ScaleAt(cursor);
        WindowHelpers.SetClientSizeInactive(hwnd, appWindow, new SizeInt32((int)Math.Ceiling(OsdWidth * scale), (int)Math.Ceiling(height * scale)));
        var size = appWindow.Size;
        var (x, y) = TaskbarPlacement.BottomRight(freeArea, size.Width, size.Height, (int)Math.Round(EdgeMarginDip * scale));
        WindowHelpers.MoveInactive(hwnd, new PointInt32(x, y));
    }

    void PlaceLimitTick()
    {
        double width = barHost.ActualWidth;
        limitTick.Visibility = limit < 100 && width > 0 ? Visibility.Visible : Visibility.Collapsed;
        limitTick.Margin = new Thickness(10 + (width - 20) * limit / 100.0 - 1, 0, 0, 0);
    }

    void StartHideTimer(int milliseconds)
    {
        hideTimer.Stop();
        hideTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        hideTimer.Start();
    }

    void FadeOut()
    {
        root.Opacity = 0;
        fadeTimer.Start();
    }

    void HideNow()
    {
        hideTimer.Stop();
        fadeTimer.Stop();
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
