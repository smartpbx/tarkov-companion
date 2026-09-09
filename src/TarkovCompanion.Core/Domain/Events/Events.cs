using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Core.Domain.Events;

public enum EventItemState
{
    Unknown,
    Untested,
    Safe,
    Allergic,
}

public sealed record EventDefinition(
    string Id,
    string Name,
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    bool Active,
    IReadOnlySet<string> ApplicableItemIds,
    string RulesJson,
    DataProvenance Provenance);

public sealed record EventProgress(string EventId, int Total, int Tested, int Safe, int Allergic, int Unknown);
