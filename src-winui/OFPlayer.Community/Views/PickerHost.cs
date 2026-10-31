namespace OFPlayer_WinUI.Views;
internal static class PickerHost
{
    public static nint MainWindowHandle => OFPlayer.Community.MainWindow.Current?.Hwnd ?? 0;
}
