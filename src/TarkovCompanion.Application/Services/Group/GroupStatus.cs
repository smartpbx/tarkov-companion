using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// [#314] The group's status line, as codes the App words: Setup &gt; Team &amp; Devices and the
/// Team workspace both show it. Nothing here is sent to the relay or stored.
/// </summary>
[PhraseCodes("Setup.GroupStatus")]
public enum GroupStatus
{
    NotSharing,

    /// <summary>{0}: a <see cref="GroupSettingsGap"/> phrase.</summary>
    Needs,

    /// <summary>{0} and {1}: the key length limits.</summary>
    KeyLength,
    OperatorRegisteredOnly,
    ServerRejected,

    /// <summary>{0}: the HTTP status code.</summary>
    ServerAnswered,

    /// <summary>{0}: the exception's message, which stays as the runtime wrote it.</summary>
    ServerUnreachable,
    NoAnswerInTime,

    /// <summary>{0}: the exception's message.</summary>
    SharingFailed,

    /// <summary>{0}: the failure phrase; {1}: how long ago, a <see cref="TimeSpan"/> the App says as a duration.</summary>
    LastHeard,

    /// <summary>{0}: the display name.</summary>
    SharingAlone,

    /// <summary>Counted: {0} the others, {1} the display name.</summary>
    SharingWith,

    /// <summary>{0}: the sharing phrase.</summary>
    NoPositionUnsupported,
    NoPositionFolder,
    NoScreenshotSteam,
    NoPositionYet,

    /// <summary>{0}: the sharing phrase; {1}: the <see cref="RelaySkew"/> phrase.</summary>
    WithSkew,

    /// <summary>{0}: the relay's protocol; {1}: this build's.</summary>
    RelaySkew,
    SettingsReset,

    /// <summary>{0}: the set-aside file's name.</summary>
    SettingsResetAside,

    /// <summary>[#292] Local only is on in Setup › Data &amp; Privacy (or TARKOV_COMPANION_OFFLINE).</summary>
    LocalOnly,

    /// <summary>[#292] Squad sharing is switched off in Setup › Data &amp; Privacy.</summary>
    SwitchedOff,
}

/// <summary>[#314] What group settings still need before sharing can start.</summary>
[PhraseCodes("Setup.GroupGap")]
public enum GroupSettingsGap
{
    ServerAddress,
    ValidServerAddress,
    HttpsAddress,
    DisplayName,
    Key,

    /// <summary>{0}: the minimum key length.</summary>
    KeyTooShort,

    /// <summary>{0}: the maximum key length.</summary>
    KeyTooLong,
}
