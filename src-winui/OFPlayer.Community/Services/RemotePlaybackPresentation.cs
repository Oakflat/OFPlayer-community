using OFPlayer_WinUI.Models;
using System.Text.Json;

namespace OFPlayer_WinUI.Services;
/// <summary>Presentation-only metadata for locally resolved third-party tracks.</summary>
public static class RemotePlaybackPresentation
{
    public static bool IsExternalTrack(string? id) => id == "external";
    public static bool UsesCurrentDetail(string? requested, string? active, bool receiver = false) => IsExternalTrack(requested);
    public static JsonElement WebDetail(TrackDetail? detail, string id, bool artwork) => detail?.Id != id ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement(new { id, displayTitle = detail.Title, title = detail.Title, artist = detail.Artist, album = detail.Album, artwork = artwork ? detail.Artwork : null });
}
