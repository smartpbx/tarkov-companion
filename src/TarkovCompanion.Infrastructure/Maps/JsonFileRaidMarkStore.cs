using System.Text.Json;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.Infrastructure.Maps;

/// <summary>Local raid marks, kept in <c>Config/raid-marks.json</c> between runs.</summary>
/// <remarks>
/// An unreadable file falls back to an empty board rather than to nothing loading at all: see
/// <see cref="JsonFileEftPathOverrideStore"/> for the same reasoning applied to game folders.
///
/// Issue 584: a ping never got an expiry stamped on it here, so it sat forever exactly like a
/// waypoint — placed from the desktop's own right-click or from a paired tablet through
/// <c>RelayMarksBridge</c>, both reach <see cref="AddAsync"/>. A ping now carries
/// <c>MapMarkState.ExpiresUtc</c>, the store never hands one back once that has passed
/// (<see cref="Marks"/>, and <see cref="LoadAsync"/> cleans a stale file on the spot), and a timer
/// kept pointed at the next one due drops it — and tells <see cref="Changed"/>'s subscribers, which
/// is what moves it off the map and the tablet's — the moment it happens, not whenever something
/// else next touches the store.
/// </remarks>
public sealed class JsonFileRaidMarkStore : IRaidMarkStore, IDisposable
{
    private const int MaximumMarks = 500;
    private const long MaximumFileBytes = 512 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _storePath;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // #889: published copy-on-write. Readers (the UI thread, GroupMarkForwarder on whatever thread
    // Changed fired on) enumerate this without the gate, so a writer never changes an array it has
    // published: it builds a new one and swaps it in. Mutating the live List in place let a
    // tablet's placement on the relay pool thread break a foreach on the expiry timer's thread,
    // inside async void OnExpiryDue, which ends the process.
    private RaidMark[] _marks = [];
    private bool _loaded;
    private ITimer? _expiryTimer;
    private bool _disposed;

