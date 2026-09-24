using System.Text.Json;
using TarkovCompanion.Application.Services.Feedback;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>The problem-report outbox, kept in <c>Config/problem-report-outbox.json</c> (#314).</summary>
/// <remarks>
/// It holds only reports the player read and pressed Send on, plus the hashes of ones already sent.
/// An unreadable or oversized file reads as an empty outbox: a lost queued report costs a retry, and
/// never blocks the next report.
/// </remarks>
public sealed class JsonFileProblemReportOutboxStore(string storePath) : IProblemReportOutboxStore
{
    private const long MaximumFileBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<ProblemReportOutboxState> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(storePath);
            if (!info.Exists || info.Length > MaximumFileBytes)
            {
                return ProblemReportOutboxState.Empty;
            }

            var text = await File.ReadAllTextAsync(storePath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<ProblemReportOutboxState>(text, JsonOptions);
            return document is null
                ? ProblemReportOutboxState.Empty
                : new(
                    [.. (document.Queued ?? []).Where(queued => !string.IsNullOrEmpty(queued.Key) && !string.IsNullOrEmpty(queued.Text))],
                    [.. (document.Sent ?? []).Where(sent => !string.IsNullOrEmpty(sent.Key))]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProblemReportOutboxState.Empty;
        }
    }

    public Task SaveAsync(ProblemReportOutboxState state, CancellationToken cancellationToken) =>
        AtomicJsonFile.WriteAsync(storePath, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
}
