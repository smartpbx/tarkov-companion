using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    /// <summary>[#314] Why a proposal was classified so: its code in words, or the adapter's own reason.</summary>
    public static string QuestImportReason(QuestImportProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return proposal.ReasonCode is { } code ? PhraseText.Say(code) : proposal.Reason;
    }

    /// <summary>
    /// [#314] A reason stored with a past import, in words where it is one of the fixed reasons.
    /// The store keeps the English, so the code is found from it; an adapter's reason is said as written.
    /// </summary>
    public static string QuestImportReasonStored(string stored) =>
        QuestImportReasons.Find(stored) is { } code ? PhraseText.Say(code) : stored;
}