    public JsonFileRaidMarkStore(string storePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        _storePath = storePath;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Never includes an expired ping. Filters rather than mutates <c>_marks</c>: reading this is
    /// not itself allowed to touch the file, and the timer below (or the next add/move/rename/
    /// remove/load) is what actually drops one and rewrites it. Returns a snapshot no later write
    /// changes (#889).
    /// </summary>
    public IReadOnlyList<RaidMark> Marks
    {
        get
        {
            var now = _timeProvider.GetUtcNow();
            var marks = Volatile.Read(ref _marks);
            return Array.Exists(marks, mark => IsExpired(mark, now))
                ? [.. marks.Where(mark => !IsExpired(mark, now))]
                : marks;
        }
    }

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pruned = false;
        try
        {
            if (_loaded)
            {
                return;
            }

            var document = await ReadOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _marks, document is null ? [] : [.. document.Marks.Select(ToMark).OfType<RaidMark>()]);
            _loaded = true;
            // An old file full of stale pings (from before this fix, or from a desktop that was
            // off for an hour) cleans itself the first time anything loads it, rather than
            // carrying dead pings forward until some other mutation happens to rewrite the file.
            pruned = PruneExpired();
            if (pruned)
            {
                await WriteUntilNoExpiredMarksAsync(cancellationToken).ConfigureAwait(false);
            }

            ScheduleNextExpiry();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public Task<RaidMark> AddAsync(
        RaidMarkKind kind,
        string mapId,
        string? floorId,
        double x,
        double y,
        string? label,
        CancellationToken cancellationToken = default) =>
        PlaceAsync(mapId, floorId, x, y, label, RaidMarkScope.Squad, RaidMarkLifetimes.DefaultFor(kind), cancellationToken);

    public async Task<RaidMark> PlaceAsync(
        string mapId,
        string? floorId,
        double x,
        double y,
        string? label,
        RaidMarkScope scope,
        RaidMarkLifetime lifetime,
        CancellationToken cancellationToken = default,
        RaidMarkRoute? route = null,
        string? colour = null)
    {
        var now = _timeProvider.GetUtcNow();
        // A waypoint is a plan and stays until removed; a ping is "look here, now" and this is the
        // one place that decides how long "now" lasts (issue 584). Since #289 the player picks the
        // lifetime, and the lifetime decides the kind: only the 45-second one is a ping.
        var mark = new RaidMark(
            Guid.NewGuid(),
            RaidMarkLifetimes.KindFor(lifetime),
            new MapMarkState(mapId, floorId, x, y, label, RaidMarkLifetimes.ExpiresUtc(lifetime, now)),
            now)
        {
            Scope = scope,
            Lifetime = lifetime,
            Route = route,
            Colour = MarkPalette.Normalize(colour),
        };
        await MutateAsync(marks => marks.Add(mark), cancellationToken).ConfigureAwait(false);
        return mark;
    }

    public Task SetOptionsAsync(Guid id, RaidMarkScope scope, RaidMarkLifetime lifetime, CancellationToken cancellationToken = default) =>
        MutateAsync(
            marks =>
            {
                var index = marks.FindIndex(mark => mark.Id == id);
                if (index < 0)
                {
                    return;
                }

                var current = marks[index];
                // An unchanged lifetime keeps its clock running; a new one counts from now,
                // which is what "make it 5 min" means to somebody choosing it mid-raid.
                var expiresUtc = current.Lifetime == lifetime
                    ? current.State.ExpiresUtc
                    : RaidMarkLifetimes.ExpiresUtc(lifetime, _timeProvider.GetUtcNow());
                var kind = RaidMarkLifetimes.KindFor(lifetime);
                marks[index] = current with
                {
                    Kind = kind,
                    Scope = scope,
                    Lifetime = lifetime,
                    State = new MapMarkState(
                        current.State.MapId,
                        current.State.FloorId,
                        current.State.X,
                        current.State.Y,
                        // A ping is never told apart by name, so one made from a waypoint drops it.
                        kind == RaidMarkKind.Ping ? null : current.State.Label,
                        expiresUtc),
                };
            },
            cancellationToken);

    public Task EndRaidAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(marks => marks.RemoveAll(mark => mark.Lifetime == RaidMarkLifetime.ThisRaid), cancellationToken);

    public Task MoveAsync(Guid id, double x, double y, CancellationToken cancellationToken = default) =>
        MutateAsync(
            marks =>
            {
                var index = marks.FindIndex(mark => mark.Id == id);
                if (index < 0)
                {
                    return;
                }

                var current = marks[index];
                // MapMarkState re-declares X/Y as validated get-only properties (see its own
                // remark on why), which is exactly the shape a `with` expression cannot touch;
                // a new instance is required instead.
                marks[index] = current with
                {
                    State = new MapMarkState(
                        current.State.MapId,
                        current.State.FloorId,
                        x,
                        y,
                        current.State.Label,
                        current.State.ExpiresUtc),
                };
            },
            cancellationToken);

    public Task RenameAsync(Guid id, string? label, CancellationToken cancellationToken = default) =>
        MutateAsync(
            marks =>
            {
                var index = marks.FindIndex(mark => mark.Id == id);
                if (index < 0)
                {
                    return;
                }

                var trimmed = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
                var normalized = trimmed is { Length: > MapMarkState.MaxLabelLength }
                    ? trimmed[..MapMarkState.MaxLabelLength]
                    : trimmed;
                var current = marks[index];
                marks[index] = current with
                {
                    State = new MapMarkState(
                        current.State.MapId,
                        current.State.FloorId,
                        current.State.X,
                        current.State.Y,
                        normalized,
                        current.State.ExpiresUtc),
                };
            },
            cancellationToken);

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
        MutateAsync(marks => marks.RemoveAll(mark => mark.Id == id), cancellationToken);

    /// <summary>
    /// [#799] The PC's clock was set: moves every mark's creation and expiry by the same jump.
    /// </summary>
    /// <remarks>
    /// A ping stamped "expires at 04:20:35" by a clock four hours fast would otherwise live four
    /// more hours once the clock came back, and one stamped before a jump forward would expire
    /// on the spot. Moved, each keeps exactly the time it had left. The timer is re-pointed by
    /// the mutation, and the moved times are what the file keeps.
    /// </remarks>
    public Task RebaseClockAsync(TimeSpan jump, CancellationToken cancellationToken = default) =>
        jump == TimeSpan.Zero
            ? Task.CompletedTask
            : MutateAsync(
                marks =>
                {
                    for (var index = 0; index < marks.Count; index++)
                    {
                        var current = marks[index];
                        marks[index] = current with
                        {
                            CreatedUtc = current.CreatedUtc + jump,
                            State = new MapMarkState(
                                current.State.MapId,
                                current.State.FloorId,
                                current.State.X,
                                current.State.Y,
                                current.State.Label,
                                current.State.ExpiresUtc + jump),
                        };
                    }
                },
                cancellationToken);

    private async Task MutateAsync(Action<List<RaidMark>> mutate, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A private copy: the published array is never changed after readers can see it.
            var next = new List<RaidMark>(_marks);
            mutate(next);
            var now = _timeProvider.GetUtcNow();
            next.RemoveAll(mark => IsExpired(mark, now));
            if (next.Count > MaximumMarks)
            {
                // Oldest first: a mark placed minutes ago is more likely to still matter than
                // one placed a raid ago.
                next = [.. next.OrderByDescending(mark => mark.CreatedUtc).Take(MaximumMarks)];
            }

            Volatile.Write(ref _marks, [.. next]);

            await WriteUntilNoExpiredMarksAsync(cancellationToken).ConfigureAwait(false);
            ScheduleNextExpiry();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Runs when the timer <see cref="ScheduleNextExpiry"/> set reaches the earliest ping still
    /// due to expire. Drops it (and anything else that has since caught up to it), rewrites the
    /// file, and reschedules for whatever is next — the mechanism that moves a stale ping off the
    /// map without a person doing anything else, or a poll checking whether it is time yet.
    /// </summary>
    private async void OnExpiryDue(object? state)
    {
        if (_disposed)
        {
            return;
        }

        bool pruned;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            pruned = PruneExpired();
            if (pruned)
            {
                await WriteUntilNoExpiredMarksAsync(CancellationToken.None).ConfigureAwait(false);
            }

            ScheduleNextExpiry();
        }
        catch (IOException)
        {
            // A missed write here is not fatal: Marks already filters expired entries on every
            // read, and the next add/move/rename/remove/load rewrites the file anyway.
            pruned = false;
        }
        finally
        {
            // An async void timer callback: the store can be disposed while it writes, and an
            // exception escaping here takes the whole process down (a test host did exactly that).
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (pruned)
        {
            // Outside every try above and inside async void: a subscriber's exception here would
            // escape on the thread pool and end the process (#889), so it stops at this line.
            try
            {
                Changed?.Invoke();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.TraceWarning($"Raid mark expiry subscriber failed: {exception.GetType().Name}");
            }
        }
    }

    /// <summary>Points the one live timer at whichever ping is due to expire soonest (now, if one already is),
    /// replacing whatever it was previously waiting for. Called with <see cref="_gate"/> held.</summary>
    private void ScheduleNextExpiry()
    {
        _expiryTimer?.Dispose();
        _expiryTimer = null;
        if (_disposed)
        {
            return;
        }

        // A ping that is already past its time still counts, and gets a zero delay. It can be here
        // because the prune ran before the file write and the write outlasted what the ping had
        // left (a 30 ms test lifetime on a loaded CI runner did exactly that). Skipping it left no
        // timer at all, so the ping sat in _marks and Changed never fired for it (issue 602).
        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? next = null;
        foreach (var mark in _marks)
        {
            if (mark.State.ExpiresUtc is not { } expires)
            {
                continue;
            }

            if (next is null || expires < next)
            {
                next = expires;
            }
        }

        if (next is not { } dueAt)
        {
            return;
        }

        var delay = dueAt - now;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        _expiryTimer = _timeProvider.CreateTimer(OnExpiryDue, null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Removes every mark whose time has passed. Called with <see cref="_gate"/> held.</summary>
    private bool PruneExpired()
    {
        var now = _timeProvider.GetUtcNow();
        var marks = _marks;
        if (!Array.Exists(marks, mark => IsExpired(mark, now)))
        {
            return false;
        }

        Volatile.Write(ref _marks, [.. marks.Where(mark => !IsExpired(mark, now))]);
        return true;
    }

    private static bool IsExpired(RaidMark mark, DateTimeOffset now) =>
        mark.State.ExpiresUtc is { } expiresUtc && now >= expiresUtc;

    private Task WriteAsync(CancellationToken cancellationToken) =>
        AtomicJsonFile.WriteAsync(
            _storePath,
            JsonSerializer.Serialize(new MarkDocument([.. _marks.Select(ToRow)]), JsonOptions),
            cancellationToken);

    /// <summary>
    /// Rechecks the clock after every write. A loaded disk can outlast a ping's remaining life;
    /// returning before rewriting that newly expired ping leaves another process able to reload it
    /// while the zero-delay timer is still waiting for a thread-pool turn.
    /// </summary>
    private async Task WriteUntilNoExpiredMarksAsync(CancellationToken cancellationToken)
    {
        do
        {
            await WriteAsync(cancellationToken).ConfigureAwait(false);
        }
        while (PruneExpired());
    }

    private async Task<MarkDocument?> ReadOrDefaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(_storePath);
            if (!info.Exists || info.Length > MaximumFileBytes)
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(_storePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<MarkDocument>(text, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Null for a row a hand-edited or schema-drifted file made invalid, so one bad row
    /// costs that mark rather than every mark the file held.</summary>
    private static RaidMark? ToMark(MarkRow row)
    {
        try
        {
            return new RaidMark(
                row.Id,
                row.Kind,
                new MapMarkState(row.MapId, row.FloorId, row.X, row.Y, row.Label, row.ExpiresUtc),
                row.CreatedUtc)
            {
                // #289: absent for every row written before scope and lifetime were chosen, and
                // those marks were all sent to the group, so Squad is what they already were.
                Scope = row.Scope ?? RaidMarkScope.Squad,
                Lifetime = row.Lifetime ?? RaidMarkLifetimes.DefaultFor(row.Kind),
                Route = row.RouteId is { } routeId && row.RouteStep is { } step ? new RaidMarkRoute(routeId, step) : null,
                // #290: a hand-edited colour outside the palette loads as none rather than drawn.
                Colour = MarkPalette.Normalize(row.Colour),
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static MarkRow ToRow(RaidMark mark) => new(
        mark.Id,
        mark.Kind,
        mark.State.MapId,
        mark.State.FloorId,
        mark.State.X,
        mark.State.Y,
        mark.State.Label,
        mark.CreatedUtc,
        mark.State.ExpiresUtc,
        mark.Scope,
        mark.Lifetime,
        mark.Route?.RouteId,
        mark.Route?.Step,
        mark.Colour);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _expiryTimer?.Dispose();
        _gate.Dispose();
    }

    private sealed record MarkDocument(IReadOnlyList<MarkRow> Marks);

    private sealed record MarkRow(
        Guid Id,
        RaidMarkKind Kind,
        string MapId,
        string? FloorId,
        double X,
        double Y,
        string? Label,
        DateTimeOffset CreatedUtc,
        // Issue 584: absent (null) for every row written before this fix, and for a waypoint
        // forever — ToMark's MapMarkState validation is what keeps a garbled value from loading.
        DateTimeOffset? ExpiresUtc = null,
        RaidMarkScope? Scope = null,
        RaidMarkLifetime? Lifetime = null,
        Guid? RouteId = null,
        int? RouteStep = null,
        string? Colour = null);
}
