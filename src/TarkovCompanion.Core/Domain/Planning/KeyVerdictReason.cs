namespace TarkovCompanion.Core.Domain.Planning;

/// <summary>The one fact that decided a key's keep-or-sell verdict, as a code the App puts into words (#314).</summary>
public enum KeyVerdictReason
{
    /// <summary>A quest the player is on needs it (<see cref="KeyReasonCode.Count"/> of them).</summary>
    TrackedQuestsNeedIt,

    /// <summary>A quest still ahead of the player needs it (<see cref="KeyReasonCode.Count"/> of them).</summary>
    QuestsAheadNeedIt,

    HideoutNeedsIt,

    /// <summary>No price is cached, so nothing can rank it.</summary>
    NoPrice,

    /// <summary>Too few keys have cached prices to rank this one.</summary>
    TooFewPriced,

    /// <summary>Dearer than <see cref="KeyReasonCode.Share"/> of priced keys.</summary>
    Dearer,

    /// <summary>Dearer than <see cref="KeyReasonCode.Share"/> of priced keys, and it opens once.</summary>
    DearerOpensOnce,

    /// <summary>Cheaper than <see cref="KeyReasonCode.Share"/> of priced keys.</summary>
    Cheaper,

    /// <summary>Cheaper than <see cref="KeyReasonCode.Share"/> of priced keys, and no cached lock lists it.</summary>
    CheaperNoLock,

    /// <summary>The market prices it in the middle.</summary>
    Middle,
}

/// <summary>A share of priced keys, as a fraction somebody would say out loud ("four keys in five").</summary>
public enum KeyShareBand
{
    AlmostNone,
    Fifth,
    Third,
    Half,
    TwoInThree,
    ThreeInFour,
    FourInFive,
    NineInTen,
    NearlyEvery,
}

/// <summary>Why a key got its verdict: the reason, and the count or share it names.</summary>
public sealed record KeyReasonCode(KeyVerdictReason Reason, int Count = 0, KeyShareBand Share = KeyShareBand.AlmostNone);
