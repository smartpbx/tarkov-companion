using System.Text.Json;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;

namespace TarkovCompanion.Infrastructure.Events;

/// <summary>
/// Loads hand-authored seasonal event definitions from the <c>*.json</c> files in one directory.
/// </summary>
/// <remarks>
/// <para>
/// json.tarkov.dev exposes no events endpoint, so there is nothing upstream to fetch: a person
/// writes each file by hand (see <c>assets/events/README.md</c>). A missing or empty directory is
/// the ordinary out-of-season state and yields an empty list; it is never an error.
/// </para>
/// <para>
/// One bad file must not hide the others, so a file that is malformed, over the size limit or
/// missing a required field is skipped and the rest still load. This type has no logger, so a
/// skipped file is silent; the README tells authors what makes a file load.
/// </para>
/// <para>
/// The first successful read is cached for the life of the instance. The interface has no
/// invalidation, and these files change only when a person edits them, so a restart is what
/// picks up new or changed definitions.
/// </para>
/// </remarks>
public sealed class JsonFileEventCatalog(string definitionsDirectory) : IEventCatalog
{
    /// <summary>Provenance source recorded on every definition this catalog returns.</summary>
    /// <remarks>
    /// The file's own <c>provenance.source</c> is deliberately ignored. Whatever an author types
    /// there, the data was hand-authored, and a definition claiming to be "json.tarkov.dev" would
    /// misrepresent where it came from.
    /// </remarks>
    public const string Source = "local event definition";

    /// <summary>Ceiling on the confidence a definition may carry.</summary>
    /// <remarks>
    /// A person typed these files from patch notes, a wiki page or their own raids; nothing
    /// upstream has confirmed them, so they must not read as synced facts. The shipped example
    /// declares 1.0, which is exactly the claim this ceiling refuses. 0.60 is the scale the rest
    /// of the app already uses for values it could not verify: the recommendation engine caps at
    /// 0.60 when it has no price to go on, and ammo heuristics cap at 0.80 even on upstream data.
    /// An author may declare a lower value for a list they are unsure of, never a higher one.
    /// </remarks>
    public const double MaximumConfidence = 0.60;

    /// <summary>
    /// Generous for a list of item ids (a thousand ids is roughly 40 KB) while keeping a stray
    /// large file out of memory. The size is checked before anything is parsed.
    /// </summary>
    private const int MaximumDefinitionBytes = 256 * 1024;

