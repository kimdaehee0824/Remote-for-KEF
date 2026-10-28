using KefRemote.App.Native;
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
/// icon toggles mute.
/// </summary>
sealed class OsdView : IVolumeOsd
{
    const double OsdWidth = 300, OsdHeight = 56, TallHeight = 74;
    const int EdgeMarginDip = 12, ShowMs = 1600, ErrorShowMs = 3200, FadeMs = 220;

    readonly Window window;
    readonly AppWindow appWindow;
    readonly Grid root;
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
    bool closing;

    /// <summary>Wheel and icon clicks, routed like hotkeys.</summary>
    public event Action<HotkeyAction>? Input;

    public FrameworkElement Root => root;

    public OsdView(AppHost host)
    {
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
        AutomationProperties.SetName(iconButton, "음소거 전환");
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

        var surface = new Grid { Children = { root } };
        bool acrylic = !host.SelfTest && AlwaysActiveAcrylic.IsSupported;
        if (!acrylic) surface.Style = Ui.Style("KefSolidSurface");
        window = new Window { Title = "Remote for KEF 음량", Content = surface };
        if (acrylic) window.SystemBackdrop = new AlwaysActiveAcrylic();
        appWindow = WindowHelpers.AppWindowOf(window);
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
        limit = content.Limit;
        int level = content.Volume ?? 0;
        bar.Value = level;
        number.Text = content.Volume?.ToString() ?? "-";
        number.Opacity = content.Pending ? 0.6 : 1;
        icon.Glyph = Glyphs.ForVolume(level, content.Muted);
        PlaceLimitTick();

        bool hasMessage = !string.IsNullOrEmpty(content.Message);
        message.Text = content.Message ?? "";
        message.Style = Ui.Style(content.IsError ? "KefCriticalText" : "KefSecondaryText");
        message.FontSize = 11.5;
        message.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;

        double height = hasMessage ? TallHeight : OsdHeight;
        root.Height = height;
        Place(height);
        fadeTimer.Stop();
        if (!visible)
        {
            visible = true;
            appWindow.Show(false);
        }
        root.Opacity = 1;
        if (!hovering) StartHideTimer(content.IsError ? ErrorShowMs : ShowMs);
    }

    void Place(double height)
    {
        var cursor = WindowHelpers.CursorPosition();
        var display = WindowHelpers.DisplayAt(cursor);
        double scale = WindowHelpers.ScaleAt(cursor);
        appWindow.ResizeClient(new SizeInt32((int)Math.Ceiling(OsdWidth * scale), (int)Math.Ceiling(height * scale)));
        var size = appWindow.Size;
        var work = display.WorkArea;
        int margin = (int)Math.Round(EdgeMarginDip * scale);
        appWindow.Move(new PointInt32(work.X + work.Width - size.Width - margin, work.Y + work.Height - size.Height - margin));
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
