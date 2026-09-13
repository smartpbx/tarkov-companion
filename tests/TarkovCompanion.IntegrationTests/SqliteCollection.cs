namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// Every test that opens a SQLite database, kept out of each other's way.
/// </summary>
/// <remarks>
/// These tear down by calling <c>SqliteConnection.ClearAllPools()</c>, which is the documented
/// way to make SQLite let go of a file so a temporary directory can be deleted. It is also
/// process-wide: it closes pooled connections belonging to every other test running at that
/// moment, not just the one tearing down.
///
/// xUnit runs test classes in parallel, so one class finishing would reach into another that
/// was mid-query and close the handle underneath it. The result was
/// <c>ObjectDisposedException: SQLitePCL.sqlite3</c> thrown from inside a reader, on a
/// different test, at random, on whichever build happened to lose the race. It blocked two
/// unrelated pull requests before anybody looked at it properly.
///
/// The alternative fix is to stop clearing pools and disable pooling in the test connection
/// string instead. That is arguably cleaner and it changes how every one of these tests talks
/// to the database, which is a larger thing to be confident about than simply not running them
/// at the same time. These are file-backed integration tests; they were never fast, and serial
/// is what they already assumed they were.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqliteCollection
{
    public const string Name = "sqlite";
}
