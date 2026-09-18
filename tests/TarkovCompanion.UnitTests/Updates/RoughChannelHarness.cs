using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Velopack.Locators;

namespace TarkovCompanion.UnitTests.Updates;

/// <summary>
/// An installed application and a published feed, both in a temporary folder.
/// </summary>
/// <remarks>
/// The updater library runs for real here: its own check, its own download and staging, and its
/// own hand-off to the program that swaps the files. Only two things are stood in for. Where the
/// application is installed comes from the library's test locator instead of the running
/// process, and starting that program and exiting are recorded instead of done, because a test
/// run that really exited would take the test host with it. There is no network anywhere.
/// </remarks>
internal sealed class RoughChannelHarness : IDisposable
{
    public const string PackId = "TarkovCompanionDesktop";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tc-updates-" + Guid.NewGuid().ToString("N"));

    public RoughChannelHarness(string installedVersion)
    {
        Feed = Directory.CreateDirectory(Path.Combine(_root, "feed")).FullName;
        Packages = Directory.CreateDirectory(Path.Combine(_root, "install", "packages")).FullName;
        var current = Directory.CreateDirectory(Path.Combine(_root, "install", "current")).FullName;
        var updateExe = Path.Combine(_root, "install", "Update.exe");
        File.WriteAllText(updateExe, "stands in for the program that swaps the files");
        Locator = new InstalledLocator(installedVersion, Packages, current, Path.Combine(_root, "install"), updateExe);
    }

    public string Feed { get; }

    public string Packages { get; }

    public InstalledLocator Locator { get; }

    public ListLogger Log { get; } = new();

    /// <summary>Publishes one full package the way the packaging tool lays it out.</summary>
    /// <returns>The package's file name and the SHA256 the feed lists for it.</returns>
    public (string FileName, string Sha256) Publish(string version, string? listedSha256 = null)
    {
        var fileName = $"{PackId}-{version}-full.nupkg";
        var bytes = RandomNumberGenerator.GetBytes(64 * 1024);
        File.WriteAllBytes(Path.Combine(Feed, fileName), bytes);
        var sha256 = listedSha256 ?? Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllText(
            Path.Combine(Feed, "releases.win.json"),
            JsonSerializer.Serialize(new
            {
                Assets = new[]
                {
                    new
                    {
                        PackageId = PackId,
                        Version = version,
                        Type = "Full",
                        FileName = fileName,
                        SHA1 = Convert.ToHexString(SHA1.HashData(bytes)),
                        SHA256 = sha256,
                        Size = bytes.Length,
                    },
                },
            }));
        return (fileName, sha256);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary folder that outlives one test is not a test failure.
        }
    }

    internal sealed class InstalledLocator(string version, string packages, string current, string root, string updateExe)
        : TestVelopackLocator(PackId, version, packages, current, root, updateExe, channel: "win")
    {
        public RecordedProcess Recorded { get; } = new();

        public override IProcessImpl Process => Recorded;
    }

    internal sealed class RecordedProcess : IProcessImpl
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Started { get; } = [];

        public int? ExitCode { get; private set; }

        public string GetCurrentProcessPath() => "TarkovCompanion.exe";

        public uint GetCurrentProcessId() => 4242;

        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow) =>
            Started.Add((exePath, args.ToArray()));

        public void Exit(int exitCode) => ExitCode = exitCode;
    }
}

/// <summary>A logger that keeps the sentences, because these tests assert on what was said.</summary>
internal sealed class ListLogger : ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception)));
}
