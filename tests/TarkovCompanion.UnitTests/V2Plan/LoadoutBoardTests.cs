using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Loadouts;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Loadouts;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.UnitTests.V2Plan;

/// <summary>
/// The kit board, the budget and the saved kits (#288).
/// </summary>
/// <remarks>
/// The Loadout page was a combo box and a flat list of whatever happened to be assigned, so
/// neither question a kit is looked at to answer — what is in it, and what is still missing — had
/// an answer without counting. These hold the three things that changed.
/// </remarks>
public sealed class LoadoutBoardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheBoardAlwaysShowsEveryMissingSlot()
    {
        var page = Page();

        Assert.Equal(10, page.SlotBoard.Count);
        Assert.All(page.SlotBoard, tile => Assert.False(tile.IsFilled));
        Assert.Equal("0 of 10 slots filled", page.SlotBoardSummary);
    }

    [Fact]
    public async Task AssigningFillsItsTileAndClearingEmptiesIt()
    {
        var page = Page();
        page.Apply(Snapshot(items: 1));
        page.SearchQuery = "carbine";
        await page.SearchAsync(CancellationToken.None);
        Assert.Single(page.Results);

        page.Results[0].AssignCommand.Execute(null);

        var weapon = page.SlotBoard.Single(tile => tile.Slot == LoadoutSlot.Weapon);
        Assert.True(weapon.IsFilled);
        Assert.Equal("Carbine", weapon.Summary);
        Assert.Equal("1 of 10 slots filled", page.SlotBoardSummary);

        weapon.ClearSlotCommand.Execute(null);

        Assert.False(page.SlotBoard.Single(tile => tile.Slot == LoadoutSlot.Weapon).IsFilled);
        Assert.Equal("0 of 10 slots filled", page.SlotBoardSummary);
    }

    [Fact]
    public async Task RecognizedEquipmentPrefillsTheSameBoard()
    {
        var page = Page();
        page.Apply(Snapshot(items: 3));

        var loaded = await page.LoadRecognizedItemsAsync(["carbine", "helmet", "bandage"]);

        Assert.Equal(3, loaded);
        Assert.Equal("3 of 10 slots filled", page.SlotBoardSummary);
        Assert.Equal("Carbine", page.SlotBoard.Single(tile => tile.Slot == LoadoutSlot.Weapon).Summary);
        Assert.Equal("Helmet", page.SlotBoard.Single(tile => tile.Slot == LoadoutSlot.Helmet).Summary);
        Assert.Equal("Bandage", page.SlotBoard.Single(tile => tile.Slot == LoadoutSlot.Medical).Summary);
        Assert.Equal("3 recognized items assigned.", page.AssignmentStatus);
    }

    [Fact]
    public void AGenericWeaponPartIsNotInventedAsAMagazine()
    {
        Assert.Null(LoadoutPageViewModel.SlotForRecognized(ItemCategory.Attachment, "M-LOK handguard"));
        Assert.Equal(
            LoadoutSlot.Magazine,
            LoadoutPageViewModel.SlotForRecognized(ItemCategory.Attachment, "30-round magazine"));
    }

    [Fact]
    public void PressingATileAimsTheSearchAtThatSlot()
    {
        var page = Page();

        page.SlotBoard.Single(tile => tile.Slot == LoadoutSlot.Helmet).SelectCommand.Execute(null);

        Assert.Equal(LoadoutSlot.Helmet, page.SelectedSlot.Slot);
        Assert.True(page.SlotBoard.Single(tile => tile.Slot == LoadoutSlot.Helmet).IsSelected);
    }

    [Theory]
    [InlineData("250000", 250_000)]
    [InlineData("250,000", 250_000)]
    [InlineData("250k", 250_000)]
    [InlineData("1.2m", 1_200_000)]
    [InlineData(" 900K ", 900_000)]
    public void ABudgetIsReadHoweverItIsTyped(string typed, long expected)
    {
        Assert.True(LoadoutPageViewModel.TryReadBudget(typed, out var roubles));
        Assert.Equal(expected, roubles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("-5")]
    public void ABudgetThatIsNotANumberIsRefused(string typed) =>
        Assert.False(LoadoutPageViewModel.TryReadBudget(typed, out _));

    [Fact]
    public async Task TheBudgetSaysWhatIsLeftAndWhenItIsGone()
    {
        var page = Page();
        page.Apply(Snapshot(items: 1));
        page.SearchQuery = "carbine";
        await page.SearchAsync(CancellationToken.None);
        page.Results[0].AssignCommand.Execute(null);
        await page.EvaluateAsync(CancellationToken.None);

        page.BudgetInput = "60k";
        Assert.False(page.IsOverBudget);
        Assert.Contains("left", page.BudgetSummary, StringComparison.Ordinal);

        page.BudgetInput = "20k";
        Assert.True(page.IsOverBudget);
        Assert.Contains("over by", page.BudgetSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKitComesBackWithItsSlotsIntact()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonFileLoadoutPresetStore(directory.File("loadouts.json"));
        var page = Page(store);
        page.Apply(Snapshot(items: 1));
        page.SearchQuery = "carbine";
        await page.SearchAsync(CancellationToken.None);
        page.Results[0].AssignCommand.Execute(null);

        page.PresetName = "Scav run";
        await page.SavePresetAsync(CancellationToken.None);
        page.Clear();
        Assert.Equal("0 of 10 slots filled", page.SlotBoardSummary);

        await page.LoadPresetAsync("Scav run", CancellationToken.None);

        var weapon = page.SlotBoard.Single(tile => tile.Slot == LoadoutSlot.Weapon);
        Assert.True(weapon.IsFilled);
        Assert.Equal("Carbine", weapon.Summary);
    }

    [Fact]
    public async Task AnEmptyKitIsNotSaved()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonFileLoadoutPresetStore(directory.File("loadouts.json"));
        var page = Page(store);

        page.PresetName = "Nothing";
        await page.SavePresetAsync(CancellationToken.None);

        Assert.Empty(await store.GetAsync(CancellationToken.None));
        Assert.Contains("Assign something", page.PresetStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageWithNoStoreOffersNoSaveButton() =>
        await Task.Run(() => Assert.False(Page().CanSavePresets));

    [Fact]
    public async Task SavingTwiceUnderOneNameKeepsOneKit()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonFileLoadoutPresetStore(directory.File("loadouts.json"));

        await store.SaveAsync(new LoadoutPreset("Scav run", Now, [new("Weapon", "a", "A")]), CancellationToken.None);
        await store.SaveAsync(new LoadoutPreset("scav RUN", Now.AddMinutes(1), [new("Weapon", "b", "B")]), CancellationToken.None);

        var saved = Assert.Single(await store.GetAsync(CancellationToken.None));
        Assert.Equal("b", saved.Items[0].ItemId);
    }

    [Fact]
    public async Task OnlyTheMostRecentKitsAreKept()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonFileLoadoutPresetStore(directory.File("loadouts.json"));

        for (var index = 0; index < LoadoutPreset.MaximumPresets + 4; index++)
        {
            await store.SaveAsync(
                new LoadoutPreset($"Kit {index}", Now.AddMinutes(index), [new("Weapon", "a", "A")]),
                CancellationToken.None);
        }

        var saved = await store.GetAsync(CancellationToken.None);
        Assert.Equal(LoadoutPreset.MaximumPresets, saved.Count);
        Assert.Equal($"Kit {LoadoutPreset.MaximumPresets + 3}", saved[0].Name);
    }

    [Fact]
    public async Task AnUnreadableFileOffersNoKitsRatherThanFailing()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("loadouts.json");
        await File.WriteAllTextAsync(path, "{ not json", CancellationToken.None);

        Assert.Empty(await new JsonFileLoadoutPresetStore(path).GetAsync(CancellationToken.None));
    }

    /// <summary>#283: the ammo line says how many of the assigned round a stash or case scan counted.</summary>
    [Fact]
    public async Task TheAssignedRoundSaysHowManyAreOwned()
    {
        var profile = new TarkovCompanion.UnitTests.V2Intel.OwnedCountsProfile(
            new Dictionary<string, int>(StringComparer.Ordinal) { ["m855"] = 20, ["m855-pack"] = 1 });
        var page = new LoadoutPageViewModel(new FactCatalog(), new SearchService(), new Repository(), null, new FixedClock(), profiles: profile);
        page.Apply(Snapshot(items: 4));
        await page.LoadRecognizedItemsAsync(["carbine", "m855"]);

        await page.EvaluateAsync();
        Assert.EndsWith(" · 50 owned", page.AmmoTierSummary, StringComparison.Ordinal);

        profile.Owned = new Dictionary<string, int>(StringComparer.Ordinal);
        await page.EvaluateAsync();
        Assert.EndsWith(" · owned not scanned", page.AmmoTierSummary, StringComparison.Ordinal);
    }

    private static LoadoutPageViewModel Page(ILoadoutPresetStore? presets = null) =>
        new(new FactCatalog(), new SearchService(), new Repository(), presets, new FixedClock());

    private static ApplicationRuntimeSnapshot Snapshot(int items) =>
        new RuntimeStateStore(new RuntimeOptions(true, true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5)))
            .Current with
        {
            Data = new RuntimeDataState(DataAvailability.DemoFixture, items, 1, Now, "fixture"),
        };

    private static ItemDefinition Carbine { get; } = new(
        "carbine",
        "Carbine",
        "CBN",
        string.Empty,
        ItemCategory.Weapon,
        new ItemDimensions(4, 1),
        true,
        null,
        null,
        null,
        null,
        null,
        new HashSet<string>(StringComparer.Ordinal),
        new DataProvenance("fixture", Now));

    private static ItemDefinition Helmet { get; } = Item("helmet", "Helmet", ItemCategory.Helmet);

    private static ItemDefinition Bandage { get; } = Item("bandage", "Bandage", ItemCategory.Medicine);

    private static ItemDefinition Item(string id, string name, ItemCategory category) => new(
        id,
        name,
        name,
        string.Empty,
        category,
        new ItemDimensions(1, 1),
        true,
        null,
        null,
        null,
        null,
        null,
        new HashSet<string>(StringComparer.Ordinal),
        new DataProvenance("fixture", Now));

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FactCatalog : IItemFactCatalog
    {
        public Task<IReadOnlyList<AmmoStats>> GetAmmoAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AmmoStats>>([]);

        public Task<IReadOnlyList<AmmoPackContents>> GetAmmoPacksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AmmoPackContents>>([new("m855-pack", "m855", 30, new DataProvenance("fixture", Now))]);

        public Task<IReadOnlyList<LoadoutItemFacts>> GetLoadoutFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LoadoutItemFacts>>(
            [
                new(
                    "carbine",
                    "Carbine",
                    ItemCategory.Weapon,
                    45_000,
                    3.2,
                    "5.56x45",
                    new HashSet<string>(StringComparer.Ordinal),
                    new HashSet<string>(StringComparer.Ordinal)),
                new("helmet", "Helmet", ItemCategory.Helmet, 20_000, 1.2, null,
                    new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)),
                new("bandage", "Bandage", ItemCategory.Medicine, 2_000, 0.1, null,
                    new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)),
                new("m855", "M855", ItemCategory.Ammunition, 300, 0.01, "5.56x45",
                    new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)),
            ]);

        public Task<IReadOnlyList<KeyFacts>> GetKeyFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KeyFacts>>([]);

        public void Invalidate()
        {
        }
    }

    private sealed class SearchService : IItemSearchService
    {
        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>(
                Carbine.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    ? [new(Carbine, 1, Carbine.Name)]
                    : []);
    }

    private sealed class Repository : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(itemId switch
            {
                "carbine" => Carbine,
                "helmet" => Helmet,
                "bandage" => Bandage,
                "m855" => Item("m855", "M855", ItemCategory.Ammunition),
                _ => null,
            });

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"tc-loadouts-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(_root);

        public string File(string name) => Path.Combine(_root, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
