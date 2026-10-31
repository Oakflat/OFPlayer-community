using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using OFPlayer_WinUI.Services;
using OFPlayer_WinUI.Views;

namespace OFPlayer.Community;
public sealed partial class MainWindow
{
    private TrayIconHost? trayIcon;
    private TrayMenu? trayMenu;
    private bool inTray, closePrompt;
    private TrayMenuState CurrentTrayState() => new(Root.RequestedTheme, model.PlaybackTitle, model.PlaybackArtist, model.IsPlaying, model.CanPlay, model.CanSkip, CapsuleWindow.IsVisible, CapsuleWindow.IsOpening);
    private void InitializeTray()
    {
        try
        {
            trayIcon = new(command => DispatcherQueue.TryEnqueue(async () => await TrayAction(command)), (x, y) => DispatcherQueue.TryEnqueue(() =>
            {
                trayMenu ??= new();
                trayMenu.Invoked -= TrayMenuInvoked;
                trayMenu.Invoked += TrayMenuInvoked;
                trayMenu.Open(new(x, y), CurrentTrayState());
            }), error => DispatcherQueue.TryEnqueue(() =>
            {
                RestoreFromTray();
                FailureText.Capture(error, "Tray");
            }));
        }
        catch (Exception error)
        {
            FailureText.Capture(error, "Initialize tray");
        }
    }

    private async void TrayMenuInvoked(TrayCommand command) => await TrayAction(command);
    private async Task TrayAction(TrayCommand command)
    {
        try
        {
            switch (command)
            {
                case TrayCommand.Open:
                    RestoreFromTray();
                    break;
                case TrayCommand.PlayPause:
                    await model.TogglePlaybackAsync();
                    break;
                case TrayCommand.Previous:
                    await model.PlayPreviousAsync();
                    break;
                case TrayCommand.Next:
                    await model.PlayNextAsync();
                    break;
                case TrayCommand.Capsule:
                    if (CapsuleWindow.IsOpen)
                        CapsuleWindow.HideIfOpen();
                    else
                        CapsuleWindow.ShowOrActivate();
                    break;
                case TrayCommand.Exit:
                    await ExitApplication();
                    break;
            }
        }
        catch (Exception error)
        {
            RestoreFromTray();
            FailureText.Capture(error, "Tray action");
        }
    }

    private void EnterTray()
    {
        if (trayIcon is null)
        {
            if (AppWindow.Presenter is OverlappedPresenter p)
                p.Minimize();
            return;
        }

        trayIcon.Show();
        inTray = true;
        CloseImmersive();
        AppWindow.Hide();
        PlaybackBar.SetPresentationActive(false);
        immersive?.SetBackgroundMode(true);
        CapsuleWindow.SetBackgroundMode(true);
    }

    internal void RestoreFromTray()
    {
        inTray = false;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter p)
            p.Restore();
        Activate();
        PlaybackBar.SetPresentationActive(true);
        immersive?.SetBackgroundMode(false);
        CapsuleWindow.SetBackgroundMode(false);
        ApplyContentBackdrop();
    }

    private async void Closing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (closeReady)
            return;
        e.Cancel = true;
        if (closing || closePrompt)
            return;
        var choice = model.CloseBehaviorPreference;
        if (choice == "ask")
        {
            closePrompt = true;
            try
            {
                var remember = new CheckBox
                {
                    Content = "记住我的选择"
                };
                var panel = new StackPanel
                {
                    Spacing = 16
                };
                panel.Children.Add(new TextBlock { Text = "最小化后会继续播放，可从托盘返回。", TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(remember);
                var result = await new ContentDialog
                {
                    XamlRoot = Root.XamlRoot,
                    Title = "关闭 OFPlayer？",
                    Content = panel,
                    PrimaryButtonText = "最小化",
                    SecondaryButtonText = "退出",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary
                }.ShowAsync();
                if (result == ContentDialogResult.None)
                    return;
                choice = result == ContentDialogResult.Primary ? "minimize" : "exit";
                if (remember.IsChecked == true)
                    await model.SetCloseBehaviorAsync(choice);
            }
            finally
            {
                closePrompt = false;
            }
        }

        if (choice == "minimize")
        {
            EnterTray();
            return;
        }

        await ExitApplication();
    }

    private async Task ExitApplication()
    {
        if (closing)
            return;
        closing = true;
        sidebarWidth?.Stop();
        smartHoverTimer?.Stop();
        Localization.LanguageChanged -= ApplyNavigationStrings;
        videoTimer?.Stop();
        contentBackdropLoad?.Cancel();
        try
        {
            trayMenu?.Close();
            trayIcon?.Dispose();
            CapsuleWindow.CloseIfOpen();
            media?.Dispose();
            await SaveVideos();
            video?.Dispose();
            await model.DisposeAsync();
        }
        catch (Exception error)
        {
            PreviewLog.Write("Community shutdown: " + error.Message);
        }
        finally
        {
            Current = null;
            closeReady = true;
            Close();
        }
    }
}
