using KefRemote.Desktop;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.Text;

namespace KefRemote.App.Views;

/// <summary>Segoe Fluent Icons code points (Segoe MDL2 Assets on Windows 10 has the same ones).</summary>
static class Glyphs
{
    public const string Settings = "\uE713", Info = "\uE946", Speaker = "\uE7F5", Keyboard = "\uE765", Power = "\uE7E8",
        Volume0 = "\uE992", Volume1 = "\uE993", Volume2 = "\uE994", Volume3 = "\uE995", Mute = "\uE74F",
        Usb = "\uE88E", Wifi = "\uE701", Bluetooth = "\uE702", Tv = "\uE7F4", Optical = "\uE706", Audio = "\uE8D6",
        Network = "\uE968", Limit = "\uE72E", Folder = "\uE838", Link = "\uE71B", Pin = "\uE840", Play = "\uE768",
        Refresh = "\uE72C", Message = "\uE8BD", Sort = "\uE8CB", Volume = "\uE767", Home = "\uE80F",
        Up = "\uE70E", Down = "\uE70D";

    public static string ForSource(string id) => id switch
    {
        "usb" => Usb,
        "wifi" => Wifi,
        "bluetooth" => Bluetooth,
        "tv" => Tv,
        "optical" => Optical,
        _ => Audio,
    };

    public static string ForVolume(int volume, bool muted) => muted ? Mute : volume switch
    {
        0 => Volume0,
        < 34 => Volume1,
        < 67 => Volume2,
        _ => Volume3,
    };
}

/// <summary>Small builders so the views read top to bottom like the layout they produce.</summary>
static class Ui
{
    public static ResourceDictionary Styles { get; set; } = new();

    public static Style Style(string key) => (Style)Styles[key];

    /// <summary>A keyed resource from the WinUI control library, or null if this version does not have it.</summary>
    public static T? Library<T>(string key) where T : class =>
        Application.Current.Resources.TryGetValue(key, out object value) ? value as T : null;

    public static TextBlock Text(string text, double size = 14, FontWeight? weight = null, string? style = null)
    {
        var block = new TextBlock { Text = Korean.KeepWords(text), TextWrapping = TextWrapping.Wrap };
        if (style != null) block.Style = Style(style);
        block.FontSize = size;
        if (weight is FontWeight w) block.FontWeight = w;
        return block;
    }

    public static TextBlock Secondary(string text, double size = 12) => Text(text, size, style: "KefSecondaryText");

    public static FontIcon Icon(string glyph, double size = 16) => new() { Glyph = glyph, FontSize = size };

    public static StackPanel Row(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static StackPanel Column(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static Grid Columns(double spacing, params (GridLength width, UIElement element)[] cells)
    {
        var grid = new Grid { ColumnSpacing = spacing };
        for (int i = 0; i < cells.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = cells[i].width });
            Grid.SetColumn((FrameworkElement)cells[i].element, i);
            grid.Children.Add(cells[i].element);
        }
        return grid;
    }

    public static GridLength Auto => GridLength.Auto;
    public static GridLength Star => new(1, GridUnitType.Star);

    public static Button AccentButton(string text)
    {
        var button = new Button { Content = text };
        if (Library<Style>("AccentButtonStyle") is Style accent) button.Style = accent;
        return button;
    }

    /// <summary>The round (?) button that opens a teaching tip; hovering shows the first sentence.</summary>
    public static Button HelpButton(string name, string hoverText, out TeachingTip tip, string title, string body, string? example = null)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = "?", FontSize = 11, FontWeight = FontWeights.SemiBold },
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(11),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name + " 설명");
        ToolTipService.SetToolTip(button, hoverText);
        var content = Column(8, Text(body, 13));
        if (example != null) content.Children.Add(Secondary(example, 12.5));
        var teachingTip = new TeachingTip
        {
            Title = title,
            Content = content,
            Target = button,
            IsLightDismissEnabled = true,
            PreferredPlacement = TeachingTipPlacementMode.Bottom,
        };
        tip = teachingTip;
        button.Click += (_, _) => teachingTip.IsOpen = !teachingTip.IsOpen;
        return button;
    }

    /// <summary>
    /// A Windows 11 Settings row: icon, title with an optional one-line description, and a control on the right.
    /// </summary>
    public static Border Card(string? glyph, UIElement title, string? description, UIElement? control, out TextBlock? descriptionBlock)
    {
        var text = Column(2, title);
        descriptionBlock = null;
        if (description != null)
        {
            descriptionBlock = Secondary(description);
            text.Children.Add(descriptionBlock);
        }
        text.VerticalAlignment = VerticalAlignment.Center;
        var cells = new List<(GridLength, UIElement)>();
        if (glyph != null)
        {
            var icon = Icon(glyph, 18);
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Width = 20;
            cells.Add((Auto, icon));
        }
        cells.Add((Star, text));
        if (control != null)
        {
            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            cells.Add((Auto, control));
        }
        var grid = Columns(16, cells.ToArray());
        return new Border { Style = Style("KefCard"), Child = grid };
    }

    public static Border Card(string? glyph, string title, string? description, UIElement? control) =>
        Card(glyph, Text(title), description, control, out _);

    public static TextBlock GroupHeader(string text) =>
        new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(1, 18, 0, 6) };

    public static TextBlock PageTitle(string text) => new()
    {
        Text = text,
        FontSize = 28,
        FontWeight = FontWeights.SemiBold,
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        Margin = new Thickness(0, 4, 0, 16),
    };

    public static Ellipse Dot(string style) => new() { Width = 8, Height = 8, Style = Style(style), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>
    /// The app icon drawn as vectors (Uni-Q driver centered on the cabinet) so it stays sharp at any scale.
    /// Brand colors, the same in light and dark mode.
    /// </summary>
    public static Viewbox AppGlyph(double size)
    {
        static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
        var canvas = new Canvas { Width = 48, Height = 48 };
        void Add(Shape shape, double left, double top)
        {
            Canvas.SetLeft(shape, left);
            Canvas.SetTop(shape, top);
            canvas.Children.Add(shape);
        }
        Add(new Rectangle { Width = 28, Height = 42, RadiusX = 5, RadiusY = 5, Fill = Brush(0xD4, 0x5F, 0x2A) }, 10, 3);
        Add(new Ellipse { Width = 22, Height = 22, Fill = Brush(0x2A, 0x2E, 0x35) }, 13, 13);
        Add(new Ellipse { Width = 16.4, Height = 16.4, Fill = Brush(0xEC, 0xE7, 0xDF) }, 15.8, 15.8);
        Add(new Ellipse { Width = 6.4, Height = 6.4, Fill = Brush(0xD4, 0x5F, 0x2A) }, 20.8, 20.8);
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }
}
