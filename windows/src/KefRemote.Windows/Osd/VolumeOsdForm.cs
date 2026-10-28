using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KefRemote.Windows;

/// <param name="Volume">Level to draw, or null when the speaker state is unknown.</param>
/// <param name="Pending">True while the level is a preview that the speaker has not confirmed yet.</param>
public sealed record OsdContent(int? Volume, bool Muted, int Limit, bool Pending, string? Message, bool IsError);

/// <summary>
/// Volume flyout in the bottom-right corner of the primary screen's work area. It slides up and fades in,
/// animates the bar to the new level, then fades out after a short idle time. It never takes focus, stays out of
/// Alt+Tab, and accepts the mouse wheel (step volume) and a click on the speaker icon (mute).
/// </summary>
public sealed class VolumeOsdForm : Form
{
    const int BaseWidth = 280, BaseHeight = 52, MessageHeight = 20, Margin_ = 12, SlideDistance = 12;
    const int FrameMs = 15, FadeInMs = 140, FadeOutMs = 220, BarAnimMs = 140;
    const int VisibleMs = 1600, ErrorVisibleMs = 3200;

    enum Phase { Hidden, Entering, Shown, Leaving }

    readonly System.Windows.Forms.Timer frame = new() { Interval = FrameMs };
    readonly Stopwatch clock = Stopwatch.StartNew();
    OsdContent content = new(null, false, 100, false, null, false);
    Phase phase = Phase.Hidden;
    long phaseStartedAt, hideAt;
    float barFrom, barTo, barShown;
    long barStartedAt;
    float scale = 1f;
    bool hovering;
    int wheelRemainder;
    Palette palette = Palette.Read();
    Font? numberFont, messageFont, iconFont;

    /// <summary>+1 or -1 per wheel notch over the flyout.</summary>
    public event Action<int>? WheelStep;
    public event Action? MuteClicked;

