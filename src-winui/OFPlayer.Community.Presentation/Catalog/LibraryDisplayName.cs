namespace OFPlayer.WinUI.Core.Catalog;

/// <summary>Resolves the display label without changing the stored library name.</summary>
public static class LibraryDisplayName
{
    public static string Resolve(string? id, string? name, bool isDefault, string defaultLabel, string otherLabel) =>
        !string.IsNullOrWhiteSpace(name) ? name
        : isDefault || id == "library-default" ? defaultLabel : otherLabel;
}
