using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.UnitTests.Profiles;

internal static class ProfileV2Fixtures
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T00:00:00Z");

    public static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    public static ProfileRecord Profile(string id, string generation, ProfileGameMode mode, string wishlist, string? wipe = null) =>
        Profile(Context(Guid.Parse(id), generation, mode, wipe), wishlist);

    public static ProfileRecord Profile(int id, string generation, ProfileGameMode mode, string wishlist, string? wipe = null) =>
        Profile(Context(Id(id), generation, mode, wipe), wishlist);

    public static ProfileRecord Profile(ProfileContext context, string wishlist) => new(
        context,
        $"profile-{context.Identity.Generation}",
        new ProfileProgress(20, wishlistItemIds: new HashSet<string> { wishlist }),
        ProfileLifecycle.Active,
        DateTimeOffset.UnixEpoch);

    public static ProfileContext Context(
        Guid id,
        string generation,
        ProfileGameMode mode,
        string? wipe = null,
        string language = "en-US",
        string region = "EU",
        string timeZone = "Etc/UTC",
        string snapshot = "snapshot-a",
        DateTimeOffset? published = null) => new(
        new ProfileIdentity(id, generation),
        mode,
        new WipeSeason(wipe ?? $"wipe-{mode}"),
        new ProfileLocale(language, region, timeZone),
        new DataSnapshotContext(snapshot, published ?? DateTimeOffset.UnixEpoch));

    public static CreateProfileRequest Request(ProfileRecord profile, bool makeActive = true) =>
        new(profile.Context, profile.Name, profile.Progress, makeActive);
}

/// <summary>
/// A compare-and-swap store that can yield on every access, so concurrent callers genuinely
/// interleave instead of completing one after another on the calling thread.
/// </summary>
internal sealed class MemoryProfileStore : IProfileWorkspaceStore
{
    private readonly Lock _gate = new();
    private ProfileWorkspaceSnapshot _value = new(0, null, []);
    private int _replaceAttempts;

    public bool YieldOnAccess { get; init; }

    public Action? OnRead { get; set; }

    public int ReplaceAttempts => Volatile.Read(ref _replaceAttempts);

    public ProfileWorkspaceSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }
    }

    public async Task<ProfileWorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (YieldOnAccess)
        {
            await Task.Yield();
        }

        OnRead?.Invoke();
        return Current;
    }

    public async Task<bool> TryReplaceAsync(long expectedRevision, ProfileWorkspaceSnapshot replacement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _replaceAttempts);
        if (YieldOnAccess)
        {
            await Task.Yield();
        }

        lock (_gate)
        {
            if (_value.Revision != expectedRevision)
            {
                return false;
            }

            _value = replacement;
            return true;
        }
    }
}

internal sealed class RetryOnceProfileStore : IProfileWorkspaceStore
{
    private readonly MemoryProfileStore _inner = new();
    private bool _conflictInjected;

    public int ReplaceAttempts { get; private set; }

    public Task<ProfileWorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken) => _inner.ReadAsync(cancellationToken);

    public Task<bool> TryReplaceAsync(long expectedRevision, ProfileWorkspaceSnapshot replacement, CancellationToken cancellationToken)
    {
        ReplaceAttempts++;
        if (!_conflictInjected)
        {
            _conflictInjected = true;
            return Task.FromResult(false);
        }

        return _inner.TryReplaceAsync(expectedRevision, replacement, cancellationToken);
    }
}

internal sealed class NullSnapshotStore : IProfileWorkspaceStore
{
    public Task<ProfileWorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<ProfileWorkspaceSnapshot>(null!);

    public Task<bool> TryReplaceAsync(long expectedRevision, ProfileWorkspaceSnapshot replacement, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

/// <summary>A codec whose output is whatever the test says, including null and hostile documents.</summary>
internal sealed class DelegateProfileCodec(
    Func<string, ProfileTransferDocument?> read,
    Func<ProfileTransferDocument, string?>? write = null) : IProfileTransferCodec
{
    public string Write(ProfileTransferDocument document) =>
        write is null ? throw new NotSupportedException() : write(document)!;

    public ProfileTransferDocument Read(string document) => read(document)!;
}

/// <summary>
/// A list that answers one way the first time it is read and another way every time after, the
/// way a codec that keeps mutating its output would.
/// </summary>
internal sealed class ShiftingProfileList(IReadOnlyList<ProfileRecord> first, IReadOnlyList<ProfileRecord> later) : IReadOnlyList<ProfileRecord>
{
    private int _countReads;
    private int _itemReads;

    public int Count => Interlocked.Increment(ref _countReads) == 1 ? first.Count : later.Count;

    public ProfileRecord this[int index] =>
        Interlocked.Increment(ref _itemReads) <= first.Count ? first[index] : later[index % later.Count];

    public IEnumerator<ProfileRecord> GetEnumerator() => later.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class NegativeCountProfileList : IReadOnlyList<ProfileRecord>
{
    public int Count => -1;

    public ProfileRecord this[int index] => throw new ArgumentOutOfRangeException(nameof(index));

    public IEnumerator<ProfileRecord> GetEnumerator() => Enumerable.Empty<ProfileRecord>().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class ProfileClock(DateTimeOffset now) : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Set(DateTimeOffset value)
    {
        lock (_gate)
        {
            _now = value;
        }
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, Exception? Error)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, exception));
}
