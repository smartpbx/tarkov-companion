namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Holds a new file back until it has stopped growing.
/// </summary>
/// <remarks>
/// The screenshot watcher yielded a path the instant it appeared in a directory listing and
/// added it to a seen set for good, so a frame the game was still writing was handed straight
/// to the recogniser — with black rows where the rest of the picture had not landed — and never
/// looked at again. One press of the screenshot key, one chance, and the chance was taken
/// before the file existed in full.
///
/// The rule is deliberately not a delay. A fixed wait is either too short on a slow disk or a
/// tax on every screenshot on a fast one; a length that has not moved between two polls says
/// the writer has finished, whatever the disk is doing.
///
/// Separate from the watcher so the Linux suite can pin it, which is the same reason
/// <see cref="AppendedLineReader"/> lives here.
/// </remarks>
public sealed class SettledFileGate
{
    private readonly Dictionary<string, long> _growing = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _released = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this file is finished and has not been released before.
    /// </summary>
    /// <param name="path">Identifies the file across polls.</param>
    /// <param name="length">Its length now; a negative length means it could not be measured.</param>
    /// <remarks>
    /// A file is released on the poll where its length matches the previous poll's. A file
    /// measured once is never released on that first sighting, because one measurement cannot
    /// tell a finished file from one that is still being written.
    /// </remarks>
    public bool IsSettled(string path, long length)
    {
        if (_released.Contains(path))
        {
            return false;
        }

        // Unmeasurable usually means the game still holds it exclusively, which is itself a
        // reason to wait. Forget the length so the next successful measurement starts again.
        if (length < 0)
        {
            _growing.Remove(path);
            return false;
        }

        if (!_growing.TryGetValue(path, out var previous) || previous != length)
        {
            _growing[path] = length;
            return false;
        }

        _growing.Remove(path);
        _released.Add(path);
        return true;
    }

    /// <summary>Treats a file as already handled without waiting for it to settle.</summary>
    /// <remarks>
    /// For the files that were present before watching began: they belong to an earlier
    /// session and are finished by definition.
    /// </remarks>
    public void Release(string path) => _released.Add(path);

    /// <summary>Whether this file has already been handed on.</summary>
    public bool WasReleased(string path) => _released.Contains(path);

    /// <summary>
    /// Offers a released file again, for a read that failed.
    /// </summary>
    /// <remarks>
    /// A decode can still come back incomplete after the length settled — the file is closed
    /// but the picture is not what it will be. Rather than lose the frame, the caller can put
    /// it back and let it settle a second time.
    /// </remarks>
    public void Retry(string path)
    {
        _released.Remove(path);
        _growing.Remove(path);
    }
}
