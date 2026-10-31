using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OFPlayer_WinUI.ViewModels;
using OFPlayer_WinUI.Services;
using Microsoft.UI.Xaml.Automation;

namespace OFPlayer_WinUI.Views;
public sealed partial class StorageAnalysisDialog : ContentDialog
{
    private readonly MainViewModel viewModel;
    private bool busy;
    private bool closed;
    private double reclaimable;
    private JsonElement lastSnapshot;
    public StorageAnalysisDialog(MainViewModel model)
    {
        viewModel = model;
        InitializeComponent();
        ApplyStrings();
        ActualThemeChanged += (_, _) =>
        {
            if (!closed && lastSnapshot.ValueKind == JsonValueKind.Object)
                Apply(lastSnapshot);
        };
        Opened += async (_, _) =>
        {
            Scroller.Width = Math.Max(280, Math.Min(720, XamlRoot.Size.Width - 100));
            Scroller.MaxHeight = Math.Max(180, XamlRoot.Size.Height - 210);
            await AnalyzeAsync();
        };
        Localization.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            closed = true;
            Localization.LanguageChanged -= OnLanguageChanged;
        };
    }

    private void OnLanguageChanged()
    {
        if (closed)
            return;
        ApplyStrings();
        if (lastSnapshot.ValueKind == JsonValueKind.Object)
            Apply(lastSnapshot);
        SetBusy(busy);
    }

    private void ApplyStrings()
    {
        this.Title = Localization.Get("storage.title");
        LocaleLabel1.Text = Localization.Get("storage.subtitle");
        LocaleLabel2.Text = Localization.Get("storage.total");
        LocaleLabel3.Text = Localization.Get("storage.reclaimable");
        LocaleLabel4.Text = Localization.Get("storage.scope");
        LocaleLabel5.Text = Localization.Get("storage.cleanup.title");
        LocaleLabel6.Text = Localization.Get("storage.cleanup.body");
        Cleanup.Content = Localization.Get("storage.cleanup.review");
        LocaleLabel7.Text = Localization.Get("storage.cleanup.confirmation");
        LocaleLabel8.Content = Localization.Get("storage.cleanup.confirm");
        Refresh.Content = Localization.Get("storage.refresh");
        this.CloseButtonText = Localization.Get("common.done");
        LocaleLabel9.Content = Localization.Get("common.cancel");
    }

    private static double Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number) ? Math.Max(0, number) : 0;
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    private static string Bytes(double value) => value switch
    {
        >= 1073741824 => (value / 1073741824).ToString("0.##", Localization.Culture) + " GB",
        >= 1048576 => (value / 1048576).ToString("0.##", Localization.Culture) + " MB",
        >= 1024 => (value / 1024).ToString("0.##", Localization.Culture) + " KB",
        _ => value.ToString("0", Localization.Culture) + " B",
    };
    private static string Label(string key) => key switch
    {
        "managedAudio" => Localization.Get("storage.category.managedAudio"),
        "database" => Localization.Get("storage.category.database"),
        "trackArtworkAssets" => Localization.Get("storage.category.trackArtworkAssets"),
        "capsuleArtworkCache" => Localization.Get("storage.category.capsuleArtworkCache"),
        "externalPlaybackCache" => Localization.Get("storage.category.externalPlaybackCache"),
        "otherCache" => Localization.Get("storage.category.otherCache"),
        "diagnostics" => Localization.Get("storage.category.diagnostics"),
        _ => key,
    };
    private void Apply(JsonElement snapshot)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("StorageAnalysisDialog.xaml.Apply");
        if (snapshot.ValueKind != JsonValueKind.Object || !snapshot.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(Localization.Get("storage.invalid"));
        lastSnapshot = snapshot.Clone();
        var total = Number(snapshot, "totalBytes");
        reclaimable = Number(snapshot, "reclaimableBytes");
        Total.Text = Bytes(total);
        Reclaimable.Text = Bytes(reclaimable);
        Categories.Children.Clear();
        UsageStrip.Children.Clear();
        UsageStrip.ColumnDefinitions.Clear();
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            var bytes = Number(item, "bytes");
            var key = Text(item, "key");
            var color = CategoryColor(key);
            var brush = new SolidColorBrush(color);
            var row = new Grid
            {
                ColumnSpacing = 14
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var details = new StackPanel
            {
                Spacing = 4
            };
            details.Children.Add(new TextBlock { Text = Label(Text(item, "key")), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            details.Children.Add(WindowTheme.Brush(new TextBlock { Text = Localization.Format("storage.item", ("count", Number(item, "fileCount").ToString("N0", Localization.Culture)), ("size", Bytes(Number(item, "reclaimableBytes")))), FontSize = 12, }, "Foreground", "TextFillColorSecondaryBrush"));
            ToolTipService.SetToolTip(details, Text(item, "path"));
            var icon = new FontIcon
            {
                Glyph = CategoryGlyph(key),
                FontSize = 17,
                Foreground = brush
            };
            row.Children.Add(new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, color.R, color.G, color.B)), Child = icon, VerticalAlignment = VerticalAlignment.Center, });
            Grid.SetColumn(details, 1);
            row.Children.Add(details);
            var amount = new TextBlock
            {
                Text = Bytes(bytes),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(amount, 2);
            row.Children.Add(amount);
            Categories.Children.Add(row);
            if (bytes <= 0)
                continue;
            UsageStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bytes, GridUnitType.Star) });
            var segment = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = brush,
            };
            ToolTipService.SetToolTip(segment, $"{Label(Text(item, "key"))} · {Bytes(bytes)}");
            Grid.SetColumn(segment, index++);
            UsageStrip.Children.Add(segment);
        }

        Results.Visibility = Visibility.Visible;
        SetWarnings(snapshot);
    }

    private Windows.UI.Color CategoryColor(string key)
    {
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            return new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground);
        var dark = ActualTheme == ElementTheme.Dark;
        var rgb = key switch
        {
            "managedAudio" => dark ? 0x83B6FF : 0x3869B2,
            "database" => dark ? 0xB6A1EE : 0x7859AD,
            "trackArtworkAssets" => dark ? 0xECA2BC : 0xAA5278,
            "capsuleArtworkCache" => dark ? 0xE9C080 : 0x92681E,
            "externalPlaybackCache" => dark ? 0x81CDC0 : 0x277D71,
            "otherCache" => dark ? 0xA3B2C7 : 0x5B6B82,
            "diagnostics" => dark ? 0xDFAC8A : 0x9A6036,
            _ => dark ? 0xB7BCC6 : 0x636976,
        };
        return Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static string CategoryGlyph(string key) => key switch
    {
        "managedAudio" => "\uE8D6",
        "database" => "\uE8F1",
        "trackArtworkAssets" => "\uEB9F",
        "capsuleArtworkCache" => "\uE8A5",
        "externalPlaybackCache" => "\uE896",
        "otherCache" => "\uE8B7",
        "diagnostics" => "\uE9D9",
        _ => "\uE8B7",
    };
    private void SetWarnings(JsonElement result)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("StorageAnalysisDialog.xaml.SetWarnings");
        var warnings = result.TryGetProperty("warnings", out var list) && list.ValueKind == JsonValueKind.Array ? string.Join("\n", list.EnumerateArray().Select(item => item.ToString())) : "";
        Notice.IsOpen = !string.IsNullOrWhiteSpace(warnings);
        Notice.Severity = InfoBarSeverity.Warning;
        Notice.Message = warnings;
    }

    private void SetBusy(bool value)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("StorageAnalysisDialog.xaml.SetBusy");
        busy = value;
        BusyIndicator.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        Refresh.IsEnabled = !value;
        Cleanup.IsEnabled = !value && reclaimable > 0 && !viewModel.IsImporting && !viewModel.Playback.Playing;
        CleanupHint.Text = value ? Localization.Get("storage.busy") : viewModel.IsImporting ? Localization.Get("storage.waitImport") : viewModel.Playback.Playing ? Localization.Get("storage.pauseFirst") : reclaimable <= 0 ? Localization.Get("storage.nothing") : Localization.Get("storage.confirmFirst");
    }

    private async Task AnalyzeAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("StorageAnalysisDialog.xaml.AnalyzeAsync");
        if (busy || closed)
            return;
        SetBusy(true);
        Notice.IsOpen = false;
        Confirmation.Visibility = Visibility.Collapsed;
        try
        {
            var result = await viewModel.LoadStorageAnalysisAsync();
            if (!closed)
                Apply(result);
        }
        catch (Exception error)
        {
            if (!closed)
            {
                reclaimable = 0;
                Results.Visibility = Visibility.Collapsed;
                ShowError(error);
            }
        }
        finally
        {
            if (!closed)
                SetBusy(false);
        }
    }

    private void ShowError(Exception error)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("StorageAnalysisDialog.xaml.ShowError");
        Notice.Severity = InfoBarSeverity.Error;
        Notice.Message = error.Message;
        Notice.IsOpen = true;
    }

    private async void RefreshClicked(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("StorageAnalysisDialog.xaml.RefreshClicked");
        await AnalyzeAsync();
    }

    private void ReviewCleanup(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("StorageAnalysisDialog.xaml.ReviewCleanup");
        Confirmation.Visibility = Visibility.Visible;
    }

    private void CancelCleanup(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("StorageAnalysisDialog.xaml.CancelCleanup");
        Confirmation.Visibility = Visibility.Collapsed;
    }

    private async void ConfirmCleanup(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("StorageAnalysisDialog.xaml.ConfirmCleanup");
        if (busy || viewModel.IsImporting || viewModel.Playback.Playing)
            return;
        SetBusy(true);
        Confirmation.Visibility = Visibility.Collapsed;
        try
        {
            var result = await viewModel.RunStorageCleanupAsync();
            if (closed)
                return;
            Apply(result.GetProperty("after"));
            SetWarnings(result);
            var warning = Notice.Message;
            Notice.Message = Localization.Format("storage.freed", ("size", Bytes(Number(result, "reclaimedBytes")))) + (string.IsNullOrEmpty(warning) ? "" : "\n" + warning);
            Notice.Severity = string.IsNullOrEmpty(warning) ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            Notice.IsOpen = true;
        }
        catch (Exception error)
        {
            if (!closed)
            {
                reclaimable = 0;
                ShowError(error);
                Notice.Message += "\n" + Localization.Get("storage.retryHint");
            }
        }
        finally
        {
            if (!closed)
                SetBusy(false);
        }
    }
}
