using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.App.Services.TestChecklist;

/// <summary>What a tester said about one item.</summary>
public enum TestStatus
{
    Untested,
    Works,
    Broken,
    NeedsWork,
    Skipped,
}

/// <summary>One item's recorded result: its status, the note, and the build and time it was recorded on.</summary>
/// <param name="TestedUtc">UTC, as everything persisted is; shown through LocalTime.</param>
public sealed record TestChecklistResult(TestStatus Status, string? Note, string? Build, DateTimeOffset? TestedUtc)
{
    /// <summary>
    /// Recorded on another build: the status is kept, and the page says to try it again. An item
    /// never tested has nothing to retest.
    /// </summary>
    public bool NeedsRetest(string runningBuild) =>
        Status != TestStatus.Untested && !string.Equals(Build, runningBuild, StringComparison.Ordinal);
}

/// <summary>
/// Keeps the checklist's results in <c>Config/test-checklist-results.json</c>, one record per item id.
/// </summary>
/// <remarks>
/// Test results are the tester's data, not a choice of behaviour, so SettingsRegistry names this as
/// not a setting: Export, Import and Reset everything leave it alone, and Clear results is its reset.
/// Written whole through <see cref="AtomicJsonFile"/> on every change; a file that cannot be read is
/// set aside rather than overwritten, so a bad edit by hand does not silently lose the results.
/// </remarks>
public sealed class TestChecklistResultsStore
{
    public const string FileName = "test-checklist-results.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TestChecklistResult> _results;

    public TestChecklistResultsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _results = Read(path);
    }

    public static TestChecklistResultsStore In(AppDataPaths paths) =>
        new(Path.Combine((paths ?? throw new ArgumentNullException(nameof(paths))).Config, FileName));

    public string FilePath => _path;

    public TestChecklistResult? Get(string id)
    {
        lock (_gate)
        {
            return _results.GetValueOrDefault(id);
        }
    }

    public IReadOnlyDictionary<string, TestChecklistResult> All
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, TestChecklistResult>(_results, StringComparer.Ordinal);
            }
        }
    }

    public void Set(string id, TestChecklistResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            _results[id] = result;
            Write();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _results.Clear();
            Write();
        }
    }

    private void Write()
    {
        var file = new ResultsFile(
            1,
            _results.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(
                pair => pair.Key,
                pair => (ResultRecord?)new ResultRecord(StatusName(pair.Value.Status), pair.Value.Note, pair.Value.Build, pair.Value.TestedUtc?.ToUniversalTime()),
                StringComparer.Ordinal));
        AtomicJsonFile.Write(_path, JsonSerializer.Serialize(file, Json));
    }

    private static Dictionary<string, TestChecklistResult> Read(string path)
    {
        var results = new Dictionary<string, TestChecklistResult>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return results;
        }

        try
        {
            var file = JsonSerializer.Deserialize<ResultsFile>(File.ReadAllText(path), Json);
            foreach (var (id, record) in file?.Results ?? [])
            {
                if (!string.IsNullOrWhiteSpace(id) && record is not null && TryParseStatus(record.Status, out var status))
                {
                    results[id] = new(status, record.Note, record.Build, record.TestedUtc);
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AtomicJsonFile.SetAside(path, DateTimeOffset.UtcNow);
        }

        return results;
    }

    internal static string StatusName(TestStatus status) => status switch
    {
        TestStatus.Works => "works",
        TestStatus.Broken => "broken",
        TestStatus.NeedsWork => "needs-work",
        TestStatus.Skipped => "skipped",
        _ => "untested",
    };

    private static bool TryParseStatus(string? name, out TestStatus status)
    {
        foreach (var candidate in Enum.GetValues<TestStatus>())
        {
            if (string.Equals(StatusName(candidate), name, StringComparison.Ordinal))
            {
                status = candidate;
                return true;
            }
        }

        status = TestStatus.Untested;
        return false;
    }

    private sealed record ResultsFile(int Version, Dictionary<string, ResultRecord?>? Results);

    private sealed record ResultRecord(string Status, string? Note, string? Build, DateTimeOffset? TestedUtc);
}
