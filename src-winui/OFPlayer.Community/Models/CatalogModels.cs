using System.Text.Json;

namespace OFPlayer_WinUI.Models;
/// <summary>A library entry from `desktop_catalog_load_structure`.</summary>
public sealed record LibraryNode(string Id, string Name, bool IsDefault = false, bool IsLocked = false);
/// <summary>A playlist entry (user "all tracks" playlists included).</summary>
public sealed record PlaylistNode(string Id, string LibraryId, string Name, string Kind, string? SystemKey = null, bool IsLocked = false);
/// <summary>Lightweight navigation tree data (no tracks included).</summary>
public sealed record CatalogStructure(IReadOnlyList<LibraryNode> Libraries, IReadOnlyList<PlaylistNode> Playlists)
{
    public static CatalogStructure FromJson(JsonElement element)
    {
        var libraries = new List<LibraryNode>();
        var playlists = new List<PlaylistNode>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("libraries", out var libraryArray) && libraryArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in libraryArray.EnumerateArray())
                {
                    var id = ReadString(item, "id");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        libraries.Add(new LibraryNode(id, ReadString(item, "name") ?? string.Empty, item.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True, item.TryGetProperty("isLocked", out var locked) && locked.ValueKind == JsonValueKind.True));
                    }
                }
            }

            if (element.TryGetProperty("playlists", out var playlistArray) && playlistArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in playlistArray.EnumerateArray())
                {
                    var id = ReadString(item, "id");
                    var libraryId = ReadString(item, "libraryId");
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(libraryId))
                    {
                        playlists.Add(new PlaylistNode(id, libraryId, ReadString(item, "name") ?? id, ReadString(item, "kind") ?? "user", ReadString(item, "systemKey"), item.TryGetProperty("isLocked", out var locked) && locked.ValueKind == JsonValueKind.True));
                    }
                }
            }
        }

        return new CatalogStructure(libraries, playlists);
    }

    private static string? ReadString(JsonElement element, string propertyName) => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Navigation counts from `desktop_catalog_resolve_navigation`.</summary>
public sealed record NavigationCounts(IReadOnlyDictionary<string, int> LibraryTrackCounts, IReadOnlyDictionary<string, int> PlaylistTrackCounts, IReadOnlyDictionary<string, int> SmartCollectionCounts)
{
    public static readonly NavigationCounts Empty = new(new Dictionary<string, int>(), new Dictionary<string, int>(), new Dictionary<string, int>());
    public int ForLibrary(string? libraryId) => libraryId is not null && LibraryTrackCounts.TryGetValue(libraryId, out var count) ? count : 0;
    public int ForPlaylist(string playlistId) => PlaylistTrackCounts.TryGetValue(playlistId, out var count) ? count : 0;
    public int ForView(string viewKey) => SmartCollectionCounts.TryGetValue(viewKey, out var count) ? count : 0;
    public static NavigationCounts FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        return new NavigationCounts(ReadCounts(element, "libraryTrackCounts"), ReadCounts(element, "playlistTrackCounts"), ReadCounts(element, "smartCollectionCounts"));
    }

    private static IReadOnlyDictionary<string, int> ReadCounts(JsonElement element, string propertyName)
    {
        var counts = new Dictionary<string, int>();
        if (element.TryGetProperty(propertyName, out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in map.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value))
                {
                    counts[property.Name] = value;
                }
            }
        }

        return counts;
    }
}

/// <summary>One row of `desktop_catalog_query_collection_tracks`.</summary>
public sealed record TrackQueryRow(string Id, string LibraryId, string Title, string Artist, string Album, string Format, double DurationSeconds, bool Favorite) : System.ComponentModel.INotifyPropertyChanged
{
    private string title = Title, artist = Artist, album = Album, format = Format;
    private double durationSeconds = DurationSeconds;
    public string Title { get => title; init => title = value; }
    public string Artist { get => artist; init => artist = value; }
    public string Album { get => album; init => album = value; }
    public string Format { get => format; init => format = value; }
    public double DurationSeconds { get => durationSeconds; init => durationSeconds = value; }

