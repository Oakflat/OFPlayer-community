using System.Text.Json;

namespace OFPlayer_WinUI.Models;
/// <summary>
/// Display-only playback snapshot decoded from `playback.changed` / command results.
/// The Rust host remains the single source of truth; the preview never commands a
/// second engine and never invents playback state locally.
/// </summary>
public sealed record PlaybackStateSnapshot(string Status, string? ActiveTrackId, double PositionSeconds, double DurationSeconds, double Volume)
{
    public static readonly PlaybackStateSnapshot Idle = new("idle", null, 0, 0, 1.0);
    public bool Playing => Status == "playing";
    public ulong? Revision { get; init; }
    /// <summary>The host's signal path for this revision; null means unknown, never "unchanged".</summary>
    public OFPlayer.WinUI.Core.Playback.AudioSignalPath? SignalPath { get; init; }

    public static PlaybackStateSnapshot FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Idle;
        }

        var status = element.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String ? statusElement.GetString() ?? "idle" : "idle";
        var activeTrackId = element.TryGetProperty("activeTrackId", out var trackElement) && trackElement.ValueKind == JsonValueKind.String ? trackElement.GetString() : null;
        var position = element.TryGetProperty("currentTime", out var positionElement) && positionElement.ValueKind == JsonValueKind.Number ? positionElement.GetDouble() : 0.0;
        var duration = element.TryGetProperty("duration", out var durationElement) && durationElement.ValueKind == JsonValueKind.Number ? durationElement.GetDouble() : 0.0;
        var volume = element.TryGetProperty("volume", out var volumeElement) && volumeElement.ValueKind == JsonValueKind.Number ? volumeElement.GetDouble() : 1.0;
        return new PlaybackStateSnapshot(status, activeTrackId, position, duration, volume)
        {
            Revision = OFPlayer.WinUI.Core.Playback.PlaybackStateOrder.ReadRevision(element, "playbackRevision"),
            SignalPath = OFPlayer.WinUI.Core.Playback.AudioSignalPath.FromSnapshot(element),
        };
    }
}

/// <summary>One output device from `playback_list_output_devices`.</summary>
public sealed record PlaybackOutputDevice(string Id, string Name, string BackendLabel, bool IsDefault)
{
    public string DisplayName => IsDefault ? Services.Localization.Format("player.output.default", ("name", Name)) : Name;

    public static PlaybackOutputDevice FromJson(JsonElement element) => new(element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? string.Empty : string.Empty, element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? string.Empty : string.Empty, element.TryGetProperty("backendLabel", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() ?? string.Empty : string.Empty, element.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True);
}

/// <summary>Device list plus the currently active/preferred selection.</summary>
public sealed record PlaybackOutputDevicesSnapshot(IReadOnlyList<PlaybackOutputDevice> Devices, string? ActiveDeviceId, string? ActiveDeviceName, bool PrefersSystemDefault)
{
    /// <summary>The saved choice, which may differ from the device that is open now.</summary>
    public string? PreferredDeviceId { get; init; }
    /// <summary>False while the saved device is not connected; the host then plays elsewhere.</summary>
    public bool PreferredDeviceAvailable { get; init; } = true;

    public static readonly PlaybackOutputDevicesSnapshot Empty = new(Array.Empty<PlaybackOutputDevice>(), null, null, true);
    public static PlaybackOutputDevicesSnapshot FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var devices = new List<PlaybackOutputDevice>();
        if (element.TryGetProperty("devices", out var devicesElement) && devicesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var device in devicesElement.EnumerateArray())
            {
                devices.Add(PlaybackOutputDevice.FromJson(device));
            }
        }

        var activeId = element.TryGetProperty("activeDeviceId", out var activeElement) && activeElement.ValueKind == JsonValueKind.String ? activeElement.GetString() : null;
        var activeName = element.TryGetProperty("activeDeviceName", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
        var prefersDefault = !(element.TryGetProperty("prefersSystemDefault", out var prefersElement) && prefersElement.ValueKind == JsonValueKind.False);
        return new PlaybackOutputDevicesSnapshot(devices, activeId, activeName, prefersDefault)
        {
            PreferredDeviceId = element.TryGetProperty("preferredDeviceId", out var preferredElement) && preferredElement.ValueKind == JsonValueKind.String ? preferredElement.GetString() : null,
            PreferredDeviceAvailable = !(element.TryGetProperty("preferredDeviceAvailable", out var availableElement) && availableElement.ValueKind == JsonValueKind.False),
        };
    }
}
