namespace TarkovCompanion.GroupServer.Diagnostics;

/// <summary>How much room a volume has left, for the things that must stop before it is full.</summary>
public static class RelayVolume
{
    private const long BytesPerMegabyte = 1024 * 1024;

    /// <summary>
    /// Free space, in whole megabytes, on the volume that holds <paramref name="path"/>, which need not
    /// exist yet: the nearest ancestor that does is asked. Zero when it cannot be found out, which the
    /// callers treat as short, because "unknown" is not "plenty".
    /// </summary>
    public static int FreeMegabytes(string path)
    {
        try
        {
            var probe = Path.GetFullPath(path);
            while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is { Length: > 0 } parent)
            {
                probe = parent;
            }

            return (int)Math.Min(int.MaxValue, new DriveInfo(probe).AvailableFreeSpace / BytesPerMegabyte);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }
}
