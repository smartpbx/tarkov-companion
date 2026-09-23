using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Events;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.UnitTests.V2Plan;

/// <summary>Writing event rules on Plan > Events: edit, validate, save, read back (#288).</summary>
public sealed class EventRuleEditorTests
{
    private static readonly DateTimeOffset Then = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<EventTargetChoice> Traders =
        [new("54cb50c76803fa8b248b4571", "Prapor"), new("58330581ace78e27b8b10cee", "Skier")];

    private static readonly IReadOnlyList<EventTargetChoice> Maps =
        [new("5714dbc024597771384a510d", "Interchange"), new("56f40101d2720b2a4d8b45d6", "Customs")];

    [Fact]
    public async Task EffectsTypedInTheEditorAreSavedAndReadBackByTheRuleParser()
    {
        var store = new Store(Definition());
        var page = await PageAsync(store);

        page.RuleEditor.AddTraderCommand.Execute(null);
        var trader = page.RuleEditor.Rows.Single();
        Assert.True(trader.UsesPicker);
        trader.SelectedChoice = Traders[0];
        trader.Multiplier = "0.8";
        page.RuleEditor.AddMapCommand.Execute(null);
        page.RuleEditor.Rows[1].SelectedChoice = Maps[0];
        page.RuleEditor.AddFleaCommand.Execute(null);

        Assert.True(page.RuleEditor.IsValid);
        Assert.Equal("While active: Prapor prices x0.8; Interchange closed; Flea closed.", page.RuleEditor.Preview);

        await page.SaveScheduleCommand.ExecuteAsync();

        var saved = Assert.Single(store.Saved);
        var parsed = EventRuleParser.Parse(saved.RulesJson);
        Assert.True(parsed.IsValid);
        Assert.Equal(
            [
                new TraderPriceMultiplierRule("54cb50c76803fa8b248b4571", "Prapor", 0.8m),
                new MapAvailabilityRule("5714dbc024597771384a510d", "Interchange", false),
                new FleaAvailabilityRule(false),
            ],
            parsed.Rules.Effects);
        Assert.True(saved.Provenance.ObservedUtc > Then);

        // Reopened, the rows are what was saved.
        var rows = page.RuleEditor.Rows;
        Assert.Equal(3, rows.Count);
        Assert.Equal("Prapor", rows[0].SelectedChoice?.Name);
        Assert.False(page.RuleEditor.IsDirty);
        Assert.StartsWith("Last changed", page.History, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidValueIsMarkedOnItsFieldAndTheSaveIsRefused()
    {
        var store = new Store(Definition());
        var page = await PageAsync(store);

        page.RuleEditor.AddBossCommand.Execute(null);
        var boss = page.RuleEditor.Rows.Single();
        boss.TargetText = "Killa";
        boss.Multiplier = "lots";

        Assert.False(page.RuleEditor.IsValid);
        Assert.Equal("Above 0, at most 100", boss.ValueError);
        Assert.False(boss.HasTargetError);
        Assert.Empty(page.RuleEditor.Preview);

        await page.SaveScheduleCommand.ExecuteAsync();
        Assert.Empty(store.Saved);
        Assert.Contains("fix the marked effects", page.ScheduleStatus, StringComparison.Ordinal);

        boss.Multiplier = "2";
        Assert.True(page.RuleEditor.IsValid);
        Assert.False(boss.HasValueError);
        Assert.Equal("While active: Killa spawns x2.", page.RuleEditor.Preview);
    }

    [Fact]
    public async Task AQuestWindowIsReadAsLocalDaysAndABackwardsWindowIsMarked()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Editor test -04:00", TimeSpan.FromHours(-4), "Editor test", "Editor test");
        using var _ = LocalTime.UseZone(zone);
        var store = new Store(Definition());
        var page = await PageAsync(store);

        page.RuleEditor.AddQuestCommand.Execute(null);
        var quest = page.RuleEditor.Rows.Single();
        Assert.Equal("Name the quest", quest.TargetError);
        quest.TargetText = "Shooter Born in Heaven";
        quest.StartText = "2026-10-20";
        quest.EndText = "2026-10-10";
        Assert.Equal("Last day is before the first", quest.RowError);

        quest.EndText = "2026-11-02";
        Assert.True(page.RuleEditor.IsValid);
        await page.SaveScheduleCommand.ExecuteAsync();

        var rule = Assert.IsType<QuestAvailabilityWindowRule>(
            EventRuleParser.Parse(Assert.Single(store.Saved).RulesJson).Rules.Effects.Single());
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T04:00:00Z"), rule.StartUtc);
        Assert.Equal("2026-10-20", page.RuleEditor.Rows.Single().StartText);
    }

    [Fact]
    public async Task AHandWrittenRuleWithAMissingMapOpensAsARowMarkedWhereItIsWrong()
    {
        var page = await PageAsync(new Store(Definition() with
        {
            RulesJson = "{\"effects\":[{\"type\":\"map-availability\",\"available\":false}]}",
        }));

        var row = Assert.Single(page.RuleEditor.Rows);
        Assert.Equal(EventEffectKind.MapAvailability, row.Kind);
        Assert.Equal("Pick a map", row.TargetError);

        row.SelectedChoice = Maps[1];
        Assert.True(page.RuleEditor.IsValid);
        Assert.Equal("While active: Customs closed.", page.RuleEditor.Preview);
    }

    [Fact]
    public async Task UntouchedRulesKeepTheirTextWhenOnlyTheScheduleIsSaved()
    {
        const string rules = "{\"consumePrecedence\":\"x\",\"effects\":[{\"type\":\"surprise\"}]}";
        var store = new Store(Definition() with { RulesJson = rules });
        var page = await PageAsync(store);
        Assert.Equal("Unknown type · remove it to save", page.RuleEditor.Rows.Single().RowError);

        await page.ToggleArchivedCommand.ExecuteAsync();

        var saved = Assert.Single(store.Saved);
        Assert.False(saved.Active);
        Assert.Equal(rules, saved.RulesJson);
    }

    [Fact]
    public void WritingKeepsOtherRootPropertiesTheParserTolerates()
    {
        var json = EventRuleDrafts.Write(
            "{\"consumePrecedence\":\"latest\",\"effects\":[]}",
            [new EventEffectDraft(EventEffectKind.FleaAvailability, Enabled: true)]);

        Assert.Contains("\"consumePrecedence\":\"latest\"", json, StringComparison.Ordinal);
        Assert.Equal([new FleaAvailabilityRule(true)], EventRuleParser.Parse(json).Rules.Effects);
    }

    [Fact]
    public async Task DuplicateCopiesRulesAndItemsUnderANewId()
    {
        var store = new Store(Definition() with
        {
            ApplicableItemIds = new HashSet<string>(["item-1"], StringComparer.Ordinal),
            RulesJson = "{\"effects\":[{\"type\":\"flea-availability\",\"enabled\":false}]}",
        });
        var page = await PageAsync(store);

        await page.DuplicateCommand.ExecuteAsync();

        var copy = Assert.Single(store.Saved);
        Assert.Equal("halloween-2026-copy", copy.Id);
        Assert.Equal("Halloween 2026 copy", copy.Name);
        Assert.Contains("item-1", copy.ApplicableItemIds);
        Assert.Equal(store.Definitions[0].RulesJson, copy.RulesJson);
        Assert.Equal("halloween-2026-copy", page.Selected?.EventId);
    }

    private static EventDefinition Definition() => new(
        "halloween-2026",
        "Halloween 2026",
        null,
        null,
        true,
        new HashSet<string>(StringComparer.Ordinal),
        "{}",
        new DataProvenance("local event definition", Then));

    private static async Task<EventsPageViewModel> PageAsync(Store store)
    {
        var page = new EventsPageViewModel(
            store,
            new Tracker(),
            new Repository(),
            store,
            _ => Task.FromResult(Traders),
            _ => Task.FromResult(Maps));
        await page.LoadAsync(CancellationToken.None);
        page.Selected = page.Events.Single();
        return page;
    }

    private sealed class Store(EventDefinition definition) : IEventCatalog, IEventAuthoring
    {
        public List<EventDefinition> Definitions { get; } = [definition];

        public List<EventDefinition> Saved { get; } = [];

        public string DefinitionsDirectory => "/fixture/events";

        public Task<IReadOnlyList<EventDefinition>> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EventDefinition>>([.. Definitions]);

        public Task SaveAsync(EventDefinition saved, CancellationToken cancellationToken)
        {
            Saved.Add(saved);
            var index = Definitions.FindIndex(existing => existing.Id == saved.Id);
            if (index >= 0)
            {
                Definitions[index] = saved;
            }
            else
            {
                Definitions.Add(saved);
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string eventId, CancellationToken cancellationToken)
        {
            Definitions.RemoveAll(existing => existing.Id == eventId);
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
