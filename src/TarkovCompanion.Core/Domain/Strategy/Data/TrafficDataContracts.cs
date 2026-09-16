using System.Text.Json.Serialization;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Profiles;
using V2RaidPhase = TarkovCompanion.Core.Abstractions.V2.RaidPhase;

namespace TarkovCompanion.Core.Domain.Strategy.Data;

public static class TrafficDataBounds
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumSources = 64;
    public const int MaximumScopes = 256;
    public const int MaximumRecords = 100_000;
    public const int MaximumCoverageGaps = 1_024;
    public const int MaximumAllowedUses = 8;
    public const int MaximumTextLength = 256;
    public const int MaximumDescriptionLength = 1_024;
    public const int MaximumRaidElapsedSeconds = 3_600;
    public const long MaximumSampleCount = 1_000_000_000;
    public const long MinimumDistributableSampleCount = 5;
    public const int PartitionBasisPoints = 10_000;
    public const int MaximumFeedbackEvents = 1_024;
    public const string LocalFeedbackSourceId = "local-traffic-feedback";
}

public enum TrafficDataSourceKind
{
    StaticPublicFacts = 1,
    ReviewedCuratedKnowledge,
    HistoricalAggregate,
    PrivateLocalFeedback,
}

public enum TrafficDataAllowedUse
{
    HistoricalLayer = 1,
    RuntimeInference,
    RoutePlanning,
    ModelTraining,
    ModelTuning,
    HeldOutEvaluation,
    AggregateContribution,
    DistributableSnapshot,
}

public enum TrafficConsentBasis
{
    PublicDataTerms = 1,
    ReviewedTermsOrConsent,
    ExplicitLocalOptIn,
}

public enum TrafficDatasetVisibility
{
    PrivateLocal = 1,
    DistributableAggregate,
}

public enum TrafficDataPartition
{
    Train = 1,
    Tune,
    HeldOut,
}

public enum TrafficObservationClass
{
    Contact = 1,
    NoContact,
    Avoided,
    Unknown,
}

public enum TrafficFeedbackEventKind
{
    Submitted = 1,
    Corrected,
    Revoked,
}

public enum TrafficFeedbackActor
{
    LocalUser = 1,
    LocalConsentPolicy,
}

public sealed record TrafficContributionConsent
{
    public TrafficContributionConsent(
        Guid consentId,
        string noticeVersion,
        DateTimeOffset grantedUtc,
        IReadOnlyList<TrafficDataAllowedUse> allowedUses)
    {
        if (consentId == Guid.Empty)
        {
            throw new ArgumentException("A consent id is required.", nameof(consentId));
        }

        ConsentId = consentId;
        NoticeVersion = TrafficDataGuard.Token(noticeVersion, nameof(noticeVersion));
        GrantedUtc = TrafficDataGuard.Utc(grantedUtc, nameof(grantedUtc));
        AllowedUses = TrafficDataGuard.EnumList(
            allowedUses,
            nameof(allowedUses),
            TrafficDataBounds.MaximumAllowedUses,
            requireNonEmpty: true);
        if (!AllowedUses.Contains(TrafficDataAllowedUse.AggregateContribution))
        {
            throw new ArgumentException("Traffic feedback consent must explicitly allow aggregate contribution.", nameof(allowedUses));
        }

        if (AllowedUses.Contains(TrafficDataAllowedUse.DistributableSnapshot))
        {
            throw new ArgumentException("Raw private feedback cannot authorize snapshot distribution.", nameof(allowedUses));
        }

        if (AllowedUses.Any(use => use != TrafficDataAllowedUse.AggregateContribution))
        {
            throw new ArgumentException("Private feedback consent authorizes aggregate contribution only.", nameof(allowedUses));
        }
    }

    public Guid ConsentId { get; }

    public string NoticeVersion { get; }

    public DateTimeOffset GrantedUtc { get; }

    public IReadOnlyList<TrafficDataAllowedUse> AllowedUses { get; }
}

/// <summary>One exact map/game-version/mode/wipe/cohort compatibility cell. None is a wildcard.</summary>
public sealed record TrafficCompatibilityScope
{
    public TrafficCompatibilityScope(
        string mapId,
        string gameVersion,
        ProfileGameMode gameMode,
        string wipeId,
        string cohortId)
    {
        if (!Enum.IsDefined(gameMode) || gameMode == ProfileGameMode.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(gameMode));
        }

        MapId = TrafficDataGuard.ExactCompatibilityToken(mapId, nameof(mapId));
        GameVersion = TrafficDataGuard.ExactCompatibilityToken(gameVersion, nameof(gameVersion));
        GameMode = gameMode;
        WipeId = TrafficDataGuard.ExactCompatibilityToken(wipeId, nameof(wipeId));
        CohortId = TrafficDataGuard.ExactCompatibilityToken(cohortId, nameof(cohortId));
    }

    public string MapId { get; }

    public string GameVersion { get; }

    public ProfileGameMode GameMode { get; }

    public string WipeId { get; }

    public string CohortId { get; }
}

