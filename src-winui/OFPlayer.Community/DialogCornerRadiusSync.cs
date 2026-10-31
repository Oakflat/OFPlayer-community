using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OFPlayer_WinUI;
/// <summary>
/// WinUI's ContentDialog template reads CornerRadius for its outer border and
/// OverlayCornerRadius for its inner DialogSpace. Keep both values aligned for
/// OFPlayer dialogs without changing the radius of menus and other overlays.
/// </summary>
public static class DialogCornerRadiusSync
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached("Radius", typeof(CornerRadius), typeof(DialogCornerRadiusSync), new PropertyMetadata(new CornerRadius(0), OnRadiusChanged));
    public static CornerRadius GetRadius(DependencyObject target) => (CornerRadius)target.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject target, CornerRadius value) => target.SetValue(RadiusProperty, value);
    private static void OnRadiusChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ContentDialog dialog || args.NewValue is not CornerRadius radius)
            return;
        dialog.CornerRadius = radius;
        dialog.Resources["OverlayCornerRadius"] = radius;
    }
}
