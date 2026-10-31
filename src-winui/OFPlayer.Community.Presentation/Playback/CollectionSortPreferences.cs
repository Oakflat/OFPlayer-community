using System.Text.Json;

namespace OFPlayer.WinUI.Core.Playback;

public sealed class CollectionSortPreferences
{
    private readonly Dictionary<string, string> collections = new();
    private string fallback = "recent";
    public static string Normalize(string? value) => value is "manual" or "title" or "duration" or "size" or "date-added" or "date-modified" ? value : "recent";
    public void Load(JsonElement preferences)
    {
        collections.Clear();
        fallback = "recent";
        // A new profile returns null preferences. The view model passes default
        // JsonElement when there is no object to unwrap.
        if (preferences.ValueKind != JsonValueKind.Object) return;
        string? Read(string key) => preferences.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        fallback = Normalize(Read("librarySortOption") ?? Read("sortOption"));
        if (preferences.TryGetProperty("collectionSortOptions", out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var item in map.EnumerateObject())
                if (item.Value.ValueKind == JsonValueKind.String)
                    collections[item.Name] = Normalize(item.Value.GetString());
    }
    public string Resolve(string? collection) => collection == "view:current-queue" ? "manual"
        : collection is not null && collections.TryGetValue(collection, out var value) ? value : fallback;
    public object Set(string? collection, string option)
    {
        option = Normalize(option);
        if (string.IsNullOrEmpty(collection))
        {
            fallback = option;
            return new { librarySortOption = option };
        }
        collections[collection] = option;
        // The host merges top-level keys; preserve all collection entries in this snapshot.
        return new { collectionSortOptions = new Dictionary<string, string>(collections) };
    }
}