    public void ApplyRoomPresentation(TrackQueryRow row)
    {
        if (Id != row.Id)
            throw new ArgumentException("Queue row identity changed.", nameof(row));
        if (Update(ref title, row.Title, nameof(Title)))
        {
            PropertyChanged?.Invoke(this, new(nameof(Monogram)));
            PropertyChanged?.Invoke(this, new(nameof(AvailabilityTip)));
        }

        if (Update(ref artist, row.Artist, nameof(Artist)))
            PropertyChanged?.Invoke(this, new(nameof(Subtitle)));
        Update(ref album, row.Album, nameof(Album));
        Update(ref format, row.Format, nameof(Format));
        if (Update(ref durationSeconds, row.DurationSeconds, nameof(DurationSeconds)))
            PropertyChanged?.Invoke(this, new(nameof(DurationText)));
        IsActive = row.IsActive;
        RowNumber = row.RowNumber;
    }

    private bool Update<T>(ref T storage, T value, string property)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return false;
        storage = value;
        PropertyChanged?.Invoke(this, new(property));
        return true;
    }

    public string DurationText => DurationSeconds > 0 ? TimeSpan.FromSeconds(DurationSeconds).ToString(@"m\:ss") : string.Empty;

    /// <summary>Observable so a toggle updates the row in place instead of replacing it.</summary>
    private bool favorite = Favorite;
    public bool Favorite
    {
        get => favorite;
        set
        {
            if (favorite == value)
                return;
            favorite = value;
            PropertyChanged?.Invoke(this, new(nameof(Favorite)));
            PropertyChanged?.Invoke(this, new(nameof(FavoriteGlyph)));
            PropertyChanged?.Invoke(this, new(nameof(FavoriteVisibility)));
        }
    }

    public string FavoriteGlyph => Favorite ? "\uE735" : "\uE734";
    public Microsoft.UI.Xaml.Visibility FavoriteVisibility => Favorite ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    /// <summary>D2: raw artwork reference from the row payload (file path or data URI).</summary>
    public string? ArtworkPath { get; init; }

    /// <summary>D2: true while this row is the playing track (distinct from selection).</summary>
    private bool isActive;
    public bool IsActive
    {
        get => isActive;
        set
        {
            if (isActive == value)
                return;
            isActive = value;
            PropertyChanged?.Invoke(this, new(nameof(IsActive)));
            PropertyChanged?.Invoke(this, new(nameof(ActiveVisibility)));
            PropertyChanged?.Invoke(this, new(nameof(InactiveVisibility)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string? LocalPath { get; set; }

    private bool isFileMissing;
    public bool IsFileMissing
    {
        get => isFileMissing;
        set
        {
            if (isFileMissing == value)
                return;
            isFileMissing = value;
            PropertyChanged?.Invoke(this, new(nameof(IsFileMissing)));
            PropertyChanged?.Invoke(this, new(nameof(RowOpacity)));
            PropertyChanged?.Invoke(this, new(nameof(Subtitle)));
            PropertyChanged?.Invoke(this, new(nameof(AvailabilityTip)));
        }
    }

    public double RowOpacity => IsFileMissing ? 0.5 : 1;
    public string Subtitle => IsFileMissing ? Services.Localization.Get("missing.file.label") : Artist;
    public string AvailabilityTip => IsFileMissing ? Services.Localization.Get("missing.file.hint") : Title;
    /// <summary>D2: row thumbnail source (file paths only; data URIs stay inspector-only).</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? ArtworkSource { get; init; }
    public Microsoft.UI.Xaml.Visibility ActiveVisibility => IsActive ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility InactiveVisibility => IsActive ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>Alignment: 1-based position in the current query window.</summary>
    private int rowNumber;
    public int RowNumber
    {
        get => rowNumber;
        set
        {
            if (rowNumber == value)
                return;
            rowNumber = value;
            PropertyChanged?.Invoke(this, new(nameof(RowNumber)));
        }
    }

    /// <summary>Alignment: letter placeholder shown when a row has no artwork (old monogram).</summary>
    public string Monogram
    {
        get
        {
            var source = Title?.Trim();
            if (string.IsNullOrEmpty(source))
            {
                return "♪";
            }

            var first = source[0];
            return char.IsAsciiLetter(first) ? char.ToUpperInvariant(first).ToString() : first.ToString();
        }
    }

    /// <summary>What a screen reader announces for the row (the record's default text is its field dump).</summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title}, {Artist}";
    public static TrackQueryRow FromJson(JsonElement element)
    {
        var title = ReadString(element, "displayTitle") ?? ReadString(element, "title") ?? ReadString(element, "fileName") ?? Services.Localization.Get("track.untitled");
        return new TrackQueryRow(ReadString(element, "id") ?? string.Empty, ReadString(element, "libraryId") ?? string.Empty, title, ReadString(element, "artist") ?? ReadString(element, "albumArtist") ?? Services.Localization.Get("track.unknownArtist"), ReadString(element, "album") ?? string.Empty, (ReadString(element, "format") ?? string.Empty).ToUpperInvariant(), ReadNumber(element, "duration"), element.TryGetProperty("isFavorite", out var favorite) && favorite.ValueKind == JsonValueKind.True)
        {
            ArtworkPath = ReadString(element, "artwork"),
        };
    }

    private static string? ReadString(JsonElement element, string propertyName) => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double ReadNumber(JsonElement element, string propertyName) => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0.0;
}

/// <summary>One album/artist group from `desktop_catalog_group_summary`.</summary>
public sealed record GroupEntry(string Kind, string Key, string Label, int TrackCount, double DurationSeconds)
{
    public string? SampleTrackId { get; init; }
    public string Monogram => string.IsNullOrWhiteSpace(Label) ? "♪" : Label[..1];
    public string SummaryText => TrackCount > 0 ? Services.Localization.Format("library.group.summary", ("count", TrackCount.ToString()), ("duration", TimeSpan.FromSeconds(DurationSeconds).ToString(@"h\:mm\:ss"))) : string.Empty;

    public override string ToString() => TrackCount > 0 ? $"{Label}, {SummaryText}" : Label;
    public static List<GroupEntry> ListFromJson(JsonElement element, string kind)
    {
        var groups = new List<GroupEntry>();
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("groups", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var label = item.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.String ? labelElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(label))
                {
                    groups.Add(new GroupEntry(kind, label, label, item.TryGetProperty("trackCount", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 0, item.TryGetProperty("durationSeconds", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetDouble() : 0.0) { SampleTrackId = item.TryGetProperty("sampleTrackId", out var sample) && sample.ValueKind == JsonValueKind.String ? sample.GetString() : null });
                }
            }
        }

        return groups;
    }
}

/// <summary>A page from `desktop_catalog_query_collection_tracks`.</summary>
public sealed record TrackQueryPage(int TotalCount, int Offset, IReadOnlyList<TrackQueryRow> Rows, IReadOnlyList<string> TrackIds, IReadOnlyList<string> AvailableFormats)
{
    public static readonly TrackQueryPage Empty = new(0, 0, Array.Empty<TrackQueryRow>(), Array.Empty<string>(), Array.Empty<string>());
    public static TrackQueryPage FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var rows = new List<TrackQueryRow>();
        if (element.TryGetProperty("rows", out var rowArray) && rowArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rowArray.EnumerateArray())
            {
                rows.Add(TrackQueryRow.FromJson(item));
            }
        }

        var trackIds = new List<string>();
        if (element.TryGetProperty("trackIds", out var idArray) && idArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in idArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    trackIds.Add(item.GetString()!);
                }
            }
        }

        var formats = new List<string>();
        if (element.TryGetProperty("availableFormats", out var formatArray) && formatArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in formatArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    formats.Add(item.GetString()!);
                }
            }
        }

        var totalCount = element.TryGetProperty("totalCount", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetInt32() : rows.Count;
        var offset = element.TryGetProperty("offset", out var offsetElement) && offsetElement.ValueKind == JsonValueKind.Number ? offsetElement.GetInt32() : 0;
        return new TrackQueryPage(totalCount, offset, rows, trackIds, formats);
    }
}
