using System.Text.Json;

namespace TarkovCompanion.Application.Services.ReleaseExperience;

/// <summary>One shipped build and the short, player-visible changes it carries.</summary>
public sealed record PlayerChangelogRelease(string Version, IReadOnlyList<string> Changes);

/// <summary>The bounded changelog compiled into the desktop application.</summary>
public sealed class PlayerChangelog
{
    public const int MaximumReleases = 20;
    public const int MaximumChangesPerRelease = 8;
    public const int MaximumChangeLength = 120;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, PlayerChangelogRelease> _byVersion;

    private PlayerChangelog(IReadOnlyList<PlayerChangelogRelease> releases)
    {
        Releases = releases;
        _byVersion = releases.ToDictionary(release => release.Version, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<PlayerChangelogRelease> Releases { get; }

    public PlayerChangelogRelease? Find(string version) =>
        string.IsNullOrWhiteSpace(version) ? null : _byVersion.GetValueOrDefault(version.Trim());

    public static PlayerChangelog Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ChangelogDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ChangelogDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("The player changelog is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The player changelog is not valid JSON.", exception);
        }

        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException($"The player changelog schema {document.SchemaVersion} is not supported.");
        }

        if (document.Releases is null || document.Releases.Count == 0 || document.Releases.Count > MaximumReleases)
        {
            throw new InvalidDataException($"The player changelog must contain 1-{MaximumReleases} releases.");
        }

        var releases = new List<PlayerChangelogRelease>(document.Releases.Count);
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in document.Releases)
        {
            var version = RequiredText(source.Version, "release version", 40);
            if (!versions.Add(version))
            {
                throw new InvalidDataException($"The player changelog repeats version '{version}'.");
            }

            if (source.Changes is null || source.Changes.Count == 0 || source.Changes.Count > MaximumChangesPerRelease)
            {
                throw new InvalidDataException(
                    $"Release '{version}' must contain 1-{MaximumChangesPerRelease} changes.");
            }

            releases.Add(new PlayerChangelogRelease(
                version,
                source.Changes.Select(change => RequiredText(change, "change", MaximumChangeLength)).ToArray()));
        }

        return new PlayerChangelog(releases);
    }

    private static string RequiredText(string? value, string label, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"A player changelog {label} is missing.");
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"A player changelog {label} must be at most {maximumLength} characters and contain no control characters.");
        }

        return normalized;
    }

    private sealed record ChangelogDocument(int SchemaVersion, IReadOnlyList<ReleaseDocument>? Releases);

    private sealed record ReleaseDocument(string? Version, IReadOnlyList<string?>? Changes);
}