/// <summary>A bounded named region or route segment; exact entity/player coordinates cannot fit.</summary>
public sealed record TrafficSpatialReference
{
    public TrafficSpatialReference(string mapId, string? regionId = null, string? corridorId = null)
    {
        MapId = TrafficDataGuard.Token(mapId, nameof(mapId));
        RegionId = TrafficDataGuard.OptionalToken(regionId, nameof(regionId));
        CorridorId = TrafficDataGuard.OptionalToken(corridorId, nameof(corridorId));
        if ((RegionId is null) == (CorridorId is null))
        {
            throw new ArgumentException("Traffic location must name exactly one bounded region or route corridor.");
        }
    }

    public string MapId { get; }

    public string? RegionId { get; }

    public string? CorridorId { get; }
}

/// <summary>A phase and optional bounded elapsed-time window, never wall-clock/current-raid time.</summary>
public sealed record TrafficPhaseWindow
{
    public TrafficPhaseWindow(V2RaidPhase phase, int? startElapsedSeconds = null, int? endElapsedSeconds = null)
    {
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if ((startElapsedSeconds is null) != (endElapsedSeconds is null))
        {
            throw new ArgumentException("A traffic time window requires both bounds or neither.");
        }

        if (startElapsedSeconds is { } start && endElapsedSeconds is { } end &&
            (start < 0 || end <= start || end > TrafficDataBounds.MaximumRaidElapsedSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(startElapsedSeconds));
        }

        Phase = phase;
        StartElapsedSeconds = startElapsedSeconds;
        EndElapsedSeconds = endElapsedSeconds;
    }

    public V2RaidPhase Phase { get; }

    public int? StartElapsedSeconds { get; }

    public int? EndElapsedSeconds { get; }
}

