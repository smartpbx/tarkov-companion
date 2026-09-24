using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Application.Services.Runtime;

/// <summary>
/// [#314] The game-data status line, as codes the App words: Setup &gt; Data and the pages that
/// wait on the catalog show it. It is not stored or sent anywhere.
/// </summary>
[PhraseCodes("Setup.DataDetail")]
public enum DataDetail
{
    Refreshing,

    /// <summary>{0}: how many endpoints.</summary>
    Refreshed,

    /// <summary>{0}: how many endpoints; {1}: how many of them served a cached copy.</summary>
    RefreshedWithStale,

    /// <summary>{0}: the <see cref="EndpointFailed"/> list.</summary>
    LocalDataStands,

    /// <summary>{0}: the <see cref="EndpointFailed"/> list.</summary>
    NoItems,
    EmptyRefresh,

    /// <summary>{0}: the endpoint's name; {1}: its reason as the sync recorded it, or <see cref="UnknownReason"/>.</summary>
    EndpointFailed,

    /// <summary>{0} and {1}: two parts of a list of failures.</summary>
    AndAlso,
    UnknownReason,
    TimedOut,
    Stopped,
    Failed,
    Reconnected,
    NoGameMode,
    OfflineCached,
    OfflineEmpty,
    DemoFixtures,
    NoLocalData,
    LastOperationFailed,
    UsableStale,
    Usable,
}
