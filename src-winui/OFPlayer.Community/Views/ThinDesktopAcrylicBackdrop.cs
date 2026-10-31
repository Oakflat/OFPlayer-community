using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OFPlayer_WinUI.Views;
/// <summary>
/// Desktop glass that reveals windows behind the player. WinUI 1.8 exposes
/// the Thin variant on the controller, but not on DesktopAcrylicBackdrop.
/// </summary>
public sealed class ThinDesktopAcrylicBackdrop : SystemBackdrop
{
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, DesktopAcrylicController> controllers = new();
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        var controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin
        };
        try
        {
            // Keep native theme, activation, transparency and high-contrast policy.
            controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot));
            controller.AddSystemBackdropTarget(connectedTarget);
            controllers.Add(connectedTarget, controller);
        }
        catch
        {
            controller.Dispose();
            throw;
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        if (controllers.Remove(disconnectedTarget, out var controller))
        {
            controller.RemoveSystemBackdropTarget(disconnectedTarget);
            controller.Dispose();
        }

        base.OnTargetDisconnected(disconnectedTarget);
    }
}
