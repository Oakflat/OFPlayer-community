using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OFPlayer_WinUI.Models;
using OFPlayer_WinUI.Services;
using Windows.UI;

namespace OFPlayer_WinUI.Views;
/// <summary>
/// Listening history with scoped requests, daily activity and artwork rankings.
/// Late requests are ignored when the range changes or the dialog closes.
/// </summary>
public sealed partial class ListeningStatsDialog : ContentDialog
{
    private int HeatmapDays = 120;
    private Func<int, Task<ListeningStatsData?>>? loader;
    private int revision;
    private bool closed;
    public void Initialize(Func<int, Task<ListeningStatsData?>> load) => loader = load;
    private async void RangeChanged(object sender, SelectionChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("ListeningStatsDialog.xaml.RangeChanged");
        if (Range.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var days))
            HeatmapDays = days;
        if (loader is not null)
            await ReloadAsync();
    }

    private async void RefreshClicked(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("ListeningStatsDialog.xaml.RefreshClicked");
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ListeningStatsDialog.xaml.ReloadAsync");
        if (loader is null || closed)
            return;
        var request = ++revision;
        BusyIndicator.Visibility = Visibility.Visible;
        Results.Visibility = Visibility.Collapsed;
        Notice.IsOpen = false;
        try
        {
            var data = await loader(HeatmapDays);
            if (!closed && request == revision)
                LoadFrom(data);
        }
        catch (Exception)
        {
            if (!closed && request == revision)
                ShowEmpty(Localization.Get("stats.loadFailed"));
        }
        finally
        {
            if (!closed && request == revision)
                BusyIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnArtworkLoaded(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ListeningStatsDialog.xaml.OnArtworkLoaded");
        if (sender is Image image && image.DataContext is StatsRowItem row)
            await ArtworkLoader.ApplyAsync(image, row.Artwork);
    }

    public ListeningStatsDialog()
    {
        InitializeComponent();
        ApplyStrings();
        Opened += async (_, _) =>
        {
            Scroller.Width = Math.Max(280, Math.Min(760, XamlRoot.Size.Width - 100));
            Scroller.MaxHeight = Math.Max(180, XamlRoot.Size.Height - 210);
            await ReloadAsync();
        };
        Closed += (_, _) =>
        {
            closed = true;
            revision++;
        };
    }

    private void ApplyStrings()
    {
        Title = Localization.Get("stats.title");
        CloseButtonText = Localization.Get("stats.done");
        SubtitleLabel.Text = Localization.Get("stats.subtitle");
        TotalLabel.Text = Localization.Get("stats.total");
        PlaysLabel.Text = Localization.Get("stats.plays");
        ActiveLabel.Text = Localization.Get("stats.active");
        StreakLabel.Text = Localization.Get("stats.streak");
        CalendarLabel.Text = Localization.Get("stats.calendar");
        TopTracksTab.Header = Localization.Get("stats.topTracks");
        TopAlbumsTab.Header = Localization.Get("stats.topAlbums");
        RefreshButton.Content = Localization.Get("stats.refresh");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Range, Localization.Get("stats.range"));
        foreach (var item in Range.Items.OfType<ComboBoxItem>())
            item.Content = item.Tag?.ToString() == "365" ? Localization.Get("stats.range.year") : Localization.Format("stats.range.days", ("count", item.Tag?.ToString() ?? ""));
    }

    public void LoadFrom(ListeningStatsData? data)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ListeningStatsDialog.xaml.LoadFrom");
        if (data is null)
        {
            ShowEmpty(Localization.Get("stats.unavailable"));
            return;
        }

        Results.Visibility = Visibility.Visible;
        Notice.IsOpen = data.Summary.PlayCount == 0;
        Notice.Severity = InfoBarSeverity.Informational;
        Notice.Message = Localization.Get("stats.empty");
        var summary = data.Summary;
        StatTotal.Text = FormatDuration(summary.TotalSeconds);
        StatPlays.Text = summary.PlayCount.ToString("N0", Localization.Culture);
        StatActive.Text = summary.ActiveDays.ToString("N0", Localization.Culture);
        StatStreak.Text = summary.LongestStreakDays > 0 ? Localization.Format("stats.days", ("count", summary.LongestStreakDays.ToString("N0", Localization.Culture))) : "—";
        var collection = Localization.Format("stats.collection", ("tracks", summary.TrackCount.ToString("N0", Localization.Culture)), ("albums", summary.AlbumCount.ToString("N0", Localization.Culture)));
        PeakLine.Text = summary.PeakDay is { Length: > 0 } ? Localization.Format("stats.peak", ("date", DisplayDate(summary.PeakDay)), ("duration", FormatDuration(summary.PeakDaySeconds))) + " · " + collection : collection;
        BuildHeatmap(data.Daily);
        BuildTopTracks(data.TopTracks);
        BuildAlbumGroups(data.AlbumGroups);
    }

    private void ShowEmpty(string message)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ListeningStatsDialog.xaml.ShowEmpty");
        Notice.Message = message;
        Notice.Severity = InfoBarSeverity.Warning;
        Notice.IsOpen = true;
        Results.Visibility = Visibility.Collapsed;
    }

    private void BuildHeatmap(IReadOnlyList<ListeningStatsDay> daily)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ListeningStatsDialog.xaml.BuildHeatmap");
        HeatmapHost.Children.Clear();
        HeatmapHost.ColumnDefinitions.Clear();
        HeatmapHost.RowDefinitions.Clear();
        var byDate = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var day in daily)
        {
            byDate[day.Date] = day.Seconds;
        }

        var last = DateOnly.FromDateTime(DateTime.Today);
        var start = last.AddDays(-(HeatmapDays - 1));
        var leading = ((int)start.DayOfWeek + 6) % 7; // Monday = 0
        var totalCells = leading + HeatmapDays;
        var weeks = (int)Math.Ceiling(totalCells / 7.0);
        var maxSeconds = daily.Where(day => DateOnly.TryParse(day.Date, out var date) && date >= start && date <= last).Select(day => day.Seconds).DefaultIfEmpty(0).Max();
        CalendarRange.Text = Localization.Format("stats.calendar.intensity", ("start", start.ToString("d", Localization.Culture)), ("end", last.ToString("d", Localization.Culture)));
        if (HeatmapDays <= 30)
        {
            CalendarRange.Text = Localization.Format("stats.calendar.daily", ("start", start.ToString("d", Localization.Culture)), ("end", last.ToString("d", Localization.Culture)));
            for (var dayIndex = 0; dayIndex < HeatmapDays; dayIndex++)
            {
                var date = start.AddDays(dayIndex);
                var seconds = byDate.GetValueOrDefault(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                HeatmapHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var column = new Grid
                {
                    Width = Math.Max(10, (Scroller.Width - 64) / HeatmapDays),
                    Height = 128,
                };
                column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
                column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var bar = new Border
                {
                    Height = maxSeconds > 0 ? Math.Max(2, seconds / maxSeconds * 96) : 2,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(2, 0, 2, 0),
                    CornerRadius = new CornerRadius(3),
                    Background = LevelBrush(seconds, maxSeconds),
                };
                var description = date.ToString("d", Localization.Culture) + " · " + FormatDuration(seconds);
                ToolTipService.SetToolTip(column, description);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(column, description);
                column.Children.Add(bar);
                if (HeatmapDays == 7 || dayIndex % 5 == 0 || dayIndex == HeatmapDays - 1)
                {
                    var label = new TextBlock
                    {
                        Text = date.Day.ToString(),
                        FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 6, 0, 0)
                    };
                    Grid.SetRow(label, 1);
                    column.Children.Add(label);
                }

                Grid.SetColumn(column, dayIndex);
                HeatmapHost.Children.Add(column);
            }

            return;
        }

        for (var column = 0; column < weeks; column++)
        {
            HeatmapHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var row = 0; row < 7; row++)
        {
            HeatmapHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        // Keep calendar cells square at every range; a wider dialog must not
        // stretch only their horizontal dimension.
        var cellSize = Math.Max(8, Math.Min(20, Math.Floor((Scroller.Width - 64) / weeks - 2)));
        for (var index = leading; index < totalCells; index++)
        {
            var date = start.AddDays(index - leading);
            var key = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var seconds = byDate.TryGetValue(key, out var value) ? value : 0;
            var cell = new Border
            {
                Width = cellSize,
                Height = cellSize,
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Background = LevelBrush(seconds, maxSeconds),
            };
            ToolTipService.SetToolTip(cell, date.ToString("d", Localization.Culture) + " · " + (seconds > 0 ? FormatDuration(seconds) : Localization.Get("stats.noHistory")));
            Grid.SetColumn(cell, index / 7);
            Grid.SetRow(cell, index % 7);
            HeatmapHost.Children.Add(cell);
        }
    }

    private void BuildTopTracks(IReadOnlyList<ListeningStatsTrackRow> tracks)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ListeningStatsDialog.xaml.BuildTopTracks");
        var rows = tracks.Take(10).Select((track, index) => new StatsRowItem((index + 1).ToString(), track.Title, string.IsNullOrWhiteSpace(track.Artist) ? "—" : track.Artist, Localization.Format("stats.rank", ("count", track.PlayCount.ToString("N0", Localization.Culture)), ("duration", FormatDuration(track.ListenSeconds))), track.Artwork)).ToList();
        TopTracksList.ItemsSource = rows;
        TopTracksList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildAlbumGroups(IReadOnlyList<ListeningStatsAlbumRow> albums)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ListeningStatsDialog.xaml.BuildAlbumGroups");
        var rows = albums.Take(6).Select((album, index) => new StatsRowItem((index + 1).ToString(), album.Album, string.IsNullOrWhiteSpace(album.AlbumArtist) ? "—" : album.AlbumArtist, Localization.Format("stats.rank", ("count", album.PlayCount.ToString("N0", Localization.Culture)), ("duration", FormatDuration(album.ListenSeconds))), album.Artwork)).ToList();
        AlbumGroupsList.ItemsSource = rows;
        AlbumGroupsList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush LevelBrush(double seconds, double maxSeconds)
    {
        var level = 0;
        if (seconds > 0 && maxSeconds > 0)
        {
            var ratio = seconds / maxSeconds;
            level = ratio switch
            {
                < 0.25 => 1,
                < 0.5 => 2,
                < 0.75 => 3,
                _ => 4,
            };
        }

        var accent = (Color)Application.Current.Resources["SystemAccentColor"];
        return new SolidColorBrush(level == 0 ? Color.FromArgb(28, 128, 128, 128) : Color.FromArgb((byte)(60 + level * 48), accent.R, accent.G, accent.B));
    }

    private static string DisplayDate(string date) => DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed.ToString("d", Localization.Culture) : date;
    private static string FormatDuration(double seconds) => seconds switch
    {
        >= 3600 => Localization.Format("stats.duration.hours", ("count", (seconds / 3600).ToString("0.#", Localization.Culture))),
        >= 60 => Localization.Format("stats.duration.minutes", ("count", (seconds / 60).ToString("0.#", Localization.Culture))),
        _ => Localization.Format("stats.duration.seconds", ("count", seconds.ToString("0", Localization.Culture))),
    };
}

/// <summary>Row model for the top-track template (reflection-bound).</summary>
public sealed record StatsRowItem(string Rank, string Title, string Subtitle, string TimesText, string Artwork);