    public VolumeOsdForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Text = "Remote for KEF 음량";
        frame.Tick += (_, _) => Animate();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, CS_DROPSHADOW = 0x20000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 draws native rounded corners; Windows 10 ignores this and keeps the square shape.
        int round = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        UpdateScale(DeviceDpi);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateScale(e.DeviceDpiNew);
        Relayout();
    }

    /// <summary>Shows or refreshes the flyout. Safe to call on every key repeat.</summary>
    public void ShowLevel(OsdContent next)
    {
        if (IsDisposed) return;
        bool heightChanges = (next.Message != null) != (content.Message != null);
        content = next;

        float level = next.Volume is int v ? v / 100f : 0f;
        if (Math.Abs(level - barTo) > 0.0001f || phase == Phase.Hidden)
        {
            barFrom = phase == Phase.Hidden ? level : barShown;
            barTo = level;
            barStartedAt = clock.ElapsedMilliseconds;
        }

        long now = clock.ElapsedMilliseconds;
        hideAt = now + (next.IsError ? ErrorVisibleMs : VisibleMs);

        if (phase == Phase.Hidden)
        {
            palette = Palette.Read();
            BackColor = palette.Background;
            if (!IsHandleCreated) CreateHandle();
            UpdateScale(DeviceDpi);
            Relayout();
            barShown = barTo;
            Begin(Phase.Entering, now);
            Opacity = Animations ? 0 : 1;
            Show();
        }
        else
        {
            if (phase == Phase.Leaving) Begin(Phase.Entering, now - (long)(FadeInMs * Opacity));
            if (heightChanges) Relayout();
        }
        frame.Start();
        Invalidate();
    }

    static bool Animations => SystemInformation.UIEffectsEnabled;

    void Begin(Phase next, long at)
    {
        phase = next;
        phaseStartedAt = at;
    }

    void Animate()
    {
        long now = clock.ElapsedMilliseconds;

        float barT = Animations ? Math.Clamp((now - barStartedAt) / (float)BarAnimMs, 0f, 1f) : 1f;
        float bar = barFrom + (barTo - barFrom) * EaseOut(barT);
        bool barMoving = Math.Abs(bar - barShown) > 0.0001f;
        barShown = bar;

        switch (phase)
        {
            case Phase.Entering:
            {
                float t = Animations ? Math.Clamp((now - phaseStartedAt) / (float)FadeInMs, 0f, 1f) : 1f;
                Opacity = EaseOut(t);
                Place(SlideDistance * (1 - EaseOut(t)));
                if (t >= 1f) Begin(Phase.Shown, now);
                break;
            }
            case Phase.Shown:
                if (hovering) hideAt = Math.Max(hideAt, now + 400);
                if (now >= hideAt) Begin(Phase.Leaving, now);
                break;
            case Phase.Leaving:
            {
                float t = Animations ? Math.Clamp((now - phaseStartedAt) / (float)FadeOutMs, 0f, 1f) : 1f;
                Opacity = 1 - t;
                Place(SlideDistance * EaseIn(t));
                if (t >= 1f)
                {
                    Hide();
                    phase = Phase.Hidden;
                    frame.Stop();
                    return;
                }
                break;
            }
        }
        if (barMoving) Invalidate();
    }

    static float EaseOut(float t) => 1 - (1 - t) * (1 - t) * (1 - t);
    static float EaseIn(float t) => t * t * t;

    void UpdateScale(int dpi)
    {
        float next = dpi / 96f;
        if (Math.Abs(next - scale) < 0.001f && numberFont != null) return;
        scale = next;
        numberFont?.Dispose();
        messageFont?.Dispose();
        iconFont?.Dispose();
        numberFont = new Font(Fonts.Text, 13f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        messageFont = new Font(Fonts.Text, 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        iconFont = new Font(Fonts.Icon, 18f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    int S(float logical) => (int)Math.Round(logical * scale);

    void Relayout()
    {
        Size = new Size(S(BaseWidth), S(BaseHeight + (content.Message != null ? MessageHeight : 0)));
        if (Environment.OSVersion.Version.Build < 22000)
        {
            using var path = RoundedRect(new RectangleF(0, 0, Width, Height), S(8));
            var old = Region;
            Region = new Region(path);
            old?.Dispose();
        }
        Place(phase == Phase.Entering ? SlideDistance * (1 - (float)Opacity) : 0);
    }

    void Place(float offsetLogical)
    {
        var area = (Screen.PrimaryScreen ?? Screen.FromControl(this)).WorkingArea;
        Location = new Point(area.Right - Width - S(Margin_), area.Bottom - Height - S(Margin_) + S(offsetLogical));
    }

    Rectangle IconBounds => new(S(12), S(BaseHeight / 2f - 16), S(32), S(32));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(palette.Background);
        if (Environment.OSVersion.Version.Build < 22000)
        {
            using var border = new Pen(palette.Border, 1);
            using var outline = RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(8));
            g.DrawPath(border, outline);
        }

        // Speaker glyph: Segoe Fluent Icons / MDL2 Volume0-3 and Mute.
        string glyph = content.Muted ? ""
            : content.Volume is not int v || v == 0 ? ""
            : v < 34 ? ""
            : v < 67 ? ""
            : "";
        TextRenderer.DrawText(g, glyph, iconFont, IconBounds, palette.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // Number.
        var numberBox = new Rectangle(Width - S(52), S(BaseHeight / 2f - 12), S(40), S(24));
        string number = content.Volume?.ToString() ?? "—";
        TextRenderer.DrawText(g, number, numberFont, numberBox, content.Pending ? palette.SecondaryText : palette.Text,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // Track, fill, cap marker.
        float left = S(56), right = Width - S(64), mid = S(BaseHeight / 2f), thickness = S(4);
        var track = new RectangleF(left, mid - thickness / 2, right - left, thickness);
        using (var trackBrush = new SolidBrush(palette.Track))
        using (var trackPath = RoundedRect(track, thickness / 2))
            g.FillPath(trackBrush, trackPath);

        if (content.Volume != null && barShown > 0.001f)
        {
            var fill = track with { Width = Math.Max(thickness, track.Width * barShown) };
            var color = content.Muted ? palette.SecondaryText : palette.Accent;
            if (content.Pending) color = Color.FromArgb(150, color);
            using var fillBrush = new SolidBrush(color);
            using var fillPath = RoundedRect(fill, thickness / 2);
            g.FillPath(fillBrush, fillPath);

            // Thumb, like the Windows 11 flyout.
            float cx = left + track.Width * barShown, r = S(7);
            using var thumbOuter = new SolidBrush(palette.Thumb);
            using var thumbInner = new SolidBrush(color);
            g.FillEllipse(thumbOuter, cx - r, mid - r, r * 2, r * 2);
            g.FillEllipse(thumbInner, cx - r * 0.55f, mid - r * 0.55f, r * 1.1f, r * 1.1f);
        }

        if (content.Limit is > 0 and < 100)
        {
            float x = left + track.Width * content.Limit / 100f;
            using var capPen = new Pen(palette.SecondaryText, Math.Max(1, S(1.5f)));
            g.DrawLine(capPen, x, mid - S(7), x, mid - S(4));
        }

        if (content.Message != null)
        {
            var messageBox = new Rectangle(S(16), S(BaseHeight - 8), Width - S(32), S(MessageHeight));
            TextRenderer.DrawText(g, content.Message, messageFont, messageBox, content.IsError ? palette.Error : palette.SecondaryText,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        hovering = true;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hovering = false;
        hideAt = Math.Max(hideAt, clock.ElapsedMilliseconds + 800);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        wheelRemainder += e.Delta;
        while (Math.Abs(wheelRemainder) >= 120)
        {
            int direction = Math.Sign(wheelRemainder);
            wheelRemainder -= direction * 120;
            WheelStep?.Invoke(direction);
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && IconBounds.Contains(e.Location)) MuteClicked?.Invoke();
    }

    void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            palette = Palette.Read();
            BackColor = palette.Background;
            if (Visible) Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            frame.Dispose();
            numberFont?.Dispose();
            messageFont?.Dispose();
            iconFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    static class Fonts
    {
        static readonly HashSet<string> Installed = new(
            FontFamily.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        public static readonly string Text = Pick("Segoe UI Variable Text", "Segoe UI", "Malgun Gothic");
        public static readonly string Icon = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets", "Segoe UI Symbol");

        static string Pick(params string[] names) =>
            names.FirstOrDefault(Installed.Contains) ?? SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Segoe UI";
    }

    readonly record struct Palette(Color Background, Color Border, Color Text, Color SecondaryText, Color Track, Color Thumb, Color Accent, Color Error)
    {
        /// <summary>Follows the Windows app theme and accent color, falling back to the default Windows blue.</summary>
        public static Palette Read()
        {
            bool light = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme") != 0;
            if (SystemInformation.HighContrast)
            {
                return new Palette(SystemColors.Window, SystemColors.WindowText, SystemColors.WindowText, SystemColors.GrayText,
                    SystemColors.GrayText, SystemColors.Window, SystemColors.Highlight, SystemColors.WindowText);
            }

            // AccentColor is stored as 0xAABBGGRR.
            uint abgr = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", 0xFFC06700);
            var accent = Color.FromArgb(255, (int)(abgr & 0xFF), (int)((abgr >> 8) & 0xFF), (int)((abgr >> 16) & 0xFF));

            return light
                ? new Palette(Color.FromArgb(249, 249, 249), Color.FromArgb(229, 229, 229), Color.FromArgb(26, 26, 26),
                    Color.FromArgb(96, 96, 96), Color.FromArgb(196, 196, 196), Color.White, Darken(accent, 0.1f), Color.FromArgb(196, 43, 28))
                : new Palette(Color.FromArgb(44, 44, 44), Color.FromArgb(64, 64, 64), Color.White,
                    Color.FromArgb(200, 200, 200), Color.FromArgb(110, 110, 110), Color.FromArgb(69, 69, 69), Lighten(accent, 0.4f), Color.FromArgb(255, 153, 164));
        }

        static uint ReadDword(string key, string name, uint fallback = 1)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(key);
                return k?.GetValue(name) is int value ? unchecked((uint)value) : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        static Color Lighten(Color c, float amount) => Color.FromArgb(
            c.R + (int)((255 - c.R) * amount), c.G + (int)((255 - c.G) * amount), c.B + (int)((255 - c.B) * amount));

        static Color Darken(Color c, float amount) => Color.FromArgb(
            (int)(c.R * (1 - amount)), (int)(c.G * (1 - amount)), (int)(c.B * (1 - amount)));
    }
}