/// <summary>The reviewed authority and permitted purpose of one dataset source.</summary>
public sealed record TrafficDatasetSource
{
    public TrafficDatasetSource(
        string sourceId,
        string displayName,
        TrafficDataSourceKind kind,
        TrafficDatasetVisibility visibility,
        EvidenceProvenance provenance,
        string licenseIdentifier,
        TrafficConsentBasis consentBasis,
        string collectionMethod,
        IReadOnlyList<TrafficDataAllowedUse> allowedUses,
        IReadOnlyList<TrafficCompatibilityScope> compatibilityScopes,
        DateTimeOffset reviewedUtc,
        string? reference = null)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(visibility) || !Enum.IsDefined(consentBasis))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(provenance);
        var normalizedSourceId = TrafficDataGuard.Token(sourceId, nameof(sourceId));
        var expectedSourceClass = kind switch
        {
            TrafficDataSourceKind.StaticPublicFacts => EvidenceSourceClass.PublicStructuredData,
            TrafficDataSourceKind.ReviewedCuratedKnowledge => EvidenceSourceClass.CuratedData,
            TrafficDataSourceKind.HistoricalAggregate => EvidenceSourceClass.HistoricalAggregate,
            TrafficDataSourceKind.PrivateLocalFeedback => EvidenceSourceClass.UserEntered,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (provenance.SourceClass != expectedSourceClass)
        {
            throw new ArgumentException($"{kind} requires {expectedSourceClass} provenance.", nameof(provenance));
        }

        if (kind == TrafficDataSourceKind.PrivateLocalFeedback && consentBasis != TrafficConsentBasis.ExplicitLocalOptIn)
        {
            throw new ArgumentException("Private local feedback requires explicit opt-in.", nameof(consentBasis));
        }

        if (kind == TrafficDataSourceKind.PrivateLocalFeedback && visibility != TrafficDatasetVisibility.PrivateLocal)
        {
            throw new ArgumentException("Private feedback source visibility must remain local.", nameof(visibility));
        }

        if (kind == TrafficDataSourceKind.PrivateLocalFeedback &&
            !string.Equals(normalizedSourceId, TrafficDataBounds.LocalFeedbackSourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Private feedback uses the fixed non-identifying local source id.", nameof(sourceId));
        }

        if (kind == TrafficDataSourceKind.PrivateLocalFeedback &&
            !string.Equals(provenance.SourceIdentifier, TrafficDataBounds.LocalFeedbackSourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Private feedback provenance uses the fixed non-identifying local source id.", nameof(provenance));
        }

        if (kind == TrafficDataSourceKind.StaticPublicFacts && consentBasis != TrafficConsentBasis.PublicDataTerms)
        {
            throw new ArgumentException("Public facts must name their public-data terms basis.", nameof(consentBasis));
        }

        if ((kind is TrafficDataSourceKind.ReviewedCuratedKnowledge or TrafficDataSourceKind.HistoricalAggregate) &&
            consentBasis != TrafficConsentBasis.ReviewedTermsOrConsent)
        {
            throw new ArgumentException("Curated and aggregate sources require reviewed terms or consent.", nameof(consentBasis));
        }

        var uses = TrafficDataGuard.EnumList(
            allowedUses,
            nameof(allowedUses),
            TrafficDataBounds.MaximumAllowedUses,
            requireNonEmpty: true);
        if (kind == TrafficDataSourceKind.PrivateLocalFeedback &&
            uses.Contains(TrafficDataAllowedUse.DistributableSnapshot))
        {
            throw new ArgumentException("Raw private feedback cannot be a distributable snapshot source.", nameof(allowedUses));
        }

        if (kind == TrafficDataSourceKind.PrivateLocalFeedback &&
            uses.Any(use => use != TrafficDataAllowedUse.AggregateContribution))
        {
            throw new ArgumentException("Raw private feedback may be used only as an aggregate contribution.", nameof(allowedUses));
        }

        if (visibility == TrafficDatasetVisibility.DistributableAggregate &&
            !uses.Contains(TrafficDataAllowedUse.DistributableSnapshot))
        {
            throw new ArgumentException("Distributable source visibility requires explicit snapshot use.", nameof(allowedUses));
        }

        SourceId = normalizedSourceId;
        DisplayName = TrafficDataGuard.Text(displayName, nameof(displayName));
        Kind = kind;
        Visibility = visibility;
        Provenance = provenance;
        LicenseIdentifier = TrafficDataGuard.Token(licenseIdentifier, nameof(licenseIdentifier));
        ConsentBasis = consentBasis;
        CollectionMethod = TrafficDataGuard.Description(collectionMethod, nameof(collectionMethod));
        AllowedUses = uses;
        CompatibilityScopes = TrafficDataGuard.List(
            compatibilityScopes,
            nameof(compatibilityScopes),
            TrafficDataBounds.MaximumScopes,
            requireNonEmpty: true);
        if (CompatibilityScopes.Distinct().Count() != CompatibilityScopes.Count)
        {
            throw new ArgumentException("Source compatibility scopes must be distinct.", nameof(compatibilityScopes));
        }

        ReviewedUtc = TrafficDataGuard.Utc(reviewedUtc, nameof(reviewedUtc));
        if (ReviewedUtc < provenance.ObservedUtc)
        {
            throw new ArgumentException("Source review cannot predate the recorded evidence.", nameof(reviewedUtc));
        }

        Reference = TrafficDataGuard.OptionalText(reference, nameof(reference));
    }

    public string SourceId { get; }

    public string DisplayName { get; }

    public TrafficDataSourceKind Kind { get; }

    public TrafficDatasetVisibility Visibility { get; }

    public EvidenceProvenance Provenance { get; }

    public string LicenseIdentifier { get; }

    public TrafficConsentBasis ConsentBasis { get; }

    public string CollectionMethod { get; }

    public IReadOnlyList<TrafficDataAllowedUse> AllowedUses { get; }

    public IReadOnlyList<TrafficCompatibilityScope> CompatibilityScopes { get; }

    public DateTimeOffset ReviewedUtc { get; }

    public string? Reference { get; }
}

/// <summary>Explicit private feedback. No submission is distinct from an explicit Unknown.</summary>
public sealed record HistoricalTrafficFeedback
{
    public HistoricalTrafficFeedback(
        Guid feedbackId,
        string sourceId,
        TrafficContributionConsent consent,
        TrafficCompatibilityScope scope,
        TrafficSpatialReference location,
        TrafficPhaseWindow window,
        TrafficObservationClass observation,
        EvidenceProvenance provenance,
        DateTimeOffset submittedUtc,
        string evaluatedPredictionId,
        string evaluatedModelVersion)
    {
        if (feedbackId == Guid.Empty)
        {
            throw new ArgumentException("A feedback id is required.", nameof(feedbackId));
        }

        ArgumentNullException.ThrowIfNull(consent);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(provenance);
        if (!Enum.IsDefined(observation))
        {
            throw new ArgumentOutOfRangeException(nameof(observation));
        }

        if (provenance.SourceClass != EvidenceSourceClass.UserEntered || provenance.Inputs.Count != 0)
        {
            throw new ArgumentException("Traffic feedback must be a direct user-entered observation.", nameof(provenance));
        }

        var normalizedSourceId = TrafficDataGuard.Token(sourceId, nameof(sourceId));
        if (!string.Equals(normalizedSourceId, TrafficDataBounds.LocalFeedbackSourceId, StringComparison.Ordinal) ||
            !string.Equals(provenance.SourceIdentifier, normalizedSourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Traffic feedback must use the fixed non-identifying local source provenance.", nameof(provenance));
        }

        if (!string.Equals(scope.MapId, location.MapId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Feedback scope and location must name the same map.", nameof(location));
        }

        FeedbackId = feedbackId;
        SourceId = normalizedSourceId;
        Consent = consent;
        Scope = scope;
        Location = location;
        Window = window;
        Observation = observation;
        Provenance = provenance;
        SubmittedUtc = TrafficDataGuard.Utc(submittedUtc, nameof(submittedUtc));
        if (SubmittedUtc < provenance.ObservedUtc)
        {
            throw new ArgumentException("Feedback submission cannot predate the observation.", nameof(submittedUtc));
        }

        if (SubmittedUtc < consent.GrantedUtc)
        {
            throw new ArgumentException("Feedback cannot be submitted before contribution consent was granted.", nameof(submittedUtc));
        }

        EvaluatedPredictionId = TrafficDataGuard.Token(evaluatedPredictionId, nameof(evaluatedPredictionId));
        EvaluatedModelVersion = TrafficDataGuard.Token(evaluatedModelVersion, nameof(evaluatedModelVersion));
    }

    public Guid FeedbackId { get; }

    public string SourceId { get; }

    public TrafficContributionConsent Consent { get; }

    public TrafficCompatibilityScope Scope { get; }

    public TrafficSpatialReference Location { get; }

    public TrafficPhaseWindow Window { get; }

    public TrafficObservationClass Observation { get; }

    public EvidenceProvenance Provenance { get; }

    public DateTimeOffset SubmittedUtc { get; }

    public string EvaluatedPredictionId { get; }

    public string EvaluatedModelVersion { get; }
}

/// <summary>An append-only correction/revocation event; deletion removes the complete local chain.</summary>
public sealed record TrafficFeedbackEvent
{
    public TrafficFeedbackEvent(
        Guid eventId,
        Guid feedbackId,
        long revision,
        TrafficFeedbackEventKind kind,
        DateTimeOffset occurredUtc,
        TrafficFeedbackActor actor,
        HistoricalTrafficFeedback? value,
        Guid? supersedesEventId = null)
    {
        if (eventId == Guid.Empty || feedbackId == Guid.Empty || revision < 1 ||
            !Enum.IsDefined(kind) || !Enum.IsDefined(actor))
        {
            throw new ArgumentException("A feedback event requires defined identifiers, revision, and kind.");
        }

        if (kind == TrafficFeedbackEventKind.Submitted)
        {
            if (revision != 1 || supersedesEventId is not null || value is null)
            {
                throw new ArgumentException("A submission is revision one with a value and no predecessor.");
            }
        }
        else if (revision == 1 || supersedesEventId is null || supersedesEventId == Guid.Empty)
        {
            throw new ArgumentException("A correction or revocation must name an earlier event.");
        }

        if ((kind == TrafficFeedbackEventKind.Revoked) != (value is null))
        {
            throw new ArgumentException("Only a revocation omits the current feedback value.", nameof(value));
        }

        if (value is not null && value.FeedbackId != feedbackId)
        {
            throw new ArgumentException("A feedback event cannot change the feedback id.", nameof(value));
        }

        EventId = eventId;
        FeedbackId = feedbackId;
        Revision = revision;
        Kind = kind;
        OccurredUtc = TrafficDataGuard.Utc(occurredUtc, nameof(occurredUtc));
        Actor = actor;
        Value = value;
        SupersedesEventId = supersedesEventId;
    }

    public Guid EventId { get; }

    public Guid FeedbackId { get; }

    public long Revision { get; }

    public TrafficFeedbackEventKind Kind { get; }

    public DateTimeOffset OccurredUtc { get; }

    public TrafficFeedbackActor Actor { get; }

    public HistoricalTrafficFeedback? Value { get; }

    public Guid? SupersedesEventId { get; }
}

/// <summary>A complete append-only feedback chain. Revocation is terminal; deletion removes this local chain.</summary>
public sealed record TrafficFeedbackHistory
{
    public TrafficFeedbackHistory(Guid feedbackId, IReadOnlyList<TrafficFeedbackEvent> events)
    {
        if (feedbackId == Guid.Empty)
        {
            throw new ArgumentException("A feedback id is required.", nameof(feedbackId));
        }

        Events = TrafficDataGuard.List(
            events,
            nameof(events),
            TrafficDataBounds.MaximumFeedbackEvents,
            requireNonEmpty: true);
        if (Events.Select(item => item.EventId).Distinct().Count() != Events.Count)
        {
            throw new ArgumentException("Feedback event ids must be distinct.", nameof(events));
        }

        for (var index = 0; index < Events.Count; index++)
        {
            var current = Events[index];
            if (current.FeedbackId != feedbackId || current.Revision != index + 1)
            {
                throw new ArgumentException("Feedback revisions must be contiguous and belong to one feedback id.", nameof(events));
            }

            if (index == 0)
            {
                if (current.Kind != TrafficFeedbackEventKind.Submitted)
                {
                    throw new ArgumentException("A feedback history must begin with a submission.", nameof(events));
                }

                if (current.OccurredUtc < current.Value!.SubmittedUtc)
                {
                    throw new ArgumentException("A submission event cannot predate its feedback value.", nameof(events));
                }

                continue;
            }

            var previous = Events[index - 1];
            if (current.SupersedesEventId != previous.EventId || current.OccurredUtc < previous.OccurredUtc)
            {
                throw new ArgumentException("Each feedback event must supersede the immediately prior event in time order.", nameof(events));
            }

            if (previous.Kind == TrafficFeedbackEventKind.Revoked)
            {
                throw new ArgumentException("A revoked feedback history is terminal.", nameof(events));
            }

            if (current.Value is { } value &&
                (!string.Equals(value.SourceId, Events[0].Value!.SourceId, StringComparison.Ordinal) ||
                 value.Consent.ConsentId != Events[0].Value.Consent.ConsentId))
            {
                throw new ArgumentException("A correction cannot switch its source or consent grant.", nameof(events));
            }

            if (current.Value is { } currentValue && current.OccurredUtc < currentValue.SubmittedUtc)
            {
                throw new ArgumentException("A correction event cannot predate its feedback value.", nameof(events));
            }
        }

        FeedbackId = feedbackId;
    }

    public Guid FeedbackId { get; }

    public IReadOnlyList<TrafficFeedbackEvent> Events { get; }

    [JsonIgnore]
    public HistoricalTrafficFeedback? Current => Events[^1].Value;

    [JsonIgnore]
    public bool IsRevoked => Events[^1].Kind == TrafficFeedbackEventKind.Revoked;
}

/// <summary>Versioned deterministic assignment; the leakage group, not an individual row, is the split unit.</summary>
public sealed record TrafficPartitionPolicy
{
    public TrafficPartitionPolicy(
        string policyVersion,
        string assignmentSalt,
        int trainBasisPoints,
        int tuneBasisPoints,
        int heldOutBasisPoints)
    {
        if (trainBasisPoints <= 0 || tuneBasisPoints <= 0 || heldOutBasisPoints <= 0 ||
            trainBasisPoints + tuneBasisPoints + heldOutBasisPoints != TrafficDataBounds.PartitionBasisPoints)
        {
            throw new ArgumentException("Partition shares must be positive and total 10,000 basis points.");
        }

        PolicyVersion = TrafficDataGuard.Token(policyVersion, nameof(policyVersion));
        AssignmentSalt = TrafficDataGuard.Token(assignmentSalt, nameof(assignmentSalt));
        TrainBasisPoints = trainBasisPoints;
        TuneBasisPoints = tuneBasisPoints;
        HeldOutBasisPoints = heldOutBasisPoints;
    }

    public string PolicyVersion { get; }

    public string AssignmentSalt { get; }

    public int TrainBasisPoints { get; }

    public int TuneBasisPoints { get; }

    public int HeldOutBasisPoints { get; }
}

/// <summary>One bounded aggregate cell. It cannot carry a current raid, identity, or exact coordinate.</summary>
public sealed record TrafficAggregateRecord
{
    public TrafficAggregateRecord(
        string recordId,
        string sourceId,
        string partitionGroupId,
        TrafficDataPartition partition,
        TrafficCompatibilityScope scope,
        TrafficSpatialReference location,
        TrafficPhaseWindow window,
        TrafficObservationClass observation,
        long sampleCount,
        EvidenceProvenance provenance)
    {
        if (!Enum.IsDefined(partition) || !Enum.IsDefined(observation))
        {
            throw new ArgumentOutOfRangeException(nameof(partition));
        }

        if (sampleCount is < 1 or > TrafficDataBounds.MaximumSampleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(provenance);
        if (provenance.SourceClass != EvidenceSourceClass.HistoricalAggregate)
        {
            throw new ArgumentException("A governed aggregate record requires historical-aggregate provenance.", nameof(provenance));
        }

        if (!string.Equals(scope.MapId, location.MapId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Aggregate scope and location must name the same map.", nameof(location));
        }

        RecordId = TrafficDataGuard.Sha256(recordId, nameof(recordId));
        SourceId = TrafficDataGuard.Token(sourceId, nameof(sourceId));
        PartitionGroupId = TrafficDataGuard.Sha256(partitionGroupId, nameof(partitionGroupId));
        Partition = partition;
        Scope = scope;
        Location = location;
        Window = window;
        Observation = observation;
        SampleCount = sampleCount;
        Provenance = provenance;
    }

    public string RecordId { get; }

    public string SourceId { get; }

    public string PartitionGroupId { get; }

    public TrafficDataPartition Partition { get; }

    public TrafficCompatibilityScope Scope { get; }

    public TrafficSpatialReference Location { get; }

    public TrafficPhaseWindow Window { get; }

    public TrafficObservationClass Observation { get; }

    public long SampleCount { get; }

    public EvidenceProvenance Provenance { get; }
}

public sealed record TrafficPartitionDigest
{
    public TrafficPartitionDigest(TrafficDataPartition partition, long recordCount, long sampleCount, string sha256)
    {
        if (!Enum.IsDefined(partition) || recordCount < 0 || sampleCount < 0 ||
            recordCount > TrafficDataBounds.MaximumRecords || sampleCount > TrafficDataBounds.MaximumSampleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(partition));
        }

        Partition = partition;
        RecordCount = recordCount;
        SampleCount = sampleCount;
        Sha256 = TrafficDataGuard.Sha256(sha256, nameof(sha256));
    }

    public TrafficDataPartition Partition { get; }

    public long RecordCount { get; }

    public long SampleCount { get; }

    public string Sha256 { get; }
}

public sealed record TrafficCoverageGap
{
    public TrafficCoverageGap(TrafficCompatibilityScope scope, string gapCode, string description)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        GapCode = TrafficDataGuard.Token(gapCode, nameof(gapCode));
        Description = TrafficDataGuard.Description(description, nameof(description));
    }

    public TrafficCompatibilityScope Scope { get; }

    public string GapCode { get; }

    public string Description { get; }
}

/// <summary>A fully partitioned immutable dataset that #275 may consume only through its manifest.</summary>
public sealed record GovernedTrafficDataset
{
    public GovernedTrafficDataset(
        int schemaVersion,
        string datasetId,
        string datasetVersion,
        TrafficDatasetVisibility visibility,
        DateTimeOffset dataThroughUtc,
        DateTimeOffset generatedUtc,
        string transformVersion,
        string contentSha256,
        TrafficPartitionPolicy partitionPolicy,
        IReadOnlyList<TrafficDatasetSource> sources,
        IReadOnlyList<TrafficAggregateRecord> records,
        IReadOnlyList<TrafficPartitionDigest> partitions,
        IReadOnlyList<TrafficCoverageGap> coverageGaps)
    {
        if (schemaVersion != TrafficDataBounds.CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        if (!Enum.IsDefined(visibility))
        {
            throw new ArgumentOutOfRangeException(nameof(visibility));
        }

        SchemaVersion = schemaVersion;
        DatasetId = TrafficDataGuard.Token(datasetId, nameof(datasetId));
        DatasetVersion = TrafficDataGuard.Token(datasetVersion, nameof(datasetVersion));
        Visibility = visibility;
        DataThroughUtc = TrafficDataGuard.Utc(dataThroughUtc, nameof(dataThroughUtc));
        GeneratedUtc = TrafficDataGuard.Utc(generatedUtc, nameof(generatedUtc));
        if (DataThroughUtc > GeneratedUtc)
        {
            throw new ArgumentException("Dataset data-through time cannot follow generation time.");
        }

        TransformVersion = TrafficDataGuard.Token(transformVersion, nameof(transformVersion));
        ContentSha256 = TrafficDataGuard.Sha256(contentSha256, nameof(contentSha256));
        PartitionPolicy = partitionPolicy ?? throw new ArgumentNullException(nameof(partitionPolicy));
        Sources = TrafficDataGuard.List(
            sources,
            nameof(sources),
            TrafficDataBounds.MaximumSources,
            requireNonEmpty: true);
        Records = TrafficDataGuard.List(
            records,
            nameof(records),
            TrafficDataBounds.MaximumRecords,
            requireNonEmpty: true);
        Partitions = TrafficDataGuard.List(
            partitions,
            nameof(partitions),
            Enum.GetValues<TrafficDataPartition>().Length,
            requireNonEmpty: true);
        CoverageGaps = TrafficDataGuard.List(
            coverageGaps,
            nameof(coverageGaps),
            TrafficDataBounds.MaximumCoverageGaps,
            requireNonEmpty: false);

        Validate();
    }

    public int SchemaVersion { get; }

    public string DatasetId { get; }

    public string DatasetVersion { get; }

    public TrafficDatasetVisibility Visibility { get; }

    public DateTimeOffset DataThroughUtc { get; }

    public DateTimeOffset GeneratedUtc { get; }

    public string TransformVersion { get; }

    public string ContentSha256 { get; }

    public TrafficPartitionPolicy PartitionPolicy { get; }

    public IReadOnlyList<TrafficDatasetSource> Sources { get; }

    public IReadOnlyList<TrafficAggregateRecord> Records { get; }

    public IReadOnlyList<TrafficPartitionDigest> Partitions { get; }

    public IReadOnlyList<TrafficCoverageGap> CoverageGaps { get; }

    private void Validate()
    {
        if (Sources.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count() != Sources.Count)
        {
            throw new ArgumentException("Dataset source ids must be distinct.", nameof(Sources));
        }

        if (Records.Select(record => record.RecordId).Distinct(StringComparer.Ordinal).Count() != Records.Count)
        {
            throw new ArgumentException("Dataset record ids must be distinct.", nameof(Records));
        }

        var expectedPartitions = Enum.GetValues<TrafficDataPartition>();
        if (Partitions.Count != expectedPartitions.Length ||
            !Partitions.Select(partition => partition.Partition).Order().SequenceEqual(expectedPartitions.Order()))
        {
            throw new ArgumentException("Dataset manifests must name train, tune, and held-out partitions exactly once.", nameof(Partitions));
        }

        if (Records.GroupBy(record => record.PartitionGroupId, StringComparer.Ordinal)
            .Any(group => group.Select(record => record.Partition).Distinct().Skip(1).Any()))
        {
            throw new ArgumentException("One leakage group cannot cross dataset partitions.", nameof(Records));
        }

        var sourceMap = Sources.ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        if (Sources.Any(source => source.ReviewedUtc > GeneratedUtc || source.Provenance.ObservedUtc > GeneratedUtc))
        {
            throw new ArgumentException("Dataset generation cannot predate source evidence or review.", nameof(Sources));
        }

        foreach (var record in Records)
        {
            if (!sourceMap.TryGetValue(record.SourceId, out var source) ||
                !source.CompatibilityScopes.Contains(record.Scope))
            {
                throw new ArgumentException("Every record must belong to a declared source compatibility scope.", nameof(Records));
            }


            var requiredUse = record.Partition switch
            {
                TrafficDataPartition.Train => TrafficDataAllowedUse.ModelTraining,
                TrafficDataPartition.Tune => TrafficDataAllowedUse.ModelTuning,
                TrafficDataPartition.HeldOut => TrafficDataAllowedUse.HeldOutEvaluation,
                _ => throw new ArgumentOutOfRangeException(nameof(record.Partition)),
            };
            if (!source.AllowedUses.Contains(requiredUse))
            {
                throw new ArgumentException($"Source {source.SourceId} does not permit {requiredUse}.", nameof(Records));
            }

            if (record.Partition != TrafficPartitioner.Assign(record.PartitionGroupId, PartitionPolicy))
            {
                throw new ArgumentException("A record partition does not match deterministic leakage-group assignment.", nameof(Records));
            }

            if (!record.Provenance.Equals(source.Provenance) &&
                !record.Provenance.DescendantInputs().Contains(source.Provenance))
            {
                throw new ArgumentException("A record's lineage must bind it to its declared source.", nameof(Records));
            }

            if (Visibility == TrafficDatasetVisibility.DistributableAggregate &&
                (source.Kind == TrafficDataSourceKind.PrivateLocalFeedback ||
                 source.Visibility != TrafficDatasetVisibility.DistributableAggregate ||
                 !source.AllowedUses.Contains(TrafficDataAllowedUse.DistributableSnapshot)))
            {
                throw new ArgumentException("A distributable dataset requires a source that explicitly permits distribution.", nameof(Records));
            }

            if (Visibility == TrafficDatasetVisibility.DistributableAggregate &&
                record.SampleCount < TrafficDataBounds.MinimumDistributableSampleCount)
            {
                throw new ArgumentException("A distributable aggregate cell does not meet the privacy sample floor.", nameof(Records));
            }

            if (record.Provenance.DataThroughUtc > DataThroughUtc ||
                record.Provenance.GeneratedUtc > GeneratedUtc ||
                record.Provenance.ObservedUtc > GeneratedUtc)
            {
                throw new ArgumentException("A record cannot be newer than its dataset manifest.", nameof(Records));
            }
        }

        var recordScopes = Records.Select(record => record.Scope).Distinct().ToArray();
        if (CoverageGaps.Any(gap => !recordScopes.Contains(gap.Scope)))
        {
            throw new ArgumentException("Every coverage gap must belong to a dataset compatibility scope.", nameof(CoverageGaps));
        }

        foreach (var partition in Partitions)
        {
            var records = Records.Where(record => record.Partition == partition.Partition).ToArray();
            if (partition.RecordCount != records.LongLength ||
                partition.SampleCount != records.Sum(record => record.SampleCount))
            {
                throw new ArgumentException("Partition counts must reconcile with the immutable records.", nameof(Partitions));
            }
        }
    }
}

/// <summary>Integrity and compatibility contract for the immutable runtime artifact installed for #275.</summary>
public sealed record TrafficModelArtifactManifest
{
    public TrafficModelArtifactManifest(
        int schemaVersion,
        string modelId,
        string modelVersion,
        string datasetId,
        string datasetVersion,
        string datasetSha256,
        string buildReportSha256,
        string artifactSha256,
        string artifactFormat,
        string transformVersion,
        string calibrationReference,
        DateTimeOffset dataThroughUtc,
        DateTimeOffset generatedUtc,
        EvidenceConfidence confidence,
        EvidenceCoverage coverage,
        IReadOnlyList<TrafficCompatibilityScope> compatibilityScopes,
        IReadOnlyList<TrafficCoverageGap> coverageGaps,
        IReadOnlyList<TrafficPartitionDigest> partitions)
    {
        if (schemaVersion != TrafficDataBounds.CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(confidence);
        ArgumentNullException.ThrowIfNull(coverage);
        SchemaVersion = schemaVersion;
        ModelId = TrafficDataGuard.Token(modelId, nameof(modelId));
        ModelVersion = TrafficDataGuard.Token(modelVersion, nameof(modelVersion));
        DatasetId = TrafficDataGuard.Token(datasetId, nameof(datasetId));
        DatasetVersion = TrafficDataGuard.Token(datasetVersion, nameof(datasetVersion));
        DatasetSha256 = TrafficDataGuard.Sha256(datasetSha256, nameof(datasetSha256));
        BuildReportSha256 = TrafficDataGuard.Sha256(buildReportSha256, nameof(buildReportSha256));
        ArtifactSha256 = TrafficDataGuard.Sha256(artifactSha256, nameof(artifactSha256));
        ArtifactFormat = TrafficDataGuard.Token(artifactFormat, nameof(artifactFormat));
        TransformVersion = TrafficDataGuard.Token(transformVersion, nameof(transformVersion));
        CalibrationReference = TrafficDataGuard.Text(calibrationReference, nameof(calibrationReference));
        if (confidence.Kind != EvidenceConfidenceKind.CalibratedEstimate ||
            !string.Equals(confidence.CalibrationReference, CalibrationReference, StringComparison.Ordinal))
        {
            throw new ArgumentException("Traffic model confidence must be calibrated by the named reference.", nameof(confidence));
        }

        Confidence = confidence;
        DataThroughUtc = TrafficDataGuard.Utc(dataThroughUtc, nameof(dataThroughUtc));
        GeneratedUtc = TrafficDataGuard.Utc(generatedUtc, nameof(generatedUtc));
        if (DataThroughUtc > GeneratedUtc)
        {
            throw new ArgumentException("Model data-through time cannot follow generation time.");
        }

        Coverage = coverage;
        CompatibilityScopes = TrafficDataGuard.List(
            compatibilityScopes,
            nameof(compatibilityScopes),
            TrafficDataBounds.MaximumScopes,
            requireNonEmpty: true);
        if (CompatibilityScopes.Distinct().Count() != CompatibilityScopes.Count)
        {
            throw new ArgumentException("Artifact compatibility scopes must be distinct.", nameof(compatibilityScopes));
        }

        CoverageGaps = TrafficDataGuard.List(
            coverageGaps,
            nameof(coverageGaps),
            TrafficDataBounds.MaximumCoverageGaps,
            requireNonEmpty: false);
        if (CoverageGaps.Any(gap => !CompatibilityScopes.Contains(gap.Scope)))
        {
            throw new ArgumentException("Every artifact coverage gap must belong to a compatibility scope.", nameof(coverageGaps));
        }

        Partitions = TrafficDataGuard.List(
            partitions,
            nameof(partitions),
            Enum.GetValues<TrafficDataPartition>().Length,
            requireNonEmpty: true);
        var expected = Enum.GetValues<TrafficDataPartition>();
        if (Partitions.Count != expected.Length ||
            !Partitions.Select(partition => partition.Partition).Order().SequenceEqual(expected.Order()))
        {
            throw new ArgumentException("A model artifact must retain every partition digest exactly once.", nameof(partitions));
        }


        var totalSamples = Partitions.Sum(partition => partition.SampleCount);
        if (Coverage.SampleSize != totalSamples)
        {
            throw new ArgumentException("Artifact coverage sample size must reconcile with its partition evidence.", nameof(coverage));
        }
    }

    public int SchemaVersion { get; }

    public string ModelId { get; }

    public string ModelVersion { get; }

    public string DatasetId { get; }

    public string DatasetVersion { get; }

    public string DatasetSha256 { get; }

    public string BuildReportSha256 { get; }

    public string ArtifactSha256 { get; }

    public string ArtifactFormat { get; }

    public string TransformVersion { get; }

    public string CalibrationReference { get; }

    public EvidenceConfidence Confidence { get; }

    public DateTimeOffset DataThroughUtc { get; }

    public DateTimeOffset GeneratedUtc { get; }

    public EvidenceCoverage Coverage { get; }

    public IReadOnlyList<TrafficCompatibilityScope> CompatibilityScopes { get; }

    public IReadOnlyList<TrafficCoverageGap> CoverageGaps { get; }

    public IReadOnlyList<TrafficPartitionDigest> Partitions { get; }

    public bool Supports(TrafficCompatibilityScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return CompatibilityScopes.Contains(scope);
    }
}

internal static class TrafficDataGuard
{
    public static string Token(string value, string parameterName) =>
        ValidateText(value, parameterName, TrafficDataBounds.MaximumTextLength, token: true);

    public static string Text(string value, string parameterName) =>
        ValidateText(value, parameterName, TrafficDataBounds.MaximumTextLength, token: false);

    public static string ExactCompatibilityToken(string value, string parameterName)
    {
        var token = Token(value, parameterName);
        if (string.Equals(token, "unknown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(token, "any", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(token, "all", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Compatibility values cannot be wildcard or unknown labels.", parameterName);
        }

        return token;
    }

    public static string Description(string value, string parameterName) =>
        ValidateText(value, parameterName, TrafficDataBounds.MaximumDescriptionLength, token: false);

    public static string? OptionalToken(string? value, string parameterName) =>
        value is null ? null : Token(value, parameterName);

    public static string? OptionalText(string? value, string parameterName) =>
        value is null ? null : Text(value, parameterName);

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentException("A timestamp is required.", parameterName);
        }

        return value.ToUniversalTime();
    }

    public static string Sha256(string value, string parameterName)
    {
        var digest = Token(value, parameterName);
        if (digest.Length != 64 || digest.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A canonical lowercase SHA-256 digest is required.", parameterName);
        }

        return digest;
    }

    public static IReadOnlyList<T> List<T>(
        IReadOnlyList<T> values,
        string parameterName,
        int maximum,
        bool requireNonEmpty)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var count = values.Count;
        if (count < 0 || count > maximum || (requireNonEmpty && count == 0))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        var copy = new T[count];
        for (var index = 0; index < count; index++)
        {
            var value = values[index];
            if (value is null)
            {
                throw new ArgumentException("Contract lists cannot contain null.", parameterName);
            }

            copy[index] = value;
        }

        return Array.AsReadOnly(copy);
    }

    public static IReadOnlyList<TEnum> EnumList<TEnum>(
        IReadOnlyList<TEnum> values,
        string parameterName,
        int maximum,
        bool requireNonEmpty)
        where TEnum : struct, Enum
    {
        var copy = List(values, parameterName, maximum, requireNonEmpty);
        if (copy.Any(value => !Enum.IsDefined(value)) || copy.Distinct().Count() != copy.Count)
        {
            throw new ArgumentException("Enum lists must contain distinct defined values.", parameterName);
        }

        return copy;
    }

    private static string ValidateText(string value, string parameterName, int maximum, bool token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximum || value != value.Trim() || value.Any(char.IsControl) ||
            (token && value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/'))))
        {
            throw new ArgumentException("Text must be bounded, canonical, and free of control characters.", parameterName);
        }

        return value;
    }
}
