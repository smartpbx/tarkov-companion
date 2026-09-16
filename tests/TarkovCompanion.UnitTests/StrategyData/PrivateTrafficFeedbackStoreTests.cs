using System.Text;
using System.Text.Json.Nodes;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Strategy.Data;
using TarkovCompanion.Infrastructure.Strategy.Datasets;

namespace TarkovCompanion.UnitTests.StrategyData;

public sealed class PrivateTrafficFeedbackStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 16, 0, 0, TimeSpan.Zero);
    private static readonly TrafficCompatibilityScope Scope = new(
        "customs", "0.16.9", ProfileGameMode.Pvp, "wipe-2026-2", "all-players");
    private static readonly TrafficSpatialReference Location = new("customs", regionId: "dorms");
    private static readonly TrafficPhaseWindow Window = new(RaidPhase.Mid, 900, 1_800);

    [Fact]
    public async Task CorrectionsAndRevocationRemainAppendOnlyAcrossRestart()
    {
        var directory = Directory.CreateTempSubdirectory("traffic-feedback-");
        var path = Path.Combine(directory.FullName, "private-feedback.json");
        var id = Guid.NewGuid();
        try
        {
            using (var store = await PrivateTrafficFeedbackStore.OpenAsync(path))
            {
                _ = await store.SubmitAsync(
                    Feedback(id, TrafficObservationClass.Contact),
                    TrafficFeedbackActor.LocalUser,
                    Now);
                _ = await store.CorrectAsync(
                    Feedback(id, TrafficObservationClass.Avoided),
                    1,
                    TrafficFeedbackActor.LocalUser,
                    Now.AddMinutes(1));
                var revoked = await store.RevokeAsync(
                    id,
                    2,
                    TrafficFeedbackActor.LocalConsentPolicy,
                    Now.AddMinutes(2));

                Assert.True(revoked.IsRevoked);
                Assert.Null(revoked.Current);
            }

            using var reopened = await PrivateTrafficFeedbackStore.OpenAsync(path);
            var history = Assert.IsType<TrafficFeedbackHistory>(await reopened.ReadAsync(id));

            Assert.Equal(3, history.Events.Count);
            Assert.Equal(TrafficObservationClass.Contact, history.Events[0].Value!.Observation);
            Assert.Equal(TrafficObservationClass.Avoided, history.Events[1].Value!.Observation);
            Assert.True(history.IsRevoked);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DeleteRemovesTheCompleteHistoryAndLeavesNoTombstone()
    {
        var directory = Directory.CreateTempSubdirectory("traffic-feedback-");
        var path = Path.Combine(directory.FullName, "private-feedback.json");
        var id = Guid.NewGuid();
        try
        {
            using (var store = await PrivateTrafficFeedbackStore.OpenAsync(path))
            {
                _ = await store.SubmitAsync(
                    Feedback(id, TrafficObservationClass.Unknown),
                    TrafficFeedbackActor.LocalUser,
                    Now);
                Assert.True(await store.DeleteAsync(id, 1));
                Assert.Null(await store.ReadAsync(id));
            }

            var persisted = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(id.ToString(), persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prediction-private-17", persisted, StringComparison.Ordinal);

            using var reopened = await PrivateTrafficFeedbackStore.OpenAsync(path);
            Assert.Null(await reopened.ReadAsync(id));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentCorrectionsUseExpectedRevisionInsteadOfSilentlyOverwriting()
    {
        var directory = Directory.CreateTempSubdirectory("traffic-feedback-");
        var path = Path.Combine(directory.FullName, "private-feedback.json");
        var id = Guid.NewGuid();
        try
        {
            using var store = await PrivateTrafficFeedbackStore.OpenAsync(path);
            _ = await store.SubmitAsync(
                Feedback(id, TrafficObservationClass.Contact),
                TrafficFeedbackActor.LocalUser,
                Now);
            var attempts = new[]
            {
                store.CorrectAsync(
                    Feedback(id, TrafficObservationClass.NoContact),
                    1,
                    TrafficFeedbackActor.LocalUser,
                    Now.AddMinutes(1)).AsTask(),
                store.CorrectAsync(
                    Feedback(id, TrafficObservationClass.Avoided),
                    1,
                    TrafficFeedbackActor.LocalUser,
                    Now.AddMinutes(2)).AsTask(),
            };

            var accepted = await Task.WhenAll(attempts.Select(async attempt =>
            {
                try
                {
                    _ = await attempt;
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }));
            var history = Assert.IsType<TrafficFeedbackHistory>(await store.ReadAsync(id));

            Assert.Equal(1, accepted.Count(value => value));
            Assert.Equal(2, history.Events.Count);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HostileUnknownAndDuplicateFieldsFailClosedOnRestart()
    {
        var directory = Directory.CreateTempSubdirectory("traffic-feedback-");
        var path = Path.Combine(directory.FullName, "private-feedback.json");
        try
        {
            using (var store = await PrivateTrafficFeedbackStore.OpenAsync(path))
            {
                _ = await store.SubmitAsync(
                    Feedback(Guid.NewGuid(), TrafficObservationClass.NoContact),
                    TrafficFeedbackActor.LocalUser,
                    Now);
            }

            var original = await File.ReadAllTextAsync(path);
            var hostile = JsonNode.Parse(original)!;
            hostile["histories"]![0]!["events"]![0]!["value"]!["playerId"] = "prohibited-identity";
            await File.WriteAllTextAsync(path, hostile.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await PrivateTrafficFeedbackStore.OpenAsync(path));

            var duplicate = original.Replace(
                "{\"formatVersion\":1,",
                "{\"formatVersion\":1,\"formatVersion\":1,",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, duplicate);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await PrivateTrafficFeedbackStore.OpenAsync(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OversizeJournalAndSecondWriterAreRefused()
    {
        var directory = Directory.CreateTempSubdirectory("traffic-feedback-");
        var path = Path.Combine(directory.FullName, "private-feedback.json");
        try
        {
            using (var first = await PrivateTrafficFeedbackStore.OpenAsync(path))
            {
                await Assert.ThrowsAsync<IOException>(async () =>
                    await PrivateTrafficFeedbackStore.OpenAsync(path));
            }

            await File.WriteAllBytesAsync(path, new byte[PrivateTrafficFeedbackStore.MaximumDocumentBytes + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await PrivateTrafficFeedbackStore.OpenAsync(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static HistoricalTrafficFeedback Feedback(Guid id, TrafficObservationClass observation) => new(
        id,
        TrafficDataBounds.LocalFeedbackSourceId,
        new TrafficContributionConsent(
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "traffic-notice-v1",
            Now.AddDays(-2),
            [TrafficDataAllowedUse.AggregateContribution]),
        Scope,
        Location,
        Window,
        observation,
        new EvidenceProvenance(
            EvidenceSourceClass.UserEntered,
            TrafficDataBounds.LocalFeedbackSourceId,
            Now.AddMinutes(-1),
            EvidenceConfidence.Certain,
            new ProducerIdentity("traffic-feedback", "1")),
        Now,
        "prediction-private-17",
        "traffic-model-4");

}
