using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Infrastructure.Events;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// Writing an event definition from inside the application, rather than by hand.
/// </summary>
/// <remarks>
/// The Events page said to put a JSON file in a directory and restart. Everything underneath it
/// worked — the catalog read the files, the tracker recorded results, the page listed both — but
/// the only way in was a text editor and a document describing the shape.
///
/// Two halves have to hold for that to change: what is written has to be what the loader reads,
/// and the catalog has to stop serving the answer it cached before the write.
/// </remarks>
public sealed class EventAuthoringTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"tarkov-event-authoring-{Guid.NewGuid():N}");

    [Fact]
    public async Task What_is_written_is_what_the_loader_reads_back()
    {
        var catalog = new JsonFileEventCatalog(_directory);

        await catalog.SaveAsync(Definition("halloween", "item-candy", "item-mask"), CancellationToken.None);

        var loaded = Assert.Single(await catalog.GetAsync(CancellationToken.None));
        Assert.Equal("halloween", loaded.Id);
        Assert.Equal("Halloween", loaded.Name);
        Assert.True(loaded.Active);
        Assert.Equal(["item-candy", "item-mask"], loaded.ApplicableItemIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_new_definition_appears_without_a_restart()
    {
        // The whole point. The catalog cached its directory listing for the life of the
        // instance, which is why the page told people to restart.
        var catalog = new JsonFileEventCatalog(_directory);
        Assert.Empty(await catalog.GetAsync(CancellationToken.None));

        await catalog.SaveAsync(Definition("halloween"), CancellationToken.None);

        Assert.Single(await catalog.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Saving_the_same_event_again_replaces_it()
    {
        // Adding an item is a re-save, and it happens once per item. Leaving the old file beside
        // the new one would make the second item added a duplicate definition.
        var catalog = new JsonFileEventCatalog(_directory);
        await catalog.SaveAsync(Definition("halloween", "item-candy"), CancellationToken.None);

        await catalog.SaveAsync(Definition("halloween", "item-candy", "item-mask"), CancellationToken.None);

        var loaded = Assert.Single(await catalog.GetAsync(CancellationToken.None));
        Assert.Equal(2, loaded.ApplicableItemIds.Count);
    }

    [Fact]
    public async Task A_deleted_definition_is_gone_from_the_next_read()
    {
        var catalog = new JsonFileEventCatalog(_directory);
        await catalog.SaveAsync(Definition("halloween"), CancellationToken.None);

        await catalog.DeleteAsync("halloween", CancellationToken.None);

        Assert.Empty(await catalog.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_something_that_is_not_there_is_not_an_error()
    {
        var catalog = new JsonFileEventCatalog(_directory);

        await catalog.DeleteAsync("never-existed", CancellationToken.None);

        Assert.Empty(await catalog.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_id_cannot_choose_where_the_file_goes()
    {
        // The id arrives from a text box by way of a slug. Only the file name is derived from
        // it, so the worst a separator can do is become a dash.
        var catalog = new JsonFileEventCatalog(_directory);

        await catalog.SaveAsync(Definition("../escaped"), CancellationToken.None);

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_directory)!, "*escaped*"));
        Assert.Single(Directory.GetFiles(_directory, "*.json"));
    }

    [Fact]
    public async Task The_tracker_knows_an_event_written_after_it_was_built()
    {
        // The tracker was handed a list read from the catalog once, at startup. An event created
        // on the page was listed by that page and then refused every result recorded against it.
        var catalog = new JsonFileEventCatalog(_directory);
        var profiles = new InMemoryProfiles();
        using var tracker = new ProfileEventTrackerService(profiles, [], null, catalog);

        await catalog.SaveAsync(Definition("halloween", "item-candy"), CancellationToken.None);
        await tracker.SetItemStateAsync("halloween", "item-candy", EventItemState.Safe, CancellationToken.None);

        Assert.Equal(
            EventItemState.Safe,
            await tracker.GetItemStateAsync("halloween", "item-candy", CancellationToken.None));
    }

    [Fact]
    public async Task An_item_added_after_the_tracker_was_built_can_be_recorded()
    {
        var catalog = new JsonFileEventCatalog(_directory);
        var profiles = new InMemoryProfiles();
        using var tracker = new ProfileEventTrackerService(profiles, [], null, catalog);
        await catalog.SaveAsync(Definition("halloween", "item-candy"), CancellationToken.None);

        await catalog.SaveAsync(Definition("halloween", "item-candy", "item-mask"), CancellationToken.None);
        await tracker.SetItemStateAsync("halloween", "item-mask", EventItemState.Allergic, CancellationToken.None);

        var progress = await tracker.GetProgressAsync("halloween", CancellationToken.None);
        Assert.Equal(2, progress.Total);
        Assert.Equal(1, progress.Allergic);
    }

    [Fact]
    public async Task An_event_that_is_in_no_catalog_is_still_refused()
    {
        var catalog = new JsonFileEventCatalog(_directory);
        var profiles = new InMemoryProfiles();
        using var tracker = new ProfileEventTrackerService(profiles, [], null, catalog);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            tracker.GetItemStateAsync("no-such-event", "item-candy", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            ScratchDirectory.Remove(_directory);
        }
    }

    private static EventDefinition Definition(string id, params string[] itemIds) => new(
        id,
        "Halloween",
        null,
        null,
        true,
        new HashSet<string>(itemIds, StringComparer.Ordinal),
        "{}",
        new DataProvenance("local event definition", DateTimeOffset.UnixEpoch));

    /// <summary>A profile that lives for the test, so recorded states can be read back.</summary>
    private sealed class InMemoryProfiles : IPlayerProfileService
    {
        private PlayerProfile _profile = new(
            Guid.Parse("6f2b6f3c-0d8c-4d2a-9d4f-2a1f2e8c9b31"),
            "test",
            GameMode.Regular,
            20,
            Faction.Usec,
            "Standard",
            new Dictionary<string, int>(),
            new HashSet<string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            new HashSet<string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, EventItemState>(),
            new Dictionary<string, string>(),
            DateTimeOffset.UnixEpoch);

        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_profile);

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken)
        {
            _profile = profile;
            return Task.CompletedTask;
        }

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
