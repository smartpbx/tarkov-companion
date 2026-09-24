using TarkovCompanion.App.Localization;

namespace TarkovCompanion.App.Services.V2.SelfTest;

/// <summary>What one folder holds, without reading a single file in it.</summary>
public sealed record SelfTestFolderState(bool Exists, int Entries, DateTimeOffset? NewestWriteUtc, string? Problem);

/// <summary>
/// Looks at a folder and says when it last changed.
/// </summary>
/// <remarks>
/// The one question Setup could never answer. Discovery reports which folder it picked and
/// stops there, so a log folder the game abandoned looked exactly like the one it is writing
/// to — #414, where every raid for an evening went unrecorded and nothing on screen was wrong.
/// "Last changed" is the difference, and it costs one directory listing.
///
/// Bounded on purpose: a screenshot folder with ten thousand files in it is ordinary, and this
/// is pressed mid-raid. It stops counting at <see cref="MaximumEntries"/> and reports the
/// newest write it saw within that bound, which is the newest overall whenever the folder is
/// enumerated newest-last — and close enough to it either way to answer the question.
/// </remarks>
public sealed class SelfTestFolderReader
{
    public const int MaximumEntries = 20_000;

    public SelfTestFolderState Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new(false, 0, null, SetupText.ProbeFolderNoneChosen);
        }

        try
        {
            if (!Directory.Exists(path))
            {
                return new(false, 0, null, SetupText.ProbeFolderMissing);
            }

            var entries = 0;
            DateTimeOffset? newest = null;
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly))
            {
                if (++entries > MaximumEntries)
                {
                    break;
                }

                var written = Written(entry);
                if (written is { } at && (newest is null || at > newest))
                {
                    newest = at;
                }
            }

            return new(true, entries, newest, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(false, 0, null, SetupText.ProbeFolderUnreadable(exception.Message));
        }
    }

    /// <summary>
    /// When a folder last changed, taking a session folder's own contents into account.
    /// </summary>
    /// <remarks>
    /// A log root's direct children are per-launch folders, and creating one does not always
    /// restamp the parent. Asking the newest child when the entry is a directory is what makes
    /// "the logs have stood still" mean the logs rather than the folder that holds them.
    /// </remarks>
    private static DateTimeOffset? Written(string entry)
    {
        try
        {
            if (!Directory.Exists(entry))
            {
                return new DateTimeOffset(File.GetLastWriteTimeUtc(entry), TimeSpan.Zero);
            }

            var folder = new DateTimeOffset(Directory.GetLastWriteTimeUtc(entry), TimeSpan.Zero);
            var newest = folder;
            var seen = 0;
            foreach (var child in Directory.EnumerateFiles(entry, "*", SearchOption.TopDirectoryOnly))
            {
                if (++seen > 64)
                {
                    break;
                }

                var written = new DateTimeOffset(File.GetLastWriteTimeUtc(child), TimeSpan.Zero);
                if (written > newest)
                {
                    newest = written;
                }
            }

            return newest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
