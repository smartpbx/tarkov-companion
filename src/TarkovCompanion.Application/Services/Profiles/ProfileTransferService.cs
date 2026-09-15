using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Application.Services.Profiles;

/// <summary>
/// One bounded, validated copy of a transfer. The caller's list is counted and indexed exactly
/// once, so a codec that keeps a reference to the list it passed in, or a list that answers
/// differently each time it is read, cannot change what is hashed, reviewed, or imported.
/// </summary>
public sealed record ProfileTransferDocument
{
    public ProfileTransferDocument(int formatVersion, DateTimeOffset exportedUtc, IReadOnlyList<ProfileRecord> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        FormatVersion = formatVersion;
        ExportedUtc = exportedUtc.ToUniversalTime();
        Profiles = new ProfileWorkspaceSnapshot(0, null, profiles).Profiles;
    }

    public int FormatVersion { get; }
    public DateTimeOffset ExportedUtc { get; }
    public IReadOnlyList<ProfileRecord> Profiles { get; }
}

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
    QuarantinedIncompatibleContext,
}

public sealed record ProfileImportEntry(
    ProfileRecord Profile,
    ProfileImportDisposition Disposition,
    string Message,
    ProfileContextCompatibility? Compatibility = null);

public sealed record ProfileImportPreview
{
    public ProfileImportPreview(Guid confirmationId, DateTimeOffset expiresUtc, IReadOnlyList<ProfileImportEntry> entries)
    {
        if (confirmationId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(confirmationId));
        ArgumentNullException.ThrowIfNull(entries);
        var copied = entries.ToArray();
        if (copied.Length > ProfileWorkspaceSnapshot.MaximumProfiles)
            throw new ArgumentOutOfRangeException(nameof(entries), $"A preview reviews at most {ProfileWorkspaceSnapshot.MaximumProfiles} profiles.");
        if (copied.Any(entry => entry is null))
            throw new ArgumentException("Preview entries cannot contain null.", nameof(entries));

        ConfirmationId = confirmationId;
        ExpiresUtc = expiresUtc.ToUniversalTime();
        Entries = Array.AsReadOnly(copied);
        ReadyEntries = Array.AsReadOnly(copied.Where(entry => entry.Disposition == ProfileImportDisposition.Ready).ToArray());
    }

    public Guid ConfirmationId { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public IReadOnlyList<ProfileImportEntry> Entries { get; }
    public IReadOnlyList<ProfileImportEntry> ReadyEntries { get; }
}

/// <summary>
/// Import parsing never mutates profile state. The short-lived confirmation token binds the exact
/// reviewed document to Apply, making a UI refresh or a second file selection unable to apply a
/// different profile by accident.
///
/// Pending previews used to live in an unbounded dictionary that only a confirmation ever
/// emptied, so every preview a user looked at and walked away from stayed for the life of the
/// process. They are now capped, a preview is dead from the instant it expires, and expired or
/// abandoned previews give their slot back under the same lock that hands slots out.
/// </summary>
public sealed class ProfileTransferService
{
    public const int MaximumPendingPreviews = 64;

    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(10);

    private readonly ProfileContextService _contexts;
    private readonly IProfileTransferCodec _codec;
    private readonly Dictionary<Guid, ProfileImportPreview> _previews = new();
    private readonly Lock _previewGate = new();
    private readonly TimeProvider _timeProvider;

    public ProfileTransferService(
        ProfileContextService contexts,
        IProfileTransferCodec codec,
        TimeProvider? timeProvider = null)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> ExportAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _contexts.GetAsync(cancellationToken).ConfigureAwait(false);
        var document = new ProfileTransferDocument(1, UtcNow(), snapshot.Profiles);
        return _codec.Write(document)
            ?? throw new InvalidDataException("The profile transfer codec returned no export document.");
    }

