using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [Package 35] tarkov.dev's maps.json names Customs, Lighthouse and Interchange by slug only, and
/// quests name them by the game's id, so a location as parsed matches no objective. The resolver
/// is what gives it the id.
/// </summary>
public sealed class MapGameIdResolverTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_location_with_no_game_id_is_given_the_one_the_maps_table_has()
    {
        var resolver = new MapGameIdResolver(new Table(new() { ["customs"] = "56f40101d2720b2a4d8b45d6" }));

        var resolved = await resolver.ResolveAsync(Location("customs", null), CancellationToken.None);

        Assert.Equal("56f40101d2720b2a4d8b45d6", resolved.SourceId);
    }

    [Fact]
    public async Task A_location_that_already_has_one_is_left_alone_and_the_table_is_not_asked()
    {
        var table = new Table(new() { ["customs"] = "somebody-else" });
        var resolver = new MapGameIdResolver(table);

        var resolved = await resolver.ResolveAsync(Location("customs", "mine"), CancellationToken.None);

        Assert.Equal("mine", resolved.SourceId);
        Assert.Equal(0, table.Asked);
    }

    [Fact]
    public async Task An_answer_is_asked_for_once_and_a_missing_one_is_asked_for_again()
    {
        var table = new Table([]);
        var resolver = new MapGameIdResolver(table);

        // The maps table is filled by a sync that may not have run yet: nothing is remembered.
        Assert.Null((await resolver.ResolveAsync(Location("woods", null), CancellationToken.None)).SourceId);
        Assert.Null((await resolver.ResolveAsync(Location("woods", null), CancellationToken.None)).SourceId);
        Assert.Equal(2, table.Asked);

        table.Ids["woods"] = "5704e3c2d2720bac5b8b4567";
        Assert.Equal("5704e3c2d2720bac5b8b4567", (await resolver.ResolveAsync(Location("woods", null), CancellationToken.None)).SourceId);
        Assert.Equal("5704e3c2d2720bac5b8b4567", (await resolver.ResolveAsync(Location("woods", null), CancellationToken.None)).SourceId);
        Assert.Equal(3, table.Asked);
    }

    [Fact]
    public async Task A_maps_table_that_fails_leaves_the_location_as_it_was()
    {
        var resolver = new MapGameIdResolver(new Table([]) { Fails = true });

        var resolved = await resolver.ResolveAsync(Location("customs", null), CancellationToken.None);

        Assert.Null(resolved.SourceId);
    }

    private static MapLocation Location(string id, string? sourceId) => new(id, sourceId, id, null, null, []);

    private sealed class Table(Dictionary<string, string> ids) : IMapDataService
    {
        public Dictionary<string, string> Ids { get; } = ids;

        public int Asked { get; private set; }

        public bool Fails { get; init; }

        public Task<MapDefinition?> GetAsync(string mapId, CancellationToken cancellationToken)
        {
            Asked++;
            if (Fails)
            {
                throw new InvalidOperationException("The maps table is unreadable.");
            }

            return Task.FromResult<MapDefinition?>(Ids.TryGetValue(mapId, out var id)
                ? new MapDefinition(mapId, mapId, null, null, [], [], null, new DataProvenance("test", NowUtc)) { GameId = id }
                : null);
        }
    }
}
