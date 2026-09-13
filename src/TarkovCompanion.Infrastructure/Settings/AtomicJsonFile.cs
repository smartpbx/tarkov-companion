using System.Text;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>
/// Writes a settings file in a way that a kill cannot half-finish.
/// </summary>
/// <remarks>
/// <see cref="File.WriteAllTextAsync(string, string?, CancellationToken)"/> truncates the file
/// before it writes, so a process killed inside that window leaves a valid path holding
/// nothing. Three settings files were written that way, and the group one is the reason it
/// mattered: its reader maps the resulting <see cref="System.Text.Json.JsonException"/> to
/// "off", so the player was silently not sharing, with the fields blank and nothing on screen
/// saying a file had been lost.
///
/// Nine other places in this assembly already did temp-then-move by hand. This is that, once,
/// so the next person writing a settings file does not have to remember it.
/// </remarks>
public static class AtomicJsonFile
{
    /// <summary>Writes <paramref name="contents"/> to <paramref name="path"/>, or not at all.</summary>
    public static async Task WriteAsync(string path, string contents, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException($"The settings path has no parent directory: {path}"));

        var temporary = full + ".writing";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(contents), cancellationToken).ConfigureAwait(false);
                // The move is only atomic with respect to the directory entry; without this the
                // bytes may still be in the operating system's cache when the power goes.
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, full, overwrite: true);
        }
        catch
        {
            // A failed write must not leave its scratch file behind to be found later and
            // wondered about.
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Moves a file that could not be parsed aside, so the next write starts clean.
    /// </summary>
    /// <remarks>
    /// Returns where it went, or null if there was nothing to move. Renaming rather than
    /// deleting because an unreadable settings file is the only remaining record of what
    /// somebody had configured, and "your settings were reset" is a great deal easier to
    /// believe when the old file is still sitting there.
    /// </remarks>
    public static string? SetAside(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var aside = $"{path}.corrupt-{now.UtcDateTime:yyyyMMddHHmmss}";
            File.Move(path, aside, overwrite: true);
            return aside;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