    public async Task<ProfileImportPreview> PreviewAsync(string document, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        cancellationToken.ThrowIfCancellationRequested();
        var decoded = _codec.Read(document)
            ?? throw new InvalidDataException("The profile transfer codec returned no import document.");
        var incoming = FreezeIncoming(decoded);

        // Nothing below observes the codec-owned document. The validated copy is complete before
        // the workspace read, which is the first point at which another operation can interleave.
        var current = await _contexts.GetAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var existing = current.Profiles.ToDictionary(profile => profile.Context.Identity.ProfileId);
        var entries = incoming.Profiles.Select(profile => Classify(profile, existing)).ToArray();
        var now = UtcNow();

        lock (_previewGate)
        {
            EvictExpiredLocked(now);
            if (_previews.Count >= MaximumPendingPreviews)
            {
                throw new InvalidOperationException(
                    $"At most {MaximumPendingPreviews} import previews can await confirmation; confirm or abandon one first.");
            }

            ProfileImportPreview preview;
            do
            {
                preview = new ProfileImportPreview(Guid.NewGuid(), now.Add(PreviewLifetime), entries);
            }
            while (!_previews.TryAdd(preview.ConfirmationId, preview));

            return preview;
        }
    }

    /// <summary>Releases a reviewed preview the user decided not to apply.</summary>
    /// <returns><see langword="false"/> when it was already confirmed, abandoned, or expired.</returns>
    public bool Abandon(Guid confirmationId)
    {
        var now = UtcNow();
        lock (_previewGate)
        {
            EvictExpiredLocked(now);
            return _previews.Remove(confirmationId);
        }
    }

    public async Task<ProfileWorkspaceSnapshot> ConfirmAsync(Guid confirmationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = UtcNow();
        ProfileImportPreview? preview = null;
        lock (_previewGate)
        {
            // Removed before anything else is checked: a token is single-use even when the
            // confirmation then fails, so two racing confirmations cannot both apply it.
            if (_previews.Remove(confirmationId, out var pending))
            {
                preview = pending;
            }

            EvictExpiredLocked(now);
        }

        if (preview is null)
        {
            throw new InvalidOperationException("Import preview was not found, expired, was abandoned, or was already confirmed.");
        }

        if (now >= preview.ExpiresUtc)
        {
            throw new InvalidOperationException("Import preview expired; review the document again.");
        }

        return await _contexts.ImportAsync(
            preview.ReadyEntries.Select(entry => entry.Profile).ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    private static ProfileTransferDocument FreezeIncoming(ProfileTransferDocument incoming)
    {
        if (incoming.FormatVersion != 1)
        {
            throw new InvalidDataException("Profile import does not satisfy the bounded profile-context contract.");
        }

        try
        {
            return new ProfileTransferDocument(incoming.FormatVersion, incoming.ExportedUtc, incoming.Profiles);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Profile import does not satisfy the bounded profile-context contract.", exception);
        }
    }

    /// <summary>
    /// A matching stable id is only "already present" when every context field matches. The same
    /// id and generation recorded under another mode, wipe, locale, or data snapshot is a different
    /// context, and treating it as present would hide exactly the contamination this guards.
    /// </summary>
    private static ProfileImportEntry Classify(ProfileRecord profile, IReadOnlyDictionary<Guid, ProfileRecord> existing)
    {
        if (!existing.TryGetValue(profile.Context.Identity.ProfileId, out var local))
        {
            return new(profile, ProfileImportDisposition.Ready, "New profile context; no local state will be merged.");
        }

        var compatibility = ProfileContextCompatibility.Compare(local.Context, profile.Context);
        if (compatibility.IsCompatible)
        {
            return new(
                profile,
                ProfileImportDisposition.AlreadyPresent,
                "The exact profile identity and context are already local; local progress is kept.",
                compatibility);
        }

        var mismatchNames = string.Join(", ", compatibility.Mismatches);
        var disposition = compatibility.Mismatches.Contains(ProfileContextMismatch.Generation)
            ? ProfileImportDisposition.QuarantinedWrongGeneration
            : ProfileImportDisposition.QuarantinedIncompatibleContext;
        return new(
            profile,
            disposition,
            $"Stable profile id matches but the context differs ({mismatchNames}); import is quarantined.",
            compatibility);
    }

    private void EvictExpiredLocked(DateTimeOffset now)
    {
        foreach (var (confirmationId, preview) in _previews.ToArray())
        {
            if (now >= preview.ExpiresUtc)
            {
                _previews.Remove(confirmationId);
            }
        }
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();
}
