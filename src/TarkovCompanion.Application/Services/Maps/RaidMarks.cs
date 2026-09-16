using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// A local mark's kind, named to match <c>TarkovCompanion.CompanionProtocol.MapMarkKind</c> so a
/// later pairing sync can adapt one to the other without a model rewrite.
/// </summary>
public enum RaidMarkKind
{
    Ping = 1,
    Waypoint,
}

/// <summary>
/// A user-placed mark on a map, kept locally. The payload is the Core v2
/// <see cref="MapMarkState"/> — the same shape the paired protocol's own mark carries — so this
/// store's rows need no translation to become the tablet package's marks.
/// </summary>
public sealed record RaidMark(Guid Id, RaidMarkKind Kind, MapMarkState State, DateTimeOffset CreatedUtc);

/// <summary>Local pings and waypoints, added and moved from the raid map and kept between runs.</summary>
public interface IRaidMarkStore
{
    IReadOnlyList<RaidMark> Marks { get; }

    /// <summary>Raised after a load, add, move, or removal changes <see cref="Marks"/>.</summary>
    event Action? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task<RaidMark> AddAsync(
        RaidMarkKind kind,
        string mapId,
        string? floorId,
        double x,
        double y,
        string? label,
        CancellationToken cancellationToken = default);

    Task MoveAsync(Guid id, double x, double y, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}
