using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.Application.Services.Profile;

public sealed record EventConsumptionDecision(
    EventItemState State,
    RecommendationAction Action,
    string Explanation);

public sealed record EventStateCounts(int Total, int Untested, int Safe, int Allergic, int Unknown)
{
    public int Tested => Safe + Allergic;
}

public sealed class ProfileEventTrackerService : IEventTrackerService, IDisposable
{
    private readonly IPlayerProfileService _profileService;
    private readonly IReadOnlyDictionary<string, EventDefinition> _definitions;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    public ProfileEventTrackerService(
        IPlayerProfileService profileService,
        IEnumerable<EventDefinition> definitions,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(profileService);
        ArgumentNullException.ThrowIfNull(definitions);

        _profileService = profileService;
        _definitions = definitions.ToDictionary(x => x.Id, StringComparer.Ordinal);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<EventItemState> GetItemStateAsync(
        string eventId,
        string itemId,
        CancellationToken cancellationToken)
    {
        var definition = GetDefinition(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        if (!definition.ApplicableItemIds.Contains(itemId))
        {
            return EventItemState.Unknown;
        }

        var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        return profile.EventItemStates.GetValueOrDefault(StateKey(eventId, itemId), EventItemState.Untested);
    }

    public async Task SetItemStateAsync(
        string eventId,
        string itemId,
        EventItemState state,
        CancellationToken cancellationToken)
    {
        var definition = GetDefinition(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (!definition.ApplicableItemIds.Contains(itemId))
        {
            throw new ArgumentException($"Item '{itemId}' is not part of event '{eventId}'.", nameof(itemId));
        }

        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            await SaveStateAsync(profile, eventId, itemId, state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public async Task<EventProgress> GetProgressAsync(string eventId, CancellationToken cancellationToken)
    {
        var counts = await GetStateCountsAsync(eventId, cancellationToken).ConfigureAwait(false);
        return new(eventId, counts.Total, counts.Tested, counts.Safe, counts.Allergic, counts.Untested + counts.Unknown);
    }

    public async Task<EventStateCounts> GetStateCountsAsync(string eventId, CancellationToken cancellationToken)
    {
        var definition = GetDefinition(eventId);
        var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var states = definition.ApplicableItemIds
            .Select(itemId => profile.EventItemStates.GetValueOrDefault(StateKey(eventId, itemId), EventItemState.Untested))
            .ToArray();
        return new(
            states.Length,
            states.Count(x => x == EventItemState.Untested),
            states.Count(x => x == EventItemState.Safe),
            states.Count(x => x == EventItemState.Allergic),
            states.Count(x => x == EventItemState.Unknown));
    }

    public async Task<EventItemState> RecordConsumptionAsync(
        string eventId,
        string itemId,
        EventItemState observedState,
        CancellationToken cancellationToken)
    {
        if (observedState is not (EventItemState.Safe or EventItemState.Allergic))
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedState),
                "A consumption result must be Safe or Allergic.");
        }

        var definition = GetDefinition(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (!definition.ApplicableItemIds.Contains(itemId))
        {
            throw new ArgumentException($"Item '{itemId}' is not part of event '{eventId}'.", nameof(itemId));
        }

        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            var existing = profile.EventItemStates.GetValueOrDefault(StateKey(eventId, itemId), EventItemState.Untested);
            var resolved = existing == EventItemState.Allergic || observedState == EventItemState.Allergic
                ? EventItemState.Allergic
                : EventItemState.Safe;
            await SaveStateAsync(profile, eventId, itemId, resolved, cancellationToken).ConfigureAwait(false);
            return resolved;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public async Task<EventConsumptionDecision> GetConsumptionDecisionAsync(
        string eventId,
        string itemId,
        CancellationToken cancellationToken)
    {
        var state = await GetItemStateAsync(eventId, itemId, cancellationToken).ConfigureAwait(false);
        return state switch
        {
            EventItemState.Allergic => new(state, RecommendationAction.AvoidConsume, "Known allergic result takes precedence; do not consume."),
            EventItemState.Untested => new(state, RecommendationAction.EventTestCandidate, "Untested for this event; test only when the player chooses to."),
            EventItemState.Safe => new(state, RecommendationAction.Use, "Previously tested safe for this event."),
            _ => new(state, RecommendationAction.Unknown, "This item is outside the event or has no applicable state."),
        };
    }

    public void Dispose() => _updateLock.Dispose();

    private EventDefinition GetDefinition(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        return _definitions.GetValueOrDefault(eventId)
            ?? throw new KeyNotFoundException($"Event '{eventId}' is not configured.");
    }

    private Task SaveStateAsync(
        PlayerProfile profile,
        string eventId,
        string itemId,
        EventItemState state,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, EventItemState>(profile.EventItemStates, StringComparer.Ordinal);
        states[StateKey(eventId, itemId)] = state;

        return _profileService.SaveAsync(
            profile with
            {
                EventItemStates = states,
                UpdatedUtc = _timeProvider.GetUtcNow(),
            },
            cancellationToken);
    }

    private static string StateKey(string eventId, string itemId) => $"{eventId}:{itemId}";
}
