using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace KefRemote.App.Native;

/// <summary>
/// Desktop acrylic that stays translucent while the window is inactive. The stock DesktopAcrylicBackdrop turns
/// solid without focus, and the volume display never takes focus.
/// </summary>
sealed class AlwaysActiveAcrylic : SystemBackdrop
{
    DesktopAcrylicController? controller;
    SystemBackdropConfiguration? configuration;

    public static bool IsSupported => DesktopAcrylicController.IsSupported();

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!DesktopAcrylicController.IsSupported()) return;
        configuration = new SystemBackdropConfiguration { IsInputActive = true };
        Sync(GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot));
        controller = new DesktopAcrylicController();
        controller.AddSystemBackdropTarget(connectedTarget);
        controller.SetSystemBackdropConfiguration(configuration);
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        Sync(GetDefaultSystemBackdropConfiguration(target, xamlRoot));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (controller == null) return;
        controller.RemoveSystemBackdropTarget(disconnectedTarget);
        controller.Dispose();
        controller = null;
    }

    /// <summary>Follow theme and high contrast from the default configuration, but never go inactive.</summary>
    void Sync(SystemBackdropConfiguration source)
    {
        if (configuration == null) return;
        configuration.Theme = source.Theme;
        configuration.IsHighContrast = source.IsHighContrast;
        configuration.IsInputActive = true;
    }
}