    // Comments and trailing commas are tolerated because these files are written by hand.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _directory = Path.GetFullPath(
        string.IsNullOrWhiteSpace(definitionsDirectory)
            ? throw new ArgumentException("An event definitions directory is required.", nameof(definitionsDirectory))
            : definitionsDirectory);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<EventDefinition>? _cached;

    /// <summary>Returns every definition that loads, in file-name order.</summary>
    /// <remarks>
    /// Definitions with <c>active</c> false or with no applicable items are returned too. Whether
    /// an event is current is the caller's to present; filtering here would make "no events are
    /// configured" and "an event is configured but switched off" indistinguishable.
    /// </remarks>
    public async Task<IReadOnlyList<EventDefinition>> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } cached)
            {
                return cached;
            }

            var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (loaded is not null)
            {
                _cached = loaded;
            }

            return loaded ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads the directory once. Returns <see langword="null"/> when the directory exists but
    /// could not be listed, so that failure is not cached and a later call tries again.
    /// </summary>
    private async Task<IReadOnlyList<EventDefinition>?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        string[] paths;
        try
        {
            // Direct children only: a sub-folder is the natural place for drafts and notes that
            // are not meant to load.
            paths = Directory.GetFiles(_directory, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // File systems differ in enumeration order; sorting keeps the result, and which file
        // wins a duplicate id, the same on every machine.
        Array.Sort(paths, StringComparer.Ordinal);

        var definitions = new List<EventDefinition>(paths.Length);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var definition = await ReadDefinitionOrNullAsync(path, cancellationToken).ConfigureAwait(false);

            // ProfileEventTrackerService keys definitions by id and would throw on a duplicate,
            // so a second file with the same id is dropped here; the first in name order wins.
            if (definition is not null && seenIds.Add(definition.Id))
            {
                definitions.Add(definition);
            }
        }

        return definitions.AsReadOnly();
    }

    private static async Task<EventDefinition?> ReadDefinitionOrNullAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var lastWriteUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumDefinitionBytes)
            {
                return null;
            }

            var document = await JsonSerializer
                .DeserializeAsync<DefinitionDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return document is null ? null : ToDefinition(document, path, lastWriteUtc);
        }
        catch (Exception exception) when (exception is JsonException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Shapes a parsed document into the domain record, or returns <see langword="null"/> when a
    /// required field is missing or the document contradicts itself.
    /// </summary>
    private static EventDefinition? ToDefinition(DefinitionDocument document, string path, DateTimeOffset lastWriteUtc)
    {
        var id = document.Id?.Trim();
        var name = document.Name?.Trim();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || document.Active is not { } active)
        {
            return null;
        }

        // A window that ends before it starts is a typo, not a preference.
        if (document.StartUtc > document.EndUtc)
        {
            return null;
        }

        var rulesJson = document.RulesJson;
        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            rulesJson = "{}";
        }

        // The rules travel as JSON text for a later consumer to parse; rejecting bad text here
        // keeps that consumer from being the one to discover the typo.
        if (!IsJsonText(rulesJson))
        {
            return null;
        }

        // Ordinal, to match how the tracker and the profile compare item ids.
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var itemId in document.ApplicableItemIds ?? [])
        {
            if (!string.IsNullOrWhiteSpace(itemId))
            {
                itemIds.Add(itemId.Trim());
            }
        }

        return new EventDefinition(
            id,
            name,
            document.StartUtc,
            document.EndUtc,
            active,
            itemIds,
            rulesJson,
            ToProvenance(document.Provenance, path, lastWriteUtc));
    }

    /// <summary>
    /// Builds provenance the loader can stand behind. The author's dates and reference are kept
    /// because they say where the list came from; the source and the confidence ceiling are the
    /// loader's, because they say what kind of data this is.
    /// </summary>
    private static DataProvenance ToProvenance(ProvenanceDocument? document, string path, DateTimeOffset lastWriteUtc)
    {
        // Without a citation, the file itself is the only thing a reader can go back to.
        var reference = document?.Reference;
        if (string.IsNullOrWhiteSpace(reference))
        {
            reference = path;
        }

        // The moment the file was last saved is the best available proxy for when its author
        // last looked at the event.
        var observedUtc = document?.ObservedUtc ?? lastWriteUtc;

        // NaN and out-of-range values fail the comparison and fall back to the ceiling rather
        // than reaching the Confidence constructor, which would throw.
        var declared = document?.Confidence?.Value;
        var confidence = declared is { } value && value >= 0 && value <= 1
            ? new Confidence(Math.Min(value, MaximumConfidence))
            : new Confidence(MaximumConfidence);

        return new DataProvenance(Source, observedUtc, document?.SourceUpdatedUtc, reference.Trim(), confidence);
    }

    private static bool IsJsonText(string text)
    {
        try
        {
            JsonDocument.Parse(text).Dispose();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The on-disk shape. Every field is optional here so that a missing one is a skip decided
    /// by <see cref="ToDefinition"/>, not a deserialization failure.
    /// </summary>
    private sealed record DefinitionDocument(
        string? Id,
        string? Name,
        DateTimeOffset? StartUtc,
        DateTimeOffset? EndUtc,
        bool? Active,
        string?[]? ApplicableItemIds,
        string? RulesJson,
        ProvenanceDocument? Provenance);

    /// <summary>
    /// Mirrors the file's provenance block without its <c>source</c>, which the loader does not
    /// honour. <see cref="Confidence"/> is read into a plain document rather than the domain
    /// struct because that struct has no JSON constructor and would silently deserialize as zero.
    /// </summary>
    private sealed record ProvenanceDocument(
        DateTimeOffset? ObservedUtc,
        DateTimeOffset? SourceUpdatedUtc,
        string? Reference,
        ConfidenceDocument? Confidence);

    private sealed record ConfidenceDocument(double? Value);
}
