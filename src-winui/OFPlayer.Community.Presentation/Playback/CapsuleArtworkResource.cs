using System.Security.Cryptography;

namespace OFPlayer.WinUI.Core.Playback;

/// <summary>A bounded image response, never a file-system URL exposed to the page.</summary>
public sealed record CapsuleArtworkResource(string Url, string ContentType, byte[] Bytes)
{
    public const string Origin = "https://capsule-artwork.ofplayer.local/";
    public const int MaxBytes = 4 * 1024 * 1024;

    public static async Task<CapsuleArtworkResource?> LoadAsync(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        byte[] bytes;
        if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma < 0 || !source[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                || source.Length - comma - 1 > ((MaxBytes + 2) / 3) * 4) return null;
            try { bytes = Convert.FromBase64String(source[(comma + 1)..]); }
            catch (FormatException) { return null; }
        }
        else
        {
            if (!Path.IsPathFullyQualified(source) || !File.Exists(source)) return null;
            await using var file = File.OpenRead(source);
            if (file.Length is <= 0 or > MaxBytes) return null;
            bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes);
        }
        if (bytes.Length is <= 0 or > MaxBytes) return null;
        // Determine the actual encoding, including extensionless native cache files.
        var mime = ContentTypeOf(bytes);
        if (mime is null) return null;
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(Origin + hash, mime, bytes);
    }

    private static string? ContentTypeOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.StartsWith("BM"u8)) return "image/bmp";
        return null;
    }
}
