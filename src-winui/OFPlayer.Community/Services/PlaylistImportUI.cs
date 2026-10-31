using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OFPlayer_WinUI.ViewModels;
using OFPlayer_WinUI.Views;
using Windows.Storage.Pickers;

namespace OFPlayer_WinUI.Services;
internal static class PlaylistImportUI
{
    public static async Task RunAsync(MainViewModel model, XamlRoot root, string? libraryId = null)
    {
        if (!model.CanImport)
            return;
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary,
                ViewMode = PickerViewMode.List
            };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, PickerHost.MainWindowHandle);
            picker.FileTypeFilter.Add(".m3u");
            picker.FileTypeFilter.Add(".m3u8");
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;
            if (libraryId is not null && model.ActiveLibraryId != libraryId)
                await model.SelectLibraryAsync(libraryId);
            await model.StartImportPlaylistAsync(file.Path);
            var result = model.LastPlaylistImportResult;
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("skippedFiles", out var skipped) || skipped.ValueKind != JsonValueKind.Array || skipped.GetArrayLength() == 0)
                return;
            var rows = skipped.EnumerateArray().Select(item => $"{item.GetProperty("fileName").GetString()}\n{item.GetProperty("reason").GetString()}");
            var text = Localization.Get("import.playlist.skipped") + "\n\n" + string.Join("\n\n", rows);
            await new ContentDialog
            {
                XamlRoot = root,
                Title = Localization.Get("library.menu.importPlaylist"),
                CloseButtonText = Localization.Get("common.close"),
                Content = new ScrollViewer
                {
                    MaxHeight = 380,
                    Content = new TextBlock
                    {
                        Text = text,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        MaxWidth = 460
                    }
                },
            }.ShowAsync();
        }
        catch (Exception error)
        {
            FailureText.Capture(error, nameof(PlaylistImportUI));
        }
    }
}
