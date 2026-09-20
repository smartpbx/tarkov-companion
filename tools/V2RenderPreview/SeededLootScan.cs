using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// <c>--loot-seeded</c>: the Loot decision page over the composed application, with nothing
/// typed in but what a recogniser would have read.
/// </summary>
/// <remarks>
/// <para>
/// <c>--loot-demo</c> hands the page a result whose needs, prices and pins were written by hand,
/// so it shows the layout and proves nothing about the wiring. This seeds the profile the way a
/// player would - a quest made active through the quest command service, a pin through the Loot
/// Scan's own controls, an Allergic result through the Events page's view model - and then hands
/// the composed <see cref="ICaptureResultHandoff"/> one analysed frame. Everything after that
/// is the shipped path: the handoff, the needs, the flea fee, the engine, the planner, the
/// capture bridge and the shell.
/// </para>
/// <para>
/// OCR and icon matching cannot run here, so the frame's cells are named by catalog lookup.
/// That is the one fixture, and it sits exactly where recognition ends.
/// </para>
/// </remarks>
internal static class SeededLootScan
{
    internal static void Run(
        IServiceProvider services,
        MainWindowViewModel viewModel,
        Action<Task> drain,
        Action<int> pump,
        string? fleaRates,
        string? phase,
        string? seedDatabase)
    {
        var clock = services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        drain(ScanFrame.SeedFleaRatesAsync(services, fleaRates, now));
        drain(RestoreQuestItemRowsAsync(services, seedDatabase));
        services.GetRequiredService<LootScanRaidPreference>().Phase = phase is null
            ? RecommendationRaidPhase.Early
            : Enum.Parse<RecommendationRaidPhase>(phase, ignoreCase: true);

        var items = services.GetRequiredService<IItemRepository>();
        ItemDefinition? Find(string name)
        {
            var search = items.SearchAsync(name, 8, CancellationToken.None);
            drain(search);
            return search.Result.FirstOrDefault(hit => string.Equals(hit.Item.Name, name, StringComparison.OrdinalIgnoreCase))?.Item
                ?? search.Result.FirstOrDefault()?.Item;
        }

        ItemDefinition? Get(string id)
        {
            var read = items.GetAsync(id, CancellationToken.None);
            drain(read);
            return read.Result;
        }

        // A quest the player is on: the first catalog requirement for a small barter item whose
        // quest is on the board, made active through the same command service the Plan page uses.
        var requirements = services.GetRequiredService<IRequirementCatalog>();
        var questRows = requirements.GetQuestRequirementsAsync(CancellationToken.None);
        drain(questRows);
        var profileTask = services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(CancellationToken.None);
        drain(profileTask);
        var profile = profileTask.Result;
        var questScope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var boardTask = services.GetRequiredService<IQuestReadService>().GetQuestBoardAsync(questScope, CancellationToken.None);
        drain(boardTask);
        var board = boardTask.Result.Tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
        // Army crackers are one of the hand-ins for Acquaintance, an early Therapist quest that
        // does not ask for found-in-raid, which a screenshot cannot read yet.
        var questItem = Find("Army crackers");
        var questRow = questRows.Result
            .Where(row => row.ItemId == questItem?.Id && board.ContainsKey(row.TaskId) && !row.FoundInRaidRequired)
            .OrderByDescending(row => board[row.TaskId].Name == "Acquaintance")
            .FirstOrDefault();
        Console.WriteLine($"Seeded loot scan: {questRows.Result.Count} quest requirement rows, {questRows.Result.Count(row => row.ItemId == questItem?.Id)} for {questItem?.Name}.");
        if (questRow is not null)
        {
            drain(services.GetRequiredService<IQuestProgressCommandService>()
                .SetTaskStateAsync(questScope, questRow.TaskId, RecordedTaskState.Active, CancellationToken.None));
            Console.WriteLine($"Seeded loot scan: quest '{board[questRow.TaskId].Name}' is active and needs {questRow.Required} x {questItem!.Name}.");
        }

        // The hideout needs it: the first level-one requirement for a one-square item.
        var hideoutRows = requirements.GetHideoutRequirementsAsync(CancellationToken.None);
        drain(hideoutRows);
        var hideoutItem = hideoutRows.Result
            .Where(row => row.TargetLevel == 1 && row.ItemId != questItem?.Id)
            .Select(row => Get(row.ItemId))
            .FirstOrDefault(item => item is { Dimensions.Slots: 1, Category: ItemCategory.Barter });

        // A pin, through the Loot Scan workspace's own controls.
        var pinned = Find("Soap");
        if (pinned is not null)
        {
            drain(services.GetRequiredService<ILootScanWorkspaceControls>().SetPinnedAsync(pinned.Id, true));
        }

        // An Allergic result, through the Events page.
        var events = viewModel.Events;
        events.NewEventName = "Feast 2026";
        drain(events.CreateCommand.ExecuteAsync());
        pump(40);
        events.ItemQuery = "Can of condensed milk";
        drain(events.SearchCommand.ExecuteAsync());
        events.Matches.FirstOrDefault()?.AddCommand.Execute(null);
        pump(60);
        var allergicId = events.Items.FirstOrDefault()?.ItemId;
        events.Items.FirstOrDefault()?.MarkAllergicCommand.Execute(null);
        pump(60);
        var allergic = allergicId is null ? null : Get(allergicId);

        // Worth its squares and wanted by nothing, so price alone decides each of these.
        var valuable = Find("Memento Server RAM Module");
        var bulky = Find("Graphics card");
        ItemDefinition?[] cheap = [Find("Zenit B-2U rail"), Find("MXLR trigger")];
        var carriedCheap = Find("Bolts");
        var carriedOther = Find("Screw nuts");

        var builder = new FrameBuilder(now.AddSeconds(-5));
        ItemDefinition?[] loot = [questItem, hideoutItem, pinned, valuable, bulky, .. cheap, allergic];
        var lootCells = builder.Place(6, 4, 1260, loot.OfType<ItemDefinition>().DistinctBy(item => item.Id).ToArray());
        // A backpack with one free square: room for the small take, and none for the two-square
        // one without giving something up.
        var carried = builder.Place(
            3,
            2,
            600,
            [.. new[] { carriedCheap, carriedOther, carriedCheap, carriedOther, carriedOther }.OfType<ItemDefinition>()]);

        var context = services.GetRequiredService<ShellCaptureContextSource>().Describe();
        var session = new CaptureSessionId(Guid.NewGuid());
        var analysis = new CaptureAnalysis(
            new string('b', 64),
            RecognizedContext.Loot,
            false,
            true,
            null,
            new Confidence(0.95),
            new GridReconstructionRequest(InventoryGridSurface.VisibleLoot, builder.Lattice(4, 6, 1260), lootCells),
            CarriedGrid: new GridReconstructionRequest(InventoryGridSurface.CarriedInventory, builder.Lattice(2, 3, 600), carried));
        var request = new CaptureHandoffRequest(
            session,
            "seeded-loot-frame",
            analysis,
            context,
            CaptureCorrelationId.New(),
            CaptureSourceKind.GameWrittenScreenshot,
            now.AddSeconds(-5),
            now.AddSeconds(-4),
            null,
            CaptureDeliveryKind.WatchedFile,
            builder.Provenance,
            0,
            CaptureReviewAction.UseDetected,
            ScanIntent.Loot,
            new CaptureCorrection(CaptureReviewAction.UseDetected, ScanIntent.Loot, RecognizedContext.Loot, 0, now, "render-preview"));

        LootScanViewModel? shown = null;
        var accepted = services.GetRequiredService<ICaptureResultHandoff>().AcceptAsync(request, CancellationToken.None).AsTask();
        drain(accepted);
        pump(40);
        shown = services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.Shell.V2ShellViewModel>().LootScanResult;
        foreach (var card in shown?.Decisions ?? [])
        {
            Console.WriteLine($"Seeded loot scan: {card.VerdictLabel,-6} {card.Name} | {card.HeadlineReason} | {card.WhyLabel}");
        }

        // The row a render is about: the quest hand-in, so its reason is on screen.
        if (shown?.Decisions.FirstOrDefault(card => card.Name == questItem?.Name) is { } questCard)
        {
            shown.Select(questCard);
            pump(10);
        }

        if (shown is null)
        {
            Console.WriteLine("Seeded loot scan: NO RESULT reached the shell.");
        }
    }

