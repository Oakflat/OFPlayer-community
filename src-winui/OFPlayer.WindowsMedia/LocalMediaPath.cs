namespace OFPlayer.WindowsMedia;

/// <summary>Community accepts local filesystem sources, never remote media addresses.</summary>
public static class LocalMediaPath
{
    public static string Validate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // A drive-qualified local path is unambiguous. Reject URLs, UNC shares,
        // device paths and drive-relative paths before touching any media API.
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            path[2] is not ('\\' or '/') || path.IndexOf(':', 2) >= 0)
            throw new ArgumentException("Community 只支持本机媒体文件。", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var drive = new DriveInfo(Path.GetPathRoot(fullPath)!);
        if (drive.DriveType == DriveType.Network)
            throw new ArgumentException("Community 不支持网络磁盘串流。", nameof(path));
        if (!File.Exists(fullPath)) throw new FileNotFoundException("媒体文件不存在。", fullPath);
        return fullPath;
    }
}
