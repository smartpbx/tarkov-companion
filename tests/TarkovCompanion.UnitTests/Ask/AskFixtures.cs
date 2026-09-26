using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Ask;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.Ask;

/// <summary>
/// In-memory versions of the services the Ask box reads, filled with names and ids from the
/// 2026-09-14 catalog (the seed database), so each intent is exercised against real names.
/// </summary>
internal static class AskFixtures
{
    public static readonly DateTimeOffset CatalogTime = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    public static readonly DataProvenance Provenance = new("tarkov.dev", CatalogTime, CatalogTime);

    public const string Ledx = "5c0530ee86f774697952d952";
    public const string Dorm314 = "5780cf7f2459777de4559322";
    public const string GasAnalyzer = "590a3efd86f77437d351a25b";
    public const string Bs545 = "56dff026d2720bb8668b4567";
    public const string Ps545 = "56dff3afd2720bba668b4567";
    public const string Igolnik545 = "5c0d5e4486f77478390952fe";
    public const string Ps762x39 = "5656d7c34bdc2d9d198b4587";
    public const string Bp762x39 = "59e0d99486f7744a32234762";
    public const string Ps762x54 = "59e77a2386f7742ee578960a";
    public const string Toilet = "5d484fba654e7600691aadf7";
    public const string Workbench = "5d484fda654e7600681d9315";
    public const string Gunsmith5 = "5b477b6f86f7747290681823";
    public const string Gunsmith1 = "5ac23c6186f7741247042bad";

    public static readonly ItemDefinition[] Items =
    [
        Item(Ledx, "LEDX Skin Transilluminator", "LEDX", ItemCategory.Barter),
        Item(Dorm314, "Dorm room 314 marked key", "Dorm 314", ItemCategory.Key),
        Item(GasAnalyzer, "Gas analyzer", "GasAn", ItemCategory.Barter),
        Item(Bs545, "5.45x39mm BS gs", "BS", ItemCategory.Ammunition),
        Item(Ps545, "5.45x39mm PS gs", "PS", ItemCategory.Ammunition),
        Item(Igolnik545, "5.45x39mm PPBS gs \"Igolnik\"", "PPBS", ItemCategory.Ammunition),
        Item(Ps762x39, "7.62x39mm PS gzh", "PS", ItemCategory.Ammunition),
        Item(Bp762x39, "7.62x39mm BP gzh", "BP", ItemCategory.Ammunition),
        Item(Ps762x54, "7.62x54mm R PS gzh", "PS", ItemCategory.Ammunition),
        Item("5d1b36a186f7742523398433", "Metal fuel tank", "Tank", ItemCategory.Barter),
        Item("5d1b371186f774253763a656", "Expeditionary fuel tank", "ExpTank", ItemCategory.Barter),
        Item("5d1b385e86f774252167b98a", "Water filter", "Filter", ItemCategory.Barter),
    ];

    public static ItemDefinition Item(string id, string name, string shortName, ItemCategory category) => new(
        id,
        name,
        shortName,
        string.Empty,
        category,
        new ItemDimensions(1, 1),
        FleaEligible: true,
        IconUri: null,
        ImageUri: null,
        WikiUri: null,
        PropertiesType: null,
        PropertiesJson: null,
        CategoryIds: new HashSet<string>(),
        Provenance: Provenance);