    /// <summary>
    /// Puts back the quest hand-in rows a seeded database loses on its way through startup.
    /// </summary>
    /// <remarks>
    /// Migration 0013 drops <c>task_objectives</c> before it copies <c>task_objective_items</c>,
    /// and the old items table cascades on that delete, so a database from before it arrives
    /// with no hand-in rows at all. The running application gets them back from its next tasks
    /// sync. An offline preview never syncs, so the rows are copied from the seed file, and the
    /// need aggregation is refreshed the way the startup coordinator does after a sync.
    /// </remarks>
    private static async Task RestoreQuestItemRowsAsync(IServiceProvider services, string? seedDatabase)
    {
        if (seedDatabase is null)
        {
            return;
        }

        await using (var connection = await services.GetRequiredService<TarkovCompanion.Infrastructure.Persistence.SqliteConnectionFactory>().OpenAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ATTACH DATABASE $seed AS seed;
                INSERT OR IGNORE INTO task_objective_items(task_id, objective_id, item_id, count, found_in_raid_required)
                SELECT objective.task_id, item.objective_id, item.item_id, item.count, item.found_in_raid_required
                FROM seed.task_objective_items AS item
                JOIN seed.task_objectives AS objective ON objective.id = item.objective_id
                JOIN main.task_objectives AS kept ON kept.task_id = objective.task_id AND kept.id = item.objective_id;
                """;
            command.Parameters.AddWithValue("$seed", seedDatabase);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var requirements = services.GetRequiredService<IRequirementCatalog>();
        requirements.Invalidate();
        services.GetRequiredService<TarkovCompanion.Application.Services.Profile.ProfileNeedAggregationService>().Update(
            await requirements.GetQuestRequirementsAsync(CancellationToken.None),
            await requirements.GetHideoutRequirementsAsync(CancellationToken.None));
    }

    private sealed class FrameBuilder(DateTimeOffset observedUtc)
    {
        private const int Cell = 63;

        public EvidenceProvenance Provenance { get; } = new(
            EvidenceSourceClass.GameWrittenScreenshot,
            "render://seeded-loot-frame",
            observedUtc,
            new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.97),
            new ProducerIdentity("v2-render-preview", "1"));

        public DetectedGridLattice Lattice(int rows, int columns, int left) => new(
            rows,
            columns,
            Cell,
            Cell,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
            Provenance,
            new EvidenceRegion(left, 180, columns * Cell, rows * Cell, EvidenceCoordinateSpace.SourcePixels));

        /// <summary>Lays items out left to right, top to bottom, at their catalog size.</summary>
        public GridCellObservation[] Place(int columns, int rows, int left, ItemDefinition[] items)
        {
            var taken = new bool[rows, columns];
            var cells = new List<GridCellObservation>();
            foreach (var item in items)
            {
                var (width, height) = (item.Dimensions.Width, item.Dimensions.Height);
                for (var at = 0; at < rows * columns; at++)
                {
                    var (row, column) = (at / columns, at % columns);
                    if (!Fits(taken, row, column, width, height, rows, columns))
                    {
                        continue;
                    }

                    for (var r = row; r < row + height; r++)
                    {
                        for (var c = column; c < column + width; c++)
                        {
                            taken[r, c] = true;
                        }
                    }

                    cells.Add(Named(row, column, left, item));
                    break;
                }
            }

            return [.. cells];
        }

        private static bool Fits(bool[,] taken, int row, int column, int width, int height, int rows, int columns)
        {
            if (row + height > rows || column + width > columns)
            {
                return false;
            }

            for (var r = row; r < row + height; r++)
            {
                for (var c = column; c < column + width; c++)
                {
                    if (taken[r, c])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private GridCellObservation Named(int row, int column, int left, ItemDefinition item)
        {
            var recognized = new RecognizedItem(
                Known("id", item.Id),
                Known("name", item.Name),
                Known<int?>("quantity", 1),
                Known<int?>("width", item.Dimensions.Width),
                Known<int?>("height", item.Dimensions.Height),
                Known<bool?>("rotated", false),
                new EvidencedValue<bool?>("fir", null, new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current), Provenance),
                Known("condition", ItemConditionReading.NotApplicable));
            return new(
                $"cell-{left}-{row}-{column}",
                new GridCellAddress(row, column),
                Known(
                    "item",
                    recognized,
                    new EvidenceRegion(
                        left + (column * Cell),
                        180 + (row * Cell),
                        item.Dimensions.Width * Cell,
                        item.Dimensions.Height * Cell,
                        EvidenceCoordinateSpace.SourcePixels)));
        }

        private EvidencedValue<T> Known<T>(string fieldId, T value, EvidenceRegion? bounds = null) =>
            new(fieldId, value, new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current), Provenance, bounds);
    }
}
