using TarkovCompanion.App.Services.V2.SelfTest;
using TarkovCompanion.Application.Services.Profiles;

namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    /// <summary>[#314] What a folder the self-test reads is for.</summary>
    public static string ProbePurpose(SelfTestFolderPurpose purpose) => PhraseText.Say(purpose);

    /// <summary>
    /// [#314] Discovery's own sentence, said from its code; the fault it quotes stays as the runtime
    /// wrote it. A code this build does not know is said as discovery wrote it.
    /// </summary>
    public static string DiscoveryDetail(EftInstallDiscoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Code switch
        {
            "eft-discovery-uninitialized" => UiText.Get("Setup.Discovery.Uninitialized"),
            "eft-discovery-ready" => UiText.Get("Setup.Discovery.Ready"),
            "eft-discovery-recovered" => UiText.Get("Setup.Discovery.Recovered"),
            "eft-install-missing" => $"{UiText.Get("Setup.Discovery.Missing")} {UiText.Get("Setup.Discovery.MissingFix")}",
            "eft-install-disappeared" => $"{UiText.Get("Setup.Discovery.Disappeared")} {UiText.Get("Setup.Discovery.DisappearedFix")}",
            "eft-discovery-configuration-invalid" when snapshot.Fault is { } fault =>
                UiText.Format("Setup.Discovery.ConfigurationInvalid", fault),
            "eft-discovery-unavailable" when snapshot.Fault is { } fault =>
                UiText.Format("Setup.Discovery.Unavailable", fault),
            _ => snapshot.Detail,
        };
    }
}
