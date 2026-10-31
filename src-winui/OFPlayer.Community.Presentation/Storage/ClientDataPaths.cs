namespace OFPlayer.WinUI.Core.Storage;

/// <summary>The single desktop data root, regardless of shortcut or developer launch.</summary>
public sealed record ClientDataPaths(string Root, string VideoRoot, bool HasExplicitOverride)
{
    public static ClientDataPaths Resolve(string? overrideRoot, string localAppData)
    {
        var explicitRoot = !string.IsNullOrWhiteSpace(overrideRoot);
        var root = explicitRoot ? overrideRoot! : Path.Combine(localAppData, "ofplayer-winui");
        if (!Path.IsPathFullyQualified(root) || (!explicitRoot && !Path.IsPathFullyQualified(localAppData)))
            throw new IOException("client-data-root-invalid");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var parent = Path.GetDirectoryName(root);
        if (string.IsNullOrWhiteSpace(parent)) throw new IOException("client-data-root-invalid");
        return new(root, Path.Combine(parent, "OFPlayer.Video"), explicitRoot);
    }
}
