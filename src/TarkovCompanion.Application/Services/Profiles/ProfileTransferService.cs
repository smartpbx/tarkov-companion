using System.Collections.Concurrent;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Application.Services.Profiles;

public sealed record ProfileTransferDocument(int FormatVersion, DateTimeOffset ExportedUtc, IReadOnlyList<ProfileRecord> Profiles);

public interface IProfileTransferCodec
{
    string Write(ProfileTransferDocument document);

    ProfileTransferDocument Read(string document);
}

public enum ProfileImportDisposition
{
    Ready,
    AlreadyPresent,
    QuarantinedWrongGeneration,
}

public sealed record ProfileImportEntry(ProfileRecord Profile, ProfileImportDisposition Disposition, string Message);

public sealed record ProfileImportPreview(Guid ConfirmationId, DateTimeOffset ExpiresUtc, IReadOnlyList<ProfileImportEntry> Entries)
{
    public IReadOnlyList<ProfileImportEntry> ReadyEntries =>
        Entries.Where(entry => entry.Disposition == ProfileImportDisposition.Ready).ToArray();
}

/// <summary>
/// Import parsing never mutates profile state. The short-lived confirmation token binds the exact
/// reviewed document to Apply, making a UI refresh or a second file selection unable to apply a
/// different profile by accident.
/// </summary>
public sealed class ProfileTransferService(
    ProfileContextService contexts,
    IProfileTransferCodec codec,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, ProfileImportPreview> _previews = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<string> ExportAsync(CancellationToken cancellationToken)
    {
        var snapshot = await contexts.GetAsync(cancellationToken).ConfigureAwait(false);
        return codec.Write(new ProfileTransferDocument(1, UtcNow(), snapshot.Profiles));
    }

    public async Task<ProfileImportPreview> PreviewAsync(string document, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        cancellationToken.ThrowIfCancellationRequested();
        var incoming = codec.Read(document);
        var current = await contexts.GetAsync(cancellationToken).ConfigureAwait(false);
        var existing = current.Profiles.ToDictionary(profile => profile.Context.Identity.ProfileId);
        var entries = incoming.Profiles.Select(profile => Classify(profile, existing)).ToArray();
        var preview = new ProfileImportPreview(Guid.NewGuid(), UtcNow().Add(PreviewLifetime), Array.AsReadOnly(entries));
        if (!_previews.TryAdd(preview.ConfirmationId, preview))
        {
            throw new InvalidOperationException("Could not create a unique import confirmation.");
        }

        return preview;
    }

    public async Task<ProfileWorkspaceSnapshot> ConfirmAsync(Guid confirmationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_previews.TryRemove(confirmationId, out var preview))
        {
            throw new InvalidOperationException("Import preview was not found or was already confirmed.");
        }

        if (preview.ExpiresUtc < UtcNow())
        {
            throw new InvalidOperationException("Import preview expired; review the document again.");
        }

        return await contexts.ImportAsync(preview.ReadyEntries.Select(entry => entry.Profile).ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static ProfileImportEntry Classify(ProfileRecord profile, IReadOnlyDictionary<Guid, ProfileRecord> existing)
    {
        if (!existing.TryGetValue(profile.Context.Identity.ProfileId, out var local))
        {
            return new(profile, ProfileImportDisposition.Ready, "New profile context; no local state will be merged.");
        }

        return string.Equals(local.Context.Identity.Generation, profile.Context.Identity.Generation, StringComparison.Ordinal)
            ? new(profile, ProfileImportDisposition.AlreadyPresent, "The exact profile identity and generation are already local.")
            : new(profile, ProfileImportDisposition.QuarantinedWrongGeneration, "Stable profile id matches but generation differs; import is quarantined.");
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();
}
