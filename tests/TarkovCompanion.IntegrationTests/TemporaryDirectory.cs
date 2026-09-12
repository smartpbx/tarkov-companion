using Microsoft.Data.Sqlite;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// Removes a temporary directory without letting the attempt fail a test.
/// </summary>
/// <remarks>
/// A composition test failed in CI with "Directory not empty" while deleting its own scratch
/// directory, and passed on the same commit in the other workflow. SQLite closes its write-ahead
/// files a moment after the pool is cleared, and on Linux a directory whose last file is still
/// being released reports itself as non-empty.
///
/// Teardown of a temporary directory is not a thing worth failing a test over. Failing there
/// reports a defect that does not exist and hides whatever the test was actually checking, which
/// is worse than leaving a directory behind for the operating system to sweep up. So this
/// retries briefly and then gives up quietly.
///
/// Clearing the connection pool is part of the job rather than the caller's business. A pooled
/// connection holds the database file open indefinitely, so on Windows no amount of retrying
/// gets past it; four tests failed that way while every one of their assertions passed. Putting
/// it here means the next test to need a scratch directory cannot forget.
/// </remarks>
internal static class TemporaryDirectory
{
    private const int Attempts = 5;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(120);

    public static void Remove(string path)
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == Attempts)
                {
                    return;
                }

                Thread.Sleep(RetryDelay);
            }
        }
    }
}
