using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.UnitTests.V2Plan;

/// <summary>
/// Scheduling, renaming and archiving an event (#288).
/// </summary>
/// <remarks>
/// The file format has carried a name, a window and an active flag since it was written, and
/// nothing in the application ever let anybody set them: every event created here was undated and
/// permanently in season. Archiving keeps the definition and everything recorded against it;
/// only Delete removes one, which is why the two are separate actions.
/// </remarks>
public sealed class EventScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("2026-10-12", "2026-10-12")]
    public void ADateIsReadAndAnEmptyBoxMeansTheAuthorDidNotSay(string typed, string? expected)
    {
        Assert.True(EventsPageViewModel.TryReadDate(typed, out var value));
        Assert.Equal(expected is null ? null : DateTimeOffset.Parse(expected + "T00:00:00Z"), value);
    }

    [Fact]
    public void SomethingThatIsNotADateIsRefused() =>
        Assert.False(EventsPageViewModel.TryReadDate("next Tuesday-ish", out _));

    [Fact]
    public void APlayersLocalCalendarDayIsStoredAsTheMatchingUtcInstant()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Event test -04:00",
            TimeSpan.FromHours(-4),
            "Event test",
            "Event test");
        using var _ = LocalTime.UseZone(zone);

        Assert.True(EventsPageViewModel.TryReadDate("2026-10-12", out var value));
        Assert.Equal(DateTimeOffset.Parse("2026-10-12T04:00:00Z"), value);
    }

    [Fact]
    public async Task AWindowIsWrittenBackToTheDefinition()
    {
        var authoring = new Authoring(Definition());
        var page = await PageAsync(authoring);

        page.ScheduleStart = "2026-10-12";
        page.ScheduleEnd = "2026-11-02";
        await page.SaveScheduleCommand.ExecuteAsync();

        var saved = Assert.Single(authoring.Saved);
        Assert.Equal(DateTimeOffset.Parse("2026-10-12T00:00:00Z"), saved.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T00:00:00Z"), saved.EndUtc);
        Assert.True(saved.Active);
    }

    [Fact]
    public async Task AWindowThatEndsBeforeItStartsIsRefusedRatherThanWritten()
    {
        var authoring = new Authoring(Definition());
        var page = await PageAsync(authoring);

        page.ScheduleStart = "2026-11-02";
        page.ScheduleEnd = "2026-10-12";
        await page.SaveScheduleCommand.ExecuteAsync();

        Assert.Empty(authoring.Saved);
        Assert.Contains("before the first", page.ScheduleStatus, StringComparison.Ordinal);
        Assert.Contains("before the first", page.SchedulePreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEventWithNoNameIsRefused()
    {
        var authoring = new Authoring(Definition());
        var page = await PageAsync(authoring);

        page.RenameTo = "   ";
        await page.SaveScheduleCommand.ExecuteAsync();

        Assert.Empty(authoring.Saved);
        Assert.Contains("needs a name", page.ScheduleStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchivingKeepsTheDefinitionAndItsId()
    {
        var authoring = new Authoring(Definition());
        var page = await PageAsync(authoring);

        await page.ToggleArchivedCommand.ExecuteAsync();

        var saved = Assert.Single(authoring.Saved);
        Assert.False(saved.Active);
        Assert.Equal("halloween-2026", saved.Id);
        Assert.Empty(authoring.Deleted);
    }

    [Fact]
    public async Task RenamingKeepsTheIdTheResultsAreStoredAgainst()
    {
        var authoring = new Authoring(Definition());
        var page = await PageAsync(authoring);

        page.RenameTo = "Halloween, second week";
        await page.SaveScheduleCommand.ExecuteAsync();

        var saved = Assert.Single(authoring.Saved);
        Assert.Equal("Halloween, second week", saved.Name);
        Assert.Equal("halloween-2026", saved.Id);
    }

    [Fact]
    public async Task ThePreviewSaysWhereTodaySitsInTheWindow()
    {
        var page = await PageAsync(new Authoring(Definition()));

        page.ScheduleStart = "2999-01-01";
        Assert.Contains("starts in", page.SchedulePreview, StringComparison.Ordinal);

        page.ScheduleStart = string.Empty;
        page.ScheduleEnd = "2000-01-01";
        Assert.Contains("ended", page.SchedulePreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypedRulesAreValidatedAndPreviewedForTheSelectedEvent()
    {
        var definition = Definition() with
        {
            RulesJson = """
                {"effects":[
                  {"type":"trader-price-multiplier","traderId":"prapor","traderName":"Prapor","multiplier":0.8},
                  {"type":"map-availability","mapId":"laboratory","mapName":"Labs","available":false}
                ]}
                """,
        };

        var page = await PageAsync(new Authoring(definition));

        Assert.Equal("While active: Prapor prices x0.8; Labs closed.", page.RulePreview);
        Assert.Equal("2 effects validated", page.RuleStatus);
        Assert.False(page.HasRuleIssues);
    }

    [Fact]
    public async Task InvalidRulesShowTheirJsonPathAndNoPreview()
    {
        var definition = Definition() with
        {
            RulesJson = "{\"effects\":[{\"type\":\"map-availability\",\"available\":false}]}",
        };

        var page = await PageAsync(new Authoring(definition));

        Assert.True(page.HasRuleIssues);
        Assert.Contains("$.effects[0].mapId", page.RuleStatus, StringComparison.Ordinal);
        Assert.Empty(page.RulePreview);
    }

    private static EventDefinition Definition() => new(
        "halloween-2026",
        "Halloween 2026",
        null,
        null,
        true,
        new HashSet<string>(StringComparer.Ordinal),
        "{}",
        new DataProvenance("local event definition", Now));

    private static async Task<EventsPageViewModel> PageAsync(Authoring authoring)
    {
        var page = new EventsPageViewModel(authoring, new Tracker(), new Repository(), authoring);
        await page.LoadAsync(CancellationToken.None);
        page.Selected = page.Events.Single();
        return page;
    }

    private sealed class Authoring(EventDefinition definition) : IEventCatalog, IEventAuthoring
    {
        private EventDefinition _definition = definition;

        public List<EventDefinition> Saved { get; } = [];

        public List<string> Deleted { get; } = [];

        public string DefinitionsDirectory => "/fixture/events";

        public Task<IReadOnlyList<EventDefinition>> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EventDefinition>>([_definition]);

        public Task SaveAsync(EventDefinition saved, CancellationToken cancellationToken)
        {
            Saved.Add(saved);
            _definition = saved;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string eventId, CancellationToken cancellationToken)
        {
            Deleted.Add(eventId);
            return Task.CompletedTask;
        }
    }

    private sealed class Tracker : IEventTrackerService
    {
        public Task<EventItemState> GetItemStateAsync(string eventId, string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(EventItemState.Untested);

        public Task SetItemStateAsync(string eventId, string itemId, EventItemState state, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<EventProgress> GetProgressAsync(string eventId, CancellationToken cancellationToken) =>
            Task.FromResult(new EventProgress(eventId, 0, 0, 0, 0, 0));
    }

    private sealed class Repository : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }
}
