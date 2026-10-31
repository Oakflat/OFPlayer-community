using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OFPlayer_WinUI.Models;
using OFPlayer_WinUI.Services;
using Windows.Storage.Pickers;

namespace OFPlayer_WinUI.Views;
public sealed partial class LibraryPageView
{
    private bool missingFileDialogOpen;
    private async void MissingFileRequested(TrackQueryRow row) => await ShowMissingFileAsync(row);
    private async Task ShowMissingFileAsync(TrackQueryRow row)
    {
        var model = viewModel;
        if (model is null || missingFileDialogOpen || XamlRoot is null || string.IsNullOrEmpty(row.LocalPath))
            return;
        missingFileDialogOpen = true;
        var library = model.ActiveLibraryId;
        var collection = model.ActiveCollectionRef;
        var inPlaylist = InUserPlaylist;
        try
        {
            var text = Localization.Get;
            var panel = new StackPanel
            {
                Spacing = 16,
                MaxWidth = 460
            };
            panel.Children.Add(new TextBlock { Text = text("missing.file.explain"), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = row.LocalPath, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            var dialog = new ContentDialog
            {
                Title = row.Title,
                Content = panel,
                CloseButtonText = text("common.cancel"),
                XamlRoot = XamlRoot
            };
            var choice = "";
            void Action(string key, string value)
            {
                var button = new Button
                {
                    Content = text(key),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                button.Click += (_, _) =>
                {
                    choice = value;
                    dialog.Hide();
                };
                panel.Children.Add(button);
            }

            Action("missing.file.locate", "locate");
            Action("missing.file.rescan", "scan");
            Action(inPlaylist ? "track.removeFromPlaylist" : "track.deleteFromLibrary", "remove");
            await dialog.ShowAsync();
            if (!IsLoaded || model != viewModel || library != model.ActiveLibraryId || collection != model.ActiveCollectionRef)
                return;
            switch (choice)
            {
                case "locate":
                    var picker = new FileOpenPicker
                    {
                        SuggestedStartLocation = PickerLocationId.MusicLibrary,
                        ViewMode = PickerViewMode.List
                    };
                    InitializePicker(picker);
                    foreach (var extension in AudioFileExtensions)
                        picker.FileTypeFilter.Add(extension);
                    var file = await picker.PickSingleFileAsync();
                    if (file is null)
                        return;
                    var confirm = new ContentDialog
                    {
                        Title = text("missing.file.locate"),
                        Content = new TextBlock
                        {
                            Text = text("missing.file.confirm") + "\n\n" + file.Path,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 460
                        },
                        PrimaryButtonText = text("missing.file.use"),
                        CloseButtonText = text("common.cancel"),
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot,
                    };
                    if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                        await model.RelinkLocalFileAsync(row, file.Path);
                    break;
                case "scan":
                    var folder = await PickFolderAsync();
                    if (folder is not null && library == model.ActiveLibraryId)
                        await model.StartImportDirectoryAsync(folder);
                    break;
                case "remove":
                    if (inPlaylist)
                        await RemoveFromPlaylistAsync(new[] { row.Id });
                    else
                        await DeleteFromLibraryAsync(new[] { row.Id });
                    break;
            }
        }
        catch (Exception error)
        {
            PreviewLog.Write($"missing file dialog failed: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            missingFileDialogOpen = false;
        }
    }
}
