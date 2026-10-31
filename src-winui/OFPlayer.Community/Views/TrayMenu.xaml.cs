using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OFPlayer_WinUI.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace OFPlayer_WinUI.Views;
/// <summary>What the tray menu shows; read again while it is open, as playback changes.</summary>
internal sealed record TrayMenuState(ElementTheme Theme, string Title, string Artist, bool Playing, bool CanPlay, bool CanSkip, bool CapsuleShown, bool CapsuleOpening);
/// <summary>
/// The tray icon's menu, in a small borderless window of its own: a system menu takes neither the
/// app's theme nor this layout. It opens at the icon, inside the work area of the icon's display,
/// and closes like a menu: when it loses activation, on Escape, or once an item is chosen. The
/// playback controls leave it open, so a few songs can be skipped in a row.
/// </summary>
public sealed partial class TrayMenu : UserControl
{
    private Window? window;
    private PointInt32 anchor;
    private Size placed;
    private bool closing;
    public TrayMenu()
    {
        InitializeComponent();
        // Measured before the first showing; placed again once the content is live.
        Loaded += (_, _) => Place();
    }

    internal event Action<TrayCommand>? Invoked;
    internal bool IsOpen => window?.AppWindow.IsVisible == true;

    internal void Open(PointInt32 at, TrayMenuState state)
    {
        anchor = at;
        var host = window ??= CreateWindow();
        Render(state);
        Place();
        host.Activate();
        SetForegroundWindow(Win32Interop.GetWindowFromWindowId(host.AppWindow.Id));
        OpenItem.Focus(FocusState.Programmatic);
    }

    /// <summary>New state for the open menu. It moves only when the new text changes its size.</summary>
    internal void Update(TrayMenuState state)
    {
        if (!IsOpen)
            return;
        Render(state);
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (DesiredSize != placed)
            Place();
    }

    internal void Hide()
    {
        if (IsOpen)
            window!.AppWindow.Hide();
    }

    internal void Close()
    {
        closing = true;
        window?.Close();
        window = null;
    }

    private void Render(TrayMenuState state)
    {
        RequestedTheme = state.Theme;
        OpenLabel.Text = Localization.Get("tray.open");
        CapsuleLabel.Text = Localization.Get(state.CapsuleOpening ? "tray.capsule.loading" : "tray.capsule");
        CapsuleItem.IsEnabled = !state.CapsuleOpening;
        CapsuleCheck.Visibility = state.CapsuleShown ? Visibility.Visible : Visibility.Collapsed;
        NowTitle.Text = state.Title;
        NowArtist.Text = state.Artist;
        NowArtist.Visibility = state.Artist.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PlayGlyph.Glyph = state.Playing ? "" : "";
        Label(PlayItem, state.Playing ? "player.pause" : "player.play");
        Label(PreviousItem, "player.previous");
        Label(NextItem, "player.next");
        PlayItem.IsEnabled = state.CanPlay;
        PreviousItem.IsEnabled = NextItem.IsEnabled = state.CanSkip;
        ExitLabel.Text = Localization.Get("close.quit");
    }

    private static void Label(Button button, string key)
    {
        var text = Localization.Get(key);
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, text);
    }

    // Sized in the icon display's scale, then opened up and to the right of the icon, or wherever
    // that display's work area leaves room.
    private void Place()
    {
        if (window is null)
            return;
        var appWindow = window.AppWindow;
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        placed = DesiredSize;
        var work = DisplayArea.GetFromPoint(anchor, DisplayAreaFallback.Nearest).WorkArea;
        var scale = GetDpiForMonitor(MonitorFromPoint(new() { X = anchor.X, Y = anchor.Y }, 2), 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        appWindow.Move(new PointInt32(work.X, work.Y));
        appWindow.ResizeClient(new SizeInt32((int)Math.Ceiling(placed.Width * scale), (int)Math.Ceiling(placed.Height * scale)));
        var size = appWindow.Size;
        var x = anchor.X + size.Width <= work.X + work.Width ? anchor.X : anchor.X - size.Width;
        var y = anchor.Y - size.Height >= work.Y ? anchor.Y - size.Height : anchor.Y;
        appWindow.Move(new PointInt32(Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - size.Width)), Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - size.Height))));
    }

    private Window CreateWindow()
    {
        var host = new Window
        {
            Content = this,
            SystemBackdrop = new DesktopAcrylicBackdrop()
        };
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        host.AppWindow.SetPresenter(presenter);
        host.AppWindow.IsShownInSwitchers = false;
        host.AppWindow.Title = "OFPlayer";
        // Alt+F4 dismisses the menu like any other; only the app closes the window.
        host.AppWindow.Closing += (_, args) =>
        {
            if (closing)
                return;
            args.Cancel = true;
            Hide();
        };
        host.Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                Hide();
        };
        var handle = Win32Interop.GetWindowFromWindowId(host.AppWindow.Id);
        int round = 2, still = 1;
        DwmSetWindowAttribute(handle, 33, ref round, sizeof(int)); // rounded corners, as the system's menus have
        DwmSetWindowAttribute(handle, 3, ref still, sizeof(int)); // appears at once, without a window's zoom
        return host;
    }

    private void Choose(TrayCommand command)
    {
        Hide();
        Invoked?.Invoke(command);
    }

    private void OpenClick(object sender, RoutedEventArgs e) => Choose(TrayCommand.Open);
    private void CapsuleClick(object sender, RoutedEventArgs e) => Choose(TrayCommand.Capsule);
    private void ExitClick(object sender, RoutedEventArgs e) => Choose(TrayCommand.Exit);
    private void PreviousClick(object sender, RoutedEventArgs e) => Invoked?.Invoke(TrayCommand.Previous);
    private void PlayClick(object sender, RoutedEventArgs e) => Invoked?.Invoke(TrayCommand.PlayPause);
    private void NextClick(object sender, RoutedEventArgs e) => Invoked?.Invoke(TrayCommand.Next);
    private void MenuKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
            return;
        e.Handled = true;
        Hide();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(ScreenPoint point, uint flags);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
