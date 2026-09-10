using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.Profile;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests;

public sealed class QuestProgressExchangeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-10T16:00:00Z");
    private static readonly JsonSerializerOptions ApiSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    [Fact]
    public void PreviewRowsExposeTheLocalAndIncomingValuesBeingResolved()
    {
        var task = new QuestImportProposalViewModel(new(
            "task:task-contract:",
            QuestProgressEntityKind.Task,
            "task-contract",
            null,
            QuestImportClassification.Conflict,
            new(TaskState: RecordedTaskState.Active),
            new(TaskState: RecordedTaskState.Completed),
            "Incoming state differs."));
        var objective = new QuestImportProposalViewModel(new(
            "objective:objective-find-item:",
            QuestProgressEntityKind.Objective,
            "objective-find-item",
            null,
            QuestImportClassification.Conflict,
            new(ObjectiveState: RecordedObjectiveState.Completed, ObjectiveCount: 2),
            new(ObjectiveState: RecordedObjectiveState.InProgress, ObjectiveCount: 1),
            "Incoming progress regresses."),
            QuestImportResolution.KeepLocal);

        Assert.Equal("Local: Active", task.LocalValue);
        Assert.Equal("Incoming: Completed", task.IncomingValue);
        Assert.Equal("Local: Completed · count 2", objective.LocalValue);
        Assert.Equal("Incoming: InProgress · count 1", objective.IncomingValue);
        Assert.Equal("KeepLocal", objective.Resolution);
    }

    [Fact]
    public async Task VersionTwoRoundTripIsDeterministicBoundedAndSecretFree()
    {
        await using var context = await ExchangeContext.CreateAsync();
        await context.Store.ApplyAsync(TaskMutation(context.Scope, RecordedTaskState.Active), CancellationToken.None);
        await context.Store.ApplyAsync(new SetObjectiveProgressMutation(
            context.Scope,
            context.Profile.Name,
            "objective-find-item",
            RecordedObjectiveState.InProgress,
            1.5m,
            QuestProgressActor.User,
            "Manual",
            Guid.NewGuid(),
            Now), CancellationToken.None);
        await context.Store.ApplyAsync(HoldingMutation(context.Scope, true, 2), CancellationToken.None);
        await context.Store.ApplyAsync(HoldingMutation(context.Scope, false, 7), CancellationToken.None);
        await context.Store.ApplyAsync(PinMutation(
            context.Scope,
            QuestPinTargetKind.Task,
            "task-contract",
            "/home/alice/private/profile.txt"), CancellationToken.None);
        await context.Store.ApplyAsync(PinMutation(
            context.Scope,
            QuestPinTargetKind.Objective,
            "objective-mark",
            "Bring markers"), CancellationToken.None);

        var firstPath = Path.Combine(context.Directory, "first.json");
        var secondPath = Path.Combine(context.Directory, "second.json");
        var progress = await context.Store.GetAsync(context.Scope, CancellationToken.None);
        var first = await context.Json.WriteAsync(firstPath, context.Profile, progress, CancellationToken.None);
        var second = await context.Json.WriteAsync(secondPath, context.Profile, progress, CancellationToken.None);
        var text = await File.ReadAllTextAsync(firstPath);
        var root = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("Export was not an object.");
        root["futureEnvelopeField"] = "ignored";
        root["payload"]!["futurePayloadField"] = 42;
        await File.WriteAllTextAsync(firstPath, root.ToJsonString());

        var imported = await context.Json.ReadAsync(firstPath, CancellationToken.None);
        var profile = Assert.Single(imported.Profiles);

        Assert.Equal(ProjectQuestProgressFormat.Identifier, imported.FormatId);
        Assert.Equal(ProjectQuestProgressFormat.Version, imported.FormatVersion);
        Assert.Equal(first.PayloadSha256, second.PayloadSha256);
        Assert.Equal(first.PayloadSha256, imported.PayloadSha256);
        Assert.Equal(context.Profile.Id, profile.ProfileId);
        Assert.Equal(context.Profile.Name, profile.ProfileName);
        Assert.Equal(context.Profile.GameMode, profile.GameMode);
        Assert.Equal(context.Profile.ProfileGeneration, profile.ProfileGeneration);
        Assert.Equal(RecordedTaskState.Active, Assert.Single(profile.Tasks).State);
        Assert.Equal(1.5m, Assert.Single(profile.Objectives).Count);
        Assert.Equal([false, true], profile.Holdings.Select(value => value.FoundInRaid).ToArray());
        Assert.Null(Assert.Single(profile.Pins, value => value.TargetKind == QuestPinTargetKind.Task).Note);
        Assert.Equal(
            "Bring markers",
            Assert.Single(profile.Pins, value => value.TargetKind == QuestPinTargetKind.Objective).Note);
        Assert.DoesNotContain("/home/alice", text, StringComparison.Ordinal);
        Assert.DoesNotContain("assertionSource", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(context.Directory, "*.tmp"));
    }

    [Fact]
    public async Task ParserRejectsDuplicateMissingNegativeNonFiniteAndUnsupportedData()
    {
        await using var context = await ExchangeContext.CreateAsync();
        var validPath = await WriteIncomingAsync(
            context,
            tasks: [new("task-contract", RecordedTaskState.Completed)],
            objectives: [new("objective-find-item", RecordedObjectiveState.InProgress, 2)]);
        var valid = JsonNode.Parse(await File.ReadAllTextAsync(validPath))?.AsObject()
            ?? throw new InvalidDataException("Export was not an object.");

        await AssertInvalidMutationAsync(context, valid, "duplicate.json", root =>
        {
            var tasks = root["payload"]!["profiles"]![0]!["tasks"]!.AsArray();
            tasks.Add(tasks[0]!.DeepClone());
        }, "duplicate");
        await AssertInvalidMutationAsync(context, valid, "missing.json", root =>
        {
            root["payload"]!["profiles"]![0]!["objectives"]![0]!.AsObject().Remove("count");
        }, "count");
        await AssertInvalidMutationAsync(context, valid, "negative.json", root =>
        {
            root["payload"]!["profiles"]![0]!["objectives"]![0]!["count"] = -1;
        }, "negative");
        await AssertInvalidMutationAsync(context, valid, "non-finite.json", root =>
        {
            root["payload"]!["profiles"]![0]!["objectives"]![0]!["count"] = "NaN";
        }, "finite");
        await AssertInvalidMutationAsync(context, valid, "version.json", root =>
        {
            root["formatVersion"] = 99;
        }, "version");
    }

    [Fact]
    public async Task ReaderEnforcesHardFileAndDepthLimitsBeforeProducingADocument()
    {
        var directory = TemporaryDirectory();
        try
        {
            var filePath = Path.Combine(directory, "oversized.json");
            await File.WriteAllTextAsync(filePath, new string('x', 65));
            var bounded = new ProjectQuestProgressJson(new("test", MaximumFileBytes: 64));
            var sizeException = await Assert.ThrowsAsync<InvalidDataException>(() =>
                bounded.ReadAsync(filePath, CancellationToken.None));
            Assert.Contains("64-byte", sizeException.Message, StringComparison.Ordinal);

            filePath = Path.Combine(directory, "deep.json");
            await File.WriteAllTextAsync(filePath, new string('[', 40) + new string(']', 40));
            var depthException = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ProjectQuestProgressJson(new("test", MaximumDepth: 8))
                    .ReadAsync(filePath, CancellationToken.None));
            Assert.Contains("bounded JSON", depthException.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReaderEnforcesProfileRecordAndStringLimits()
    {
        await using var context = await ExchangeContext.CreateAsync();
        var path = await WriteIncomingAsync(
            context,
            tasks: [new("task-contract", RecordedTaskState.Completed)],
            holdings: [new("item-a", false, 1)]);
        var recordException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProjectQuestProgressJson(new("test", MaximumRecords: 1))
                .ReadAsync(path, CancellationToken.None));
        Assert.Contains("record", recordException.Message, StringComparison.OrdinalIgnoreCase);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsObject()
            ?? throw new InvalidDataException("Export was not an object.");
        var profiles = root["payload"]!["profiles"]!.AsArray();
        profiles.Add(profiles[0]!.DeepClone());
        var profilePath = Path.Combine(context.Directory, "too-many-profiles.json");
        await File.WriteAllTextAsync(profilePath, root.ToJsonString());
        var profileException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProjectQuestProgressJson(new("test", MaximumProfiles: 1))
                .ReadAsync(profilePath, CancellationToken.None));
        Assert.Contains("profiles", profileException.Message, StringComparison.OrdinalIgnoreCase);

        root = JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsObject()
            ?? throw new InvalidDataException("Export was not an object.");
        root["futureLongString"] = new string('q', 129);
        var stringPath = Path.Combine(context.Directory, "long-string.json");
        await File.WriteAllTextAsync(stringPath, root.ToJsonString());
        var stringException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProjectQuestProgressJson(new("test", MaximumStringLength: 128))
                .ReadAsync(stringPath, CancellationToken.None));
        Assert.Contains("string", stringException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewClassifiesApplyIsAtomicIdempotentAndUndoIsJournaled()
    {
        await using var context = await ExchangeContext.CreateAsync();
        await context.Store.ApplyAsync(TaskMutation(context.Scope, RecordedTaskState.Active), CancellationToken.None);
        await context.Store.ApplyAsync(new SetObjectiveProgressMutation(
            context.Scope,
            context.Profile.Name,
            "objective-find-item",
            RecordedObjectiveState.Completed,
            2,
            QuestProgressActor.User,
            "Manual",
            Guid.NewGuid(),
            Now), CancellationToken.None);
        await context.Store.ApplyAsync(new SetObjectiveProgressMutation(
            context.Scope,
            context.Profile.Name,
            "objective-mark",
            RecordedObjectiveState.Completed,
            null,
            QuestProgressActor.User,
            "Manual",
            Guid.NewGuid(),
            Now), CancellationToken.None);
        await context.Store.ApplyAsync(HoldingMutation(context.Scope, true, 5), CancellationToken.None);
        var beforeImport = await context.Store.GetAsync(context.Scope, CancellationToken.None);
        var importPath = await WriteIncomingAsync(
            context,
            tasks:
            [
                new("task-contract", RecordedTaskState.Completed),
                new("unknown-task", RecordedTaskState.Completed),
            ],
            objectives: [new("objective-find-item", RecordedObjectiveState.InProgress, 1)],
            holdings:
            [
                new("item-a", true, 7),
                new("item-a", false, 2),
            ],
            pins: [new(QuestPinTargetKind.Objective, "objective-mark", 4, "Next")]);

        var preview = await context.Exchange.PreviewImportAsync(
            context.Scope, importPath, CancellationToken.None);

        Assert.Equal(4, preview.SafeProposals.Count);
        var conflict = Assert.Single(preview.Conflicts);
        Assert.Equal(QuestProgressEntityKind.Objective, conflict.EntityKind);
        Assert.Single(preview.Unresolved);
        Assert.Empty(preview.Ignored);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Exchange.ApplyImportAsync(
            preview,
            new Dictionary<string, QuestImportResolution>(),
            CancellationToken.None));
        var result = await context.Exchange.ApplyImportAsync(
            preview,
            new Dictionary<string, QuestImportResolution>
            {
                [conflict.Key] = QuestImportResolution.UseIncoming,
            },
            CancellationToken.None);
        var applied = await context.Store.GetAsync(context.Scope, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(5, result.AppliedChangeCount);
        Assert.Equal(RecordedTaskState.Completed, applied.Tasks["task-contract"].State);
        Assert.Equal(1, applied.Objectives["objective-find-item"].Count);
        Assert.Equal(RecordedObjectiveState.Completed, applied.Objectives["objective-mark"].State);
        Assert.Equal(7, applied.ItemHoldings.Single(value => value.FoundInRaid).Count);
        Assert.Equal(2, applied.ItemHoldings.Single(value => !value.FoundInRaid).Count);
        Assert.Single(applied.Pins);
        Assert.DoesNotContain("unknown-task", applied.Tasks.Keys);

        var repeated = await context.Exchange.ApplyImportAsync(
            preview,
            new Dictionary<string, QuestImportResolution>
            {
                [conflict.Key] = QuestImportResolution.UseIncoming,
            },
            CancellationToken.None);
        Assert.True(repeated.AlreadyApplied);
        Assert.Equal(result.ImportId, repeated.ImportId);
        Assert.Equal(applied.Revision, (await context.Store.GetAsync(context.Scope, CancellationToken.None)).Revision);

        var undo = await context.Exchange.UndoImportAsync(
            context.Scope, result.ImportId, CancellationToken.None);
        var restored = await context.Store.GetAsync(context.Scope, CancellationToken.None);

        Assert.True(undo.Changed);
        Assert.Equal(5, undo.RestoredChangeCount);
        Assert.Equal(beforeImport.Tasks["task-contract"].State, restored.Tasks["task-contract"].State);
        Assert.Equal(beforeImport.Objectives["objective-find-item"].State, restored.Objectives["objective-find-item"].State);
        Assert.Equal(beforeImport.Objectives["objective-find-item"].Count, restored.Objectives["objective-find-item"].Count);
        Assert.Equal(RecordedObjectiveState.Completed, restored.Objectives["objective-mark"].State);
        Assert.Equal(5, Assert.Single(restored.ItemHoldings).Count);
        Assert.Empty(restored.Pins);
        var journal = await context.Store.GetJournalAsync(context.Scope, CancellationToken.None);
        Assert.Equal(14, journal.Count);
        Assert.Equal(5, journal.Count(value => value.CorrelationId == result.ImportId));
        Assert.Equal(5, journal.Count(value => value.CorrelationId == undo.UndoCorrelationId));
        Assert.All(journal.Where(value => value.CorrelationId == undo.UndoCorrelationId), value =>
            Assert.Equal(QuestProgressActor.Import, value.Actor));

        await using var connection = await context.Factory.OpenAsync(CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM quest_progress_imports;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM quest_progress_import_conflicts;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM quest_progress_import_unresolved;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM quest_progress_import_undos;"));
    }

    [Fact]
    public async Task StaleTamperedWrongScopeAndJournalFailureLeaveDatabaseUnchanged()
    {
        await using var context = await ExchangeContext.CreateAsync();
        var importPath = await WriteIncomingAsync(
            context,
            tasks: [new("task-contract", RecordedTaskState.Completed)]);
        var preview = await context.Exchange.PreviewImportAsync(context.Scope, importPath, CancellationToken.None);
        var tamperedProposal = preview.Proposals[0] with
        {
            IncomingValue = new QuestImportValue(TaskState: RecordedTaskState.NotStarted),
        };
        var tampered = preview with { Proposals = [tamperedProposal] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Exchange.ApplyImportAsync(
            tampered,
            new Dictionary<string, QuestImportResolution>(),
            CancellationToken.None));

        await context.Store.ApplyAsync(TaskMutation(context.Scope, RecordedTaskState.Active), CancellationToken.None);
        var staleState = await context.Store.GetAsync(context.Scope, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Exchange.ApplyImportAsync(
            preview,
            new Dictionary<string, QuestImportResolution>(),
            CancellationToken.None));
        var afterStale = await context.Store.GetAsync(context.Scope, CancellationToken.None);
        Assert.Equal(staleState.Revision, afterStale.Revision);
        Assert.Equal(staleState.Tasks["task-contract"], afterStale.Tasks["task-contract"]);
        Assert.Empty(afterStale.Objectives);
        Assert.Empty(afterStale.ItemHoldings);
        Assert.Empty(afterStale.Pins);

        var wrongModeProfile = context.Profile with { GameMode = GameMode.Pve };
        var wrongModePath = Path.Combine(context.Directory, "wrong-mode.json");
        await context.Json.WriteAsync(
            wrongModePath,
            wrongModeProfile,
            EmptyProgress(wrongModeProfile, [new("task-contract", RecordedTaskState.Completed)]),
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Exchange.PreviewImportAsync(
            context.Scope, wrongModePath, CancellationToken.None));

        var wrongGenerationProfile = context.Profile with { ProfileGeneration = "other-generation" };
        var wrongGenerationPath = Path.Combine(context.Directory, "wrong-generation.json");
        await context.Json.WriteAsync(
            wrongGenerationPath,
            wrongGenerationProfile,
            EmptyProgress(wrongGenerationProfile, [new("task-contract", RecordedTaskState.Completed)]),
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Exchange.PreviewImportAsync(
            context.Scope, wrongGenerationPath, CancellationToken.None));

        await using (var connection = await context.Factory.OpenAsync(CancellationToken.None))
        {
            await ExecuteAsync(connection, """
                CREATE TRIGGER test_reject_import_journal
                BEFORE INSERT ON quest_progress_journal
                WHEN NEW.assertion_source = 'Project JSON v2 import'
                BEGIN
                    SELECT RAISE(ABORT, 'forced import journal failure');
                END;
                """);
        }

        var freshPath = await WriteIncomingAsync(
            context,
            tasks: [new("task-contract", RecordedTaskState.Completed)],
            holdings: [new("item-a", true, 1)]);
        var fresh = await context.Exchange.PreviewImportAsync(context.Scope, freshPath, CancellationToken.None);
        await Assert.ThrowsAsync<SqliteException>(() => context.Exchange.ApplyImportAsync(
            fresh,
            new Dictionary<string, QuestImportResolution>(),
            CancellationToken.None));
        var afterFailure = await context.Store.GetAsync(context.Scope, CancellationToken.None);
        Assert.Equal(staleState.Revision, afterFailure.Revision);
        Assert.Equal(staleState.Tasks["task-contract"], afterFailure.Tasks["task-contract"]);
        Assert.Empty(afterFailure.Objectives);
        Assert.Empty(afterFailure.ItemHoldings);
        Assert.Empty(afterFailure.Pins);
        await using var verification = await context.Factory.OpenAsync(CancellationToken.None);
        Assert.Equal(0L, await ScalarAsync(verification, "SELECT COUNT(*) FROM quest_progress_imports;"));
    }

    [Fact]
    public async Task LegacyProfileSchemaOnePreservesOnlyExplicitAssertionsInItsExactGeneration()
    {
        const string profileId = "940d35d5-47a2-4a25-afb9-94145166d65b";
        await using var context = await ExchangeContext.CreateAsync(
            $"legacy-{Guid.Parse(profileId):N}");
        var path = Path.Combine(context.Directory, "profile-v1.json");
        var legacy = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["profile"] = new JsonObject
            {
                ["id"] = context.Profile.Id.ToString("D"),
                ["name"] = context.Profile.Name,
                ["gameMode"] = "regular",
                ["completedTaskIds"] = new JsonArray("task-contract"),
                ["objectiveProgress"] = new JsonObject { ["objective-find-item"] = 99 },
                ["ownedItemCounts"] = new JsonObject { ["item-a"] = 4 },
            },
            ["exportedUtc"] = Now.ToString("O"),
        };
        await File.WriteAllTextAsync(path, legacy.ToJsonString());

        var preview = await context.Exchange.PreviewImportAsync(context.Scope, path, CancellationToken.None);

        Assert.True(preview.IsLegacyProfileSettingsEnvelope);
        Assert.Equal(3, preview.SafeProposals.Count);
        Assert.Empty(preview.Conflicts);
        var applied = await context.Exchange.ApplyImportAsync(
            preview,
            new Dictionary<string, QuestImportResolution>(),
            CancellationToken.None);
        var progress = await context.Store.GetAsync(context.Scope, CancellationToken.None);

        Assert.Equal(3, applied.AppliedChangeCount);
        Assert.Equal(RecordedTaskState.Completed, Assert.Single(progress.Tasks).Value.State);
        Assert.Equal(RecordedObjectiveState.InProgress, Assert.Single(progress.Objectives).Value.State);
        Assert.Equal(99, Assert.Single(progress.Objectives).Value.Count);
        var holding = Assert.Single(progress.ItemHoldings);
        Assert.False(holding.FoundInRaid);
        Assert.Equal(4, holding.Count);
        Assert.Empty(progress.Pins);
        Assert.All(
            await context.Store.GetJournalAsync(context.Scope, CancellationToken.None),
            value => Assert.Equal("Project profile JSON v1 compatibility import", value.Source));
        await using var connection = await context.Factory.OpenAsync(CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM quest_progress_imports;"));
    }

    [Fact]
    public async Task LegacyProfileSchemaOneRefusesANonLegacyActiveGeneration()
    {
        await using var context = await ExchangeContext.CreateAsync();
        var path = Path.Combine(context.Directory, "profile-v1-wrong-generation.json");
        var legacy = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["profile"] = new JsonObject
            {
                ["id"] = context.Profile.Id.ToString("D"),
                ["name"] = context.Profile.Name,
                ["gameMode"] = "regular",
                ["completedTaskIds"] = new JsonArray("task-contract"),
                ["objectiveProgress"] = new JsonObject(),
                ["ownedItemCounts"] = new JsonObject(),
            },
        };
        await File.WriteAllTextAsync(path, legacy.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Exchange.PreviewImportAsync(context.Scope, path, CancellationToken.None));

        Assert.Contains("generation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty((await context.Store.GetAsync(context.Scope, CancellationToken.None)).Tasks);
    }

    private static async Task AssertInvalidMutationAsync(
        ExchangeContext context,
        JsonObject valid,
        string fileName,
        Action<JsonObject> mutate,
        string expectedMessage)
    {
        var root = valid.DeepClone().AsObject();
        mutate(root);
        var path = Path.Combine(context.Directory, fileName);
        await File.WriteAllTextAsync(path, root.ToJsonString());
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            context.Json.ReadAsync(path, CancellationToken.None));
        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> WriteIncomingAsync(
        ExchangeContext context,
        IReadOnlyList<ProjectQuestProgressTask>? tasks = null,
        IReadOnlyList<ProjectQuestProgressObjective>? objectives = null,
        IReadOnlyList<ProjectQuestProgressHolding>? holdings = null,
        IReadOnlyList<ProjectQuestProgressPin>? pins = null)
    {
        var path = Path.Combine(context.Directory, $"import-{Guid.NewGuid():N}.json");
        await context.Json.WriteAsync(
            path,
            context.Profile,
            EmptyProgress(context.Profile, tasks, objectives, holdings, pins),
            CancellationToken.None);
        return path;
    }

    private static QuestProgressSnapshot EmptyProgress(
        PlayerProfile profile,
        IReadOnlyList<ProjectQuestProgressTask>? tasks = null,
        IReadOnlyList<ProjectQuestProgressObjective>? objectives = null,
        IReadOnlyList<ProjectQuestProgressHolding>? holdings = null,
        IReadOnlyList<ProjectQuestProgressPin>? pins = null)
    {
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        return new(
            scope,
            0,
            (tasks ?? []).ToDictionary(
                value => value.TaskId,
                value => new RecordedTaskProgress(value.TaskId, value.State, "fixture", 0, Now),
                StringComparer.Ordinal),
            (objectives ?? []).ToDictionary(
                value => value.ObjectiveId,
                value => new RecordedObjectiveProgress(value.ObjectiveId, value.State, value.Count, "fixture", 0, Now),
                StringComparer.Ordinal),
            (holdings ?? []).Select(value =>
                new RecordedItemHolding(value.ItemId, value.FoundInRaid, value.Count, "fixture", 0, Now)).ToArray(),
            (pins ?? []).Select(value =>
                new RecordedQuestPin(value.TargetKind, value.TargetId, value.SortOrder, value.Note, "fixture", 0, Now)).ToArray());
    }

    private static SetTaskStateMutation TaskMutation(
        QuestProfileScope scope,
        RecordedTaskState state) => new(
            scope,
            "Quest profile",
            "task-contract",
            state,
            QuestProgressActor.User,
            "Manual",
            Guid.NewGuid(),
            Now);

    private static SetItemHoldingMutation HoldingMutation(
        QuestProfileScope scope,
        bool foundInRaid,
        int count) => new(
            scope,
            "Quest profile",
            "item-a",
            foundInRaid,
            count,
            QuestProgressActor.User,
            "Manual",
            Guid.NewGuid(),
            Now);

    private static SetQuestPinMutation PinMutation(
        QuestProfileScope scope,
        QuestPinTargetKind kind,
        string id,
        string? note) => new(
            scope,
            "Quest profile",
            kind,
            id,
            true,
            4,
            note,
            QuestProgressActor.User,
            "Manual",
            Guid.NewGuid(),
            Now);

    private static async Task<QuestCatalogSnapshot> CatalogAsync()
    {
        var json = await FixtureJson.ReadAsync("tasks-contract.json");
        var envelope = JsonSerializer.Deserialize<TarkovDevEnvelope<TarkovDevTasksData>>(json, ApiSerializerOptions)
            ?? throw new InvalidDataException("Quest fixture was null.");
        var response = new TarkovDevResponse<TarkovDevTasksData>(
            envelope.Data,
            json,
            Now,
            false,
            false,
            "\"stage-4\"",
            Now,
            json);
        return new TarkovDevQuestCatalogNormalizer().Normalize(response, GameMode.Regular, "en", Now);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string TemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-quest-exchange-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class ExchangeContext : IAsyncDisposable
    {
        private readonly JsonFilePlayerProfileService _profileService;

        private ExchangeContext(
            string directory,
            SqliteConnectionFactory factory,
            PlayerProfile profile,
            JsonFilePlayerProfileService profileService,
            SqliteQuestProgressStore store,
            ProjectQuestProgressJson json,
            QuestProgressExchangeService exchange)
        {
            Directory = directory;
            Factory = factory;
            Profile = profile;
            _profileService = profileService;
            Store = store;
            Json = json;
            Exchange = exchange;
        }

        public string Directory { get; }

        public SqliteConnectionFactory Factory { get; }

        public PlayerProfile Profile { get; }

        public QuestProfileScope Scope => new(Profile.Id, Profile.GameMode, Profile.ProfileGeneration);

        public SqliteQuestProgressStore Store { get; }

        public ProjectQuestProgressJson Json { get; }

        public QuestProgressExchangeService Exchange { get; }

        public static async Task<ExchangeContext> CreateAsync(string profileGeneration = "generation-a")
        {
            var directory = TemporaryDirectory();
            var factory = new SqliteConnectionFactory(new(Path.Combine(directory, "progress.db")));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            var profile = new PlayerProfile(
                Guid.Parse("940d35d5-47a2-4a25-afb9-94145166d65b"),
                "Quest profile",
                GameMode.Regular,
                25,
                Faction.Usec,
                null,
                new Dictionary<string, int>(),
                new HashSet<string>(),
                new Dictionary<string, int>(),
                new Dictionary<string, int>(),
                new HashSet<string>(),
                new Dictionary<string, int>(),
                new Dictionary<string, EventItemState>(),
                new Dictionary<string, string>(),
                Now,
                profileGeneration);
            var profileService = new JsonFilePlayerProfileService(
                new(Path.Combine(directory, "profile.json")),
                new ManualTimeProvider(Now));
            await profileService.SaveAsync(profile, CancellationToken.None);
            var store = new SqliteQuestProgressStore(factory);
            var importStore = new SqliteQuestProgressImportStore(factory, new ManualTimeProvider(Now));
            var json = new ProjectQuestProgressJson(new("1.2.3-test"), new ManualTimeProvider(Now));
            var exchange = new QuestProgressExchangeService(
                profileService,
                new FixedCatalog(await CatalogAsync()),
                store,
                importStore,
                json,
                new());
            return new(directory, factory, profile, profileService, store, json, exchange);
        }

        public ValueTask DisposeAsync()
        {
            _profileService.Dispose();
            SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedCatalog(QuestCatalogSnapshot catalog) : IQuestCatalog
    {
        public Task<QuestCatalogSnapshot?> GetAsync(
            GameMode gameMode,
            string language,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<QuestCatalogSnapshot?>(
                catalog.Provenance.GameMode == gameMode && catalog.Provenance.Language == language ? catalog : null);
        }
    }
}
