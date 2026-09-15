using System.Text.Json;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Runtime;

/// <summary>One raid-history write, from a closed set of typed commands.</summary>
/// <remarks>
/// <para>
/// Raid events used to reach the outbox as a type name and a JSON string. The outbox wrapped
/// that string inside its own typed payload, so whatever a caller serialized — a screenshot
/// filename, exact coordinates — travelled through a boundary whose generic payload rules exist
/// precisely to refuse those fields.
/// </para>
/// <para>
/// A command now carries the domain value it records, and the outbox encodes each kind with its
/// own reviewed codec. A direct v1 store is written through <see cref="WriteAsync"/>, which
/// produces exactly the JSON the coordinator wrote before, so both paths store the same rows.
/// </para>
/// </remarks>
public abstract class RaidHistoryCommand
{
    private RaidHistoryCommand(Guid raidId)
    {
        if (raidId == Guid.Empty)
        {
            throw new ArgumentException("A raid id is required.", nameof(raidId));
        }

        RaidId = raidId;
    }

    public Guid RaidId { get; }

    public static RaidHistoryCommand StartRaid(RaidHistoryEntry raid) => new Started(raid);

    public static RaidHistoryCommand RecordState(Guid raidId, RaidEvidence evidence) => new StateRecorded(raidId, evidence);

    public static RaidHistoryCommand RecordExtracts(
        Guid raidId,
        DateTimeOffset observedUtc,
        IReadOnlyList<ActiveExtract> extracts) =>
        new ExtractsRecorded(raidId, observedUtc, extracts);

    public static RaidHistoryCommand RecordScan(Guid raidId, ScanExecutionResult result) => new ScanRecorded(raidId, result);

    public static RaidHistoryCommand RecordSale(Guid raidId, FleaSaleObservation sale) => new SaleRecorded(raidId, sale);

    public static RaidHistoryCommand RecordQuest(Guid raidId, QuestStatusObservation quest) => new QuestRecorded(raidId, quest);

    public static RaidHistoryCommand RecordPosition(Guid raidId, ScreenshotPosition position) =>
        new PositionRecorded(raidId, position);

    public static RaidHistoryCommand EndRaid(Guid raidId, DateTimeOffset endUtc, string? outcome, string? notes) =>
        new Ended(raidId, endUtc, outcome, notes);

    /// <summary>Writes this command to a store directly, as the v1 coordinator recorded it.</summary>
    public abstract Task WriteAsync(IRaidHistoryService target, CancellationToken cancellationToken);

    internal sealed class Started : RaidHistoryCommand
    {
        public Started(RaidHistoryEntry raid)
            : base((raid ?? throw new ArgumentNullException(nameof(raid))).Id)
        {
            Raid = raid;
        }

        public RaidHistoryEntry Raid { get; }

        public override Task WriteAsync(IRaidHistoryService target, CancellationToken cancellationToken) =>
            target.StartAsync(Raid, cancellationToken);
    }

    internal sealed class StateRecorded : RaidHistoryCommand
    {
        public StateRecorded(Guid raidId, RaidEvidence evidence)
            : base(raidId)
        {
            Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        }

        public RaidEvidence Evidence { get; }

        public override Task WriteAsync(IRaidHistoryService target, CancellationToken cancellationToken) =>
            target.RecordEventAsync(
                RaidId,
                "state",
                Evidence.ObservedUtc,
                JsonSerializer.Serialize(Evidence),
                cancellationToken);
    }

    internal sealed class ExtractsRecorded : RaidHistoryCommand
    {
        public ExtractsRecorded(Guid raidId, DateTimeOffset observedUtc, IReadOnlyList<ActiveExtract> extracts)
            : base(raidId)
        {
            ArgumentNullException.ThrowIfNull(extracts);
            if (extracts.Any(extract => extract is null))
            {
                throw new ArgumentException("Extracts cannot contain null.", nameof(extracts));
            }

            ObservedUtc = observedUtc;
            Extracts = [.. extracts];
        }

        public DateTimeOffset ObservedUtc { get; }

        public IReadOnlyList<ActiveExtract> Extracts { get; }

        public override Task WriteAsync(IRaidHistoryService target, CancellationToken cancellationToken) =>
            target.RecordEventAsync(
                RaidId,
                "extracts",
                ObservedUtc,
                JsonSerializer.Serialize(Extracts),
                cancellationToken);
    }

    internal sealed class ScanRecorded : RaidHistoryCommand
    {
        public ScanRecorded(Guid raidId, ScanExecutionResult result)
            : base(raidId)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        public ScanExecutionResult Result { get; }

        public override Task WriteAsync(IRaidHistoryService target, CancellationToken cancellationToken) =>
            target.RecordEventAsync(
                RaidId,
                "scan",
                Result.ObservedUtc,
                JsonSerializer.Serialize(Result),
                cancellationToken);
    }

    internal sealed class SaleRecorded : RaidHistoryCommand
    {
        public SaleRecorded(Guid raidId, FleaSaleObservation sale)
            : base(raidId)
        {
            Sale = sale ?? throw new ArgumentNullException(nameof(sale));
        }

        public FleaSaleObservation Sale { get; }

        public override Task WriteAsync(IRaidHistoryService target, CancellationToken cancellationToken) =>
            target.RecordEventAsync(
                RaidId,
                "sale",
                Sale.ObservedUtc,
                JsonSerializer.Serialize(Sale),
                cancellationToken);
    }

    internal sealed class QuestRecorded : RaidHistoryCommand
    {
        public QuestRecorded(Guid raidId, QuestStatusObservation quest)
            : base(raidId)
        {
            Quest = quest ?? throw new ArgumentNullException(nameof(quest));
        }

        public QuestStatusObservation Quest { get; }

        public override Task WriteAsync(IRaidHistoryService target, CancellationToken cancellationToken) =>
            target.RecordEventAsync(
                RaidId,
                "quest",
                Quest.ObservedUtc,
                JsonSerializer.Serialize(Quest),
                cancellationToken);
    }

    internal sealed class PositionRecorded : RaidHistoryCommand
    {
        public PositionRecorded(Guid raidId, ScreenshotPosition position)
            : base(raidId)
        {
            Position = position ?? throw new ArgumentNullException(nameof(position));
        }

        public ScreenshotPosition Position { get; }

        public override Task WriteAsync(IRaidHistoryService target, CancellationToken cancellationToken) =>
            target.RecordEventAsync(
                RaidId,
                "position",
                Position.Timestamp,
                JsonSerializer.Serialize(Position),
                cancellationToken);
    }

    internal sealed class Ended : RaidHistoryCommand
    {
        public Ended(Guid raidId, DateTimeOffset endUtc, string? outcome, string? notes)
            : base(raidId)
        {
            EndUtc = endUtc;
            Outcome = outcome;
            Notes = notes;
        }

        public DateTimeOffset EndUtc { get; }

        public string? Outcome { get; }

        public string? Notes { get; }

        public override Task WriteAsync(IRaidHistoryService target, CancellationToken cancellationToken) =>
            target.EndAsync(RaidId, EndUtc, Outcome, Notes, cancellationToken);
    }
}
