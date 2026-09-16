using TarkovCompanion.Application.Services.Intelligence.Keys;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Keys;

namespace TarkovCompanion.UnitTests.Keys;

public sealed class KeyInventoryCorrectionServiceTests
{
    private static readonly DateTimeOffset ObservedUtc =
        new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CorrectionAndReversalAreAttributableAndKeepRawObservation()
    {
        var service = new KeyInventoryCorrectionService();
        var current = Inventory();
        var corrected = service.Apply(current, new KeyInventoryCorrectionCommand(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            current.ProfileScope,
            current.ItemId,
            KeyInventoryCorrectionTarget.RemainingUses,
            10,
            6,
            ObservedUtc.AddMinutes(1),
            CorrectionOriginClass.User,
            "profile-editor",
            "Read the counter again."));

        var reversed = service.ReverseLast(
            corrected,
            current.ProfileScope,
            current.ItemId,
            KeyInventoryCorrectionTarget.RemainingUses,
            ObservedUtc.AddMinutes(2),
            CorrectionOriginClass.User,
            "profile-editor");

        Assert.Equal(10, reversed.RemainingUses.RecognizedValue);
        Assert.Equal(10, reversed.RemainingUses.Value);
        Assert.Equal(2, reversed.RemainingUses.Corrections.Count);
        Assert.Equal("profile-editor", reversed.RemainingUses.Corrections[0].OriginIdentifier);
        Assert.Equal(6, reversed.RemainingUses.Corrections[1].OriginalValue);
        Assert.Equal(10, reversed.RemainingUses.Corrections[1].CorrectedValue);
    }

    [Fact]
    public void CorrectionCannotCrossProfileGenerationOrBreakUseBounds()
    {
        var service = new KeyInventoryCorrectionService();
        var current = Inventory();
        var otherScope = new InventoryProfileScope(
            current.ProfileScope.ProfileId,
            "other-wipe",
            current.ProfileScope.GameMode);
        var wrongScope = new KeyInventoryCorrectionCommand(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            otherScope,
            current.ItemId,
            KeyInventoryCorrectionTarget.RemainingUses,
            10,
            6,
            ObservedUtc.AddMinutes(1),
            CorrectionOriginClass.User,
            "profile-editor");
        Assert.Throws<InvalidOperationException>(() => service.Apply(current, wrongScope));

        var impossibleUses = new KeyInventoryCorrectionCommand(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            current.ProfileScope,
            current.ItemId,
            KeyInventoryCorrectionTarget.RemainingUses,
            10,
            41,
            ObservedUtc.AddMinutes(1),
            CorrectionOriginClass.User,
            "profile-editor");
        Assert.Throws<ArgumentException>(() => service.Apply(current, impossibleUses));
        Assert.Equal(10, current.RemainingUses.Value);
        Assert.Empty(current.RemainingUses.Corrections);
    }

    [Fact]
    public void StaleCorrectionCommandCannotOverwriteNewerReview()
    {
        var service = new KeyInventoryCorrectionService();
        var current = Inventory();
        var first = service.Apply(current, new KeyInventoryCorrectionCommand(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            current.ProfileScope,
            current.ItemId,
            KeyInventoryCorrectionTarget.RemainingUses,
            10,
            8,
            ObservedUtc.AddMinutes(1),
            CorrectionOriginClass.User,
            "reviewer"));
        var stale = new KeyInventoryCorrectionCommand(
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            current.ProfileScope,
            current.ItemId,
            KeyInventoryCorrectionTarget.RemainingUses,
            10,
            4,
            ObservedUtc.AddMinutes(2),
            CorrectionOriginClass.User,
            "reviewer");

        Assert.Throws<InvalidOperationException>(() => service.Apply(first, stale));
        Assert.Equal(8, first.RemainingUses.Value);
    }

    [Fact]
    public void UnresolvedRawFactCannotEnterAOneWayCorrectionPath()
    {
        var current = Inventory();
        var unknownRemaining = new EvidencedValue<int?>(
            "remaining-uses",
            null,
            new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Unknown),
            current.RemainingUses.Provenance);
        var unresolved = new KeyInventoryFacts(
            current.ProfileScope,
            current.ItemId,
            current.TotalOwned,
            current.FoundInRaidOwned,
            current.DuplicateQuantity,
            current.MaximumUses,
            unknownRemaining);
        var command = new KeyInventoryCorrectionCommand(
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            current.ProfileScope,
            current.ItemId,
            KeyInventoryCorrectionTarget.RemainingUses,
            null,
            5,
            ObservedUtc.AddMinutes(1),
            CorrectionOriginClass.User,
            "reviewer");

        var error = Assert.Throws<InvalidOperationException>(() =>
            new KeyInventoryCorrectionService().Apply(unresolved, command));

        Assert.Contains("reversible", error.Message, StringComparison.Ordinal);
        Assert.Null(unresolved.RemainingUses.Value);
        Assert.Empty(unresolved.RemainingUses.Corrections);
    }

    private static KeyInventoryFacts Inventory()
    {
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "fixture screenshot",
            ObservedUtc,
            new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9),
            new ProducerIdentity("fixture", "1"));
        var status = new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current);
        EvidencedValue<int?> Fact(string id, int value) => new(id, value, status, provenance);
        return new KeyInventoryFacts(
            new InventoryProfileScope(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "wipe-1",
                "regular"),
            "key-1",
            Fact("total", 2),
            Fact("fir", 1),
            Fact("duplicates", 1),
            Fact("maximum-uses", 40),
            Fact("remaining-uses", 10));
    }
}
