using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace OFPlayer_WinUI;
/// <summary>
/// The theme chosen in Settings is set on the window's root element, and two things do not
/// look there. A theme brush read in code (<c>Application.Current.Resources[key]</c>, or any
/// of its theme dictionaries) is the Windows theme's brush, and a dialog is not inside the
/// root. With Windows light and OFPlayer dark, both came out light. Reading the built-in theme
/// dictionaries for a particular theme does not help: some brushes (secondary text, dividers)
/// come back in the Windows theme anyway, and afterwards every <c>{ThemeResource}</c> use of them,
/// XAML included, stays that way. So code never reads a theme brush.
/// <para>Code-built content takes theme brushes from <see cref = "Brush{T}"/>, and every dialog
/// follows the window through <c>OFPlayerDialogStyle</c>.</para>
/// </summary>
public static class WindowTheme
{
    private static readonly Dictionary<string, Style> styles = new();
    /// <summary>Gives <paramref name = "element"/> the theme brush <paramref name = "key"/> for
    /// <paramref name = "property"/>, in the element's own theme and following it.</summary>
    public static T Brush<T>(T element, string property, string key)
        where T : FrameworkElement => Brushes(element, (property, key));
    /// <summary>Several theme brushes at once; see <see cref = "Brush{T}"/>.</summary>
    /// <remarks>Only a <c>{ThemeResource}</c> reference resolves for the element's theme, and code
    /// can attach one only through a style, so this replaces the element's style. It is for plain
    /// elements; one with a style of its own gets its brushes in XAML. A local value of the same
    /// property, including one set in XAML, wins over the style and has to go.</remarks>
    public static T Brushes<T>(T element, params (string Property, string Key)[] brushes)
        where T : FrameworkElement
    {
        if (element.Style is { } own && !styles.ContainsValue(own))
            throw new InvalidOperationException($"{element.GetType().Name} has its own style; give it its theme brushes in XAML.");
        // The nearest WinUI type, which the XAML namespace below can name; a style also fits subclasses.
        var target = element.GetType();
        while (target.Namespace?.StartsWith("Microsoft.UI.Xaml", StringComparison.Ordinal) != true)
            target = target.BaseType!;
        var signature = target.Name + string.Concat(brushes.Select(brush => $"|{brush.Property}={brush.Key}"));
        if (!styles.TryGetValue(signature, out var style))
        {
            var setters = string.Concat(brushes.Select(brush => $"<Setter Property=\"{brush.Property}\" Value=\"{{ThemeResource {brush.Key}}}\"/>"));
            style = (Style)XamlReader.Load($"<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"{target.Name}\">{setters}</Style>");
            styles.Add(signature, style);
        }

        element.Style = style;
        return element;
    }

    public static readonly DependencyProperty FollowsWindowProperty = DependencyProperty.RegisterAttached("FollowsWindow", typeof(bool), typeof(WindowTheme), new PropertyMetadata(false, OnFollowsWindowChanged));
    public static bool GetFollowsWindow(DependencyObject target) => (bool)target.GetValue(FollowsWindowProperty);
    public static void SetFollowsWindow(DependencyObject target, bool value) => target.SetValue(FollowsWindowProperty, value);
    // The dialog takes the root's theme when it is shown and keeps it while it is open.
    private static void OnFollowsWindowChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ContentDialog dialog || args.NewValue is not true)
            return;
        FrameworkElement? root = null;
        void Match(FrameworkElement sender, object _) => dialog.RequestedTheme = sender.ActualTheme;
        dialog.Loaded += (_, _) =>
        {
            if (root is not null || dialog.XamlRoot?.Content is not FrameworkElement content)
                return;
            root = content;
            dialog.RequestedTheme = root.ActualTheme;
            root.ActualThemeChanged += Match;
        };
        dialog.Closed += (_, _) =>
        {
            if (root is not null)
                root.ActualThemeChanged -= Match;
            root = null;
        };
    }
}
