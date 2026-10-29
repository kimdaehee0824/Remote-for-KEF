using KefRemote.App.Views;
using KefRemote.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace KefRemote.App;

/// <summary>
/// WinUI application without XAML pages. Implementing IXamlMetadataProvider with the controls provider is what a
/// XAML page project would generate; it lets the framework resolve the built-in control types and templates.
/// </summary>
sealed class App : Application, IXamlMetadataProvider
{
    public static int ExitCode { get; set; }

    readonly XamlControlsXamlMetaDataProvider metadata = new();
    readonly LaunchOptions options;
    AppHost? host;

    public App(LaunchOptions options)
    {
        this.options = options;
        // The tray icon keeps the app alive with every window hidden or closed. Exit is explicit.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write("unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources ??= new ResourceDictionary();
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        Ui.Styles = LoadStyles();
        Resources.MergedDictionaries.Add(Ui.Styles);
        if (Loc.Language == UiLanguage.Japanese) UseFont(new FontFamily("Segoe UI Variable Text, Yu Gothic UI"));

        if (options.SelfTestFolder is string folder)
        {
            _ = SelfTest.RunAsync(folder);
            return;
        }
        if (options.Demo)
        {
            host = new AppHost(options with { Background = true }, Demo.CreateEnvironment());
            _ = Demo.ShowAsync(host);
            return;
        }
        host = new AppHost(options);
    }

    /// <summary>
    /// XAML picks the fallback font for kana by the Windows display language, not by the Language property, so on a
    /// Korean PC Japanese came out in Malgun Gothic with full-width kana. Naming the fallback for every text keeps
    /// Yu Gothic UI. Hangul falls back to Malgun Gothic under any language, so Korean needs nothing.
    /// </summary>
    void UseFont(FontFamily font)
    {
        Resources["ContentControlThemeFontFamily"] = font;
        foreach (var style in Ui.Styles.Values.OfType<Style>().Where(s => s.TargetType == typeof(TextBlock)))
            style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, font));
        var text = new Style(typeof(TextBlock));
        text.Setters.Add(new Setter(TextBlock.FontFamilyProperty, font));
        Resources[typeof(TextBlock)] = text;
    }

    static ResourceDictionary LoadStyles()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("KefRemote.App.Styles.xaml")
            ?? throw new InvalidOperationException("Styles.xaml is not embedded");
        using var reader = new StreamReader(stream);
        return (ResourceDictionary)XamlReader.Load(reader.ReadToEnd());
    }

    void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Write("unhandled exception", e.Exception);
        if (options.SelfTestFolder != null)
        {
            ExitCode = 1;
            return;
        }
        // A tray app that dies silently also loses its shortcuts. Keep running; the log has the details.
        e.Handled = true;
    }

    public IXamlType GetXamlType(Type type) => metadata.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => metadata.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => metadata.GetXmlnsDefinitions();
}