    public static PlayerProfile Profile(IReadOnlyDictionary<string, int>? hideout = null) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "Local profile",
        GameMode.Regular,
        30,
        Faction.Usec,
        null,
        new Dictionary<string, int>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, int>(StringComparer.Ordinal),
        hideout ?? new Dictionary<string, int>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, int>(StringComparer.Ordinal),
        new Dictionary<string, EventItemState>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        CatalogTime);

    public static QuestSummaryReadModel Task(
        string id,
        string name,
        string trader,
        int? level,
        IReadOnlyList<QuestObjectiveReadModel> objectives,
        RecordedTaskState state = RecordedTaskState.NotStarted,
        IReadOnlyList<string>? after = null) => new(
            id,
            name,
            "trader-" + trader,
            null,
            state,
            "test",
            null,
            new QuestEligibility(QuestEligibilityState.Available, []),
            RecordedObjectivesSatisfaction.Indeterminate,
            false,
            null,
            false,
            [],
            [.. (after ?? []).Select(required => new QuestPrerequisiteReadModel(required, ["complete"], RecordedTaskState.Completed))],
            objectives)
        {
            TraderName = trader,
            MinimumPlayerLevel = level,
        };

    public static QuestObjectiveReadModel Objective(
        string description,
        QuestObjectiveKind kind = QuestObjectiveKind.BuildWeapon,
        decimal? count = null,
        bool? foundInRaid = null) => new(
            Guid.NewGuid().ToString("N"),
            description,
            kind,
            false,
            false,
            RecordedObjectiveState.Unknown,
            null,
            count,
            foundInRaid,
            "test",
            null,
            false,
            [],
            []);

    public static readonly QuestSummaryReadModel[] Tasks =
    [
        Task(Gunsmith1, "Gunsmith Master - Part 1", "Mechanic", 2, [Objective("Modify an MP-133 to comply with the given specifications")]),
        Task(Gunsmith5, "Gunsmith Master - Part 5", "Mechanic", 40, [Objective("Modify an AKMN to comply with the given specifications")], after: [Gunsmith1]),
        Task("5b47825886f77468074618d3", "Gunsmith Master - Part 10", "Mechanic", 45, [Objective("Modify an AK-105 to comply with the given specifications")]),
        Task("5ac345dc86f774288030817f", "Farming - Part 1", "Mechanic", 11, [
            Objective("Hand over the found in raid item: Metal fuel tank", QuestObjectiveKind.GiveItem, 2, true),
            Objective("Hand over the item: Gas analyzer", QuestObjectiveKind.GiveItem, 3, false),
        ]),
    ];

    public static RulesAnswerSource Source(
        IAskRaidSource? raid = null,
        IReadOnlyDictionary<string, V2ItemIntelResult>? cards = null,
        IReadOnlyList<IntelTradeRow>? trades = null,
        IReadOnlyDictionary<string, int>? hideout = null) =>
        new(
            new FakeItems(Items),
            new FakeIntel(cards ?? new Dictionary<string, V2ItemIntelResult>()),
            new FakeQuests(Tasks),
            new FakeProfiles(Profile(hideout)),
            new FakeRequirements(),
            prerequisites: new FakePrerequisites(),
            trades: new FakeTrades(trades ?? []),
            facts: new FakeFacts(),
            raid: raid);

    /// <summary>Scores like the item search does: whole name or short name, prefix and edit distance.</summary>
    internal sealed class FakeItems(IReadOnlyList<ItemDefinition> items) : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(items.FirstOrDefault(item => item.Id == itemId));

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<ItemSearchHit>>(
            [
                .. items
                    .Select(item => new ItemSearchHit(item, Math.Max(FuzzyMatcher.Similarity(query, item.Name), FuzzyMatcher.ShortNameSimilarity(query, item.ShortName)), item.Name))
                    .Where(hit => hit.Score >= 0.6)
                    .OrderByDescending(hit => hit.Score)
                    .Take(limit),
            ]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken)
        {
            long? flea = itemId switch
            {
                Bs545 => 1_450,
                Ps545 => 180,
                Igolnik545 => 700,
                Ps762x39 => 160,
                Bp762x39 => 900,
                _ => null,
            };
            return System.Threading.Tasks.Task.FromResult<ItemPriceSnapshot?>(new(flea, [], null, null, null, Provenance));
        }
    }

    internal sealed class FakeIntel(IReadOnlyDictionary<string, V2ItemIntelResult> cards) : IItemIntelService
    {
        public Task<V2ItemIntelResult> GetAsync(string itemId, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(cards.TryGetValue(itemId, out var card)
                ? card
                : new V2ItemIntelResult(V2IntelKind.Item, itemId, itemId, itemId, null, ItemCategory.Barter, 1, 1, true, Keep: new([], [])));
    }

    internal sealed class FakeQuests(IReadOnlyList<QuestSummaryReadModel> tasks) : IQuestReadService
    {
        public Task<QuestBoardReadModel> GetQuestBoardAsync(QuestProfileScope scope, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new QuestBoardReadModel(
                scope,
                1,
                new QuestCatalogProvenance("tarkov.dev", "https://json.tarkov.dev", GameMode.Regular, "regular", "en", "a", "b", null, null, CatalogTime, CatalogTime),
                tasks,
                []));

        public Task<QuestItemNeedsReadModel> GetItemNeedsAsync(QuestProfileScope scope, string itemId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuestMapObjectivesReadModel> GetActiveMapObjectivesAsync(QuestProfileScope scope, IReadOnlyCollection<string> mapIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    internal sealed class FakeProfiles(PlayerProfile profile) : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(profile);

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Lavatory 1–4 and Workbench 1–3; Lavatory 2's items as the catalog lists them.</summary>
    internal sealed class FakeRequirements : IRequirementCatalog
    {
        public Task<IReadOnlyList<HideoutItemRequirement>> GetHideoutRequirementsAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<HideoutItemRequirement>>(
            [
                new(Toilet, 2, "5d1b36a186f7742523398433", 2),
                new(Toilet, 2, "5d1b385e86f774252167b98a", 1),
                new(Toilet, 3, "5d1b371186f774253763a656", 2),
                new(Workbench, 2, GasAnalyzer, 2),
            ]);

        public Task<IReadOnlyList<QuestItemRequirement>> GetQuestRequirementsAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<QuestItemRequirement>>([]);

        public Task<IReadOnlyList<HideoutStationSummary>> GetStationsAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<HideoutStationSummary>>(
            [
                new(Toilet, "Lavatory", [1, 2, 3, 4]),
                new(Workbench, "Workbench", [1, 2, 3]),
            ]);

        public void Invalidate()
        {
        }
    }

    internal sealed class FakePrerequisites : IHideoutPrerequisiteCatalog
    {
        public Task<Core.Domain.Planning.HideoutPrerequisites> GetAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new Core.Domain.Planning.HideoutPrerequisites(
                [new(Toilet, 2, "5d3b396e33c48f02b81cd9f3", 1)],
                [new(Toilet, 2, "Loyalty level 1 with Mechanic")]));
    }

    internal sealed class FakeTrades(IReadOnlyList<IntelTradeRow> rows) : IIntelTradeCatalogService
    {
        public Task<IReadOnlyList<IntelTradeRow>> GetAllAsync(CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(rows);
    }

    internal sealed class FakeFacts : IItemFactCatalog
    {
        public Task<IReadOnlyList<AmmoStats>> GetAmmoAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<AmmoStats>>(
            [
                Ammo(Bs545, "Caliber545x39", 45, 54),
                Ammo(Ps545, "Caliber545x39", 50, 28),
                Ammo(Igolnik545, "Caliber545x39", 37, 62),
                Ammo(Ps762x39, "Caliber762x39", 57, 35),
                Ammo(Bp762x39, "Caliber762x39", 58, 47),
                Ammo(Ps762x54, "Caliber762x54R", 84, 45),
            ]);

        public Task<IReadOnlyList<AmmoPackContents>> GetAmmoPacksAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<AmmoPackContents>>([]);

        public Task<IReadOnlyList<LoadoutItemFacts>> GetLoadoutFactsAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<LoadoutItemFacts>>([]);

        public Task<IReadOnlyList<KeyFacts>> GetKeyFactsAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<KeyFacts>>(
            [
                new(Dorm314, "56f40101d2720b2a4d8b45d6", 10, ["Dorm room 314"], ["5ac345dc86f774288030817f"], 60_000, 0, 0, false, 0, Provenance),
            ]);

        public void Invalidate()
        {
        }

        private static AmmoStats Ammo(string id, string caliber, int damage, int penetration) =>
            new(id, caliber, damage, penetration, null, null, 1, null, null, null, false, false, Provenance);
    }

    internal sealed class FakeRaid(AskRaidSnapshot? snapshot) : IAskRaidSource
    {
        public AskRaidSnapshot? Current() => snapshot;
    }
}
