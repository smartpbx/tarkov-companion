using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Application.Services.Ask;

/// <summary>What a question asks for, as the grammar read it.</summary>
public enum AskIntent
{
    /// <summary>"What do I need for Gunsmith 5" or "Lavatory 2": a quest or a hideout level.</summary>
    Needs,

    /// <summary>"Where is Dorms 314 key used", "is LEDX needed for anything".</summary>
    ItemUses,

    /// <summary>"Where can I buy a Gas analyzer": traders, barters, crafts and the flea.</summary>
    ItemSources,

    /// <summary>"Best extract from here": the raid's exits from the last screenshot.</summary>
    BestExtract,

    /// <summary>"Best 5.45 for class 4", "best 7.62x39 under 1k".</summary>
    Ammo,
}

/// <summary>A question the grammar understood: its intent and the words it is about.</summary>
/// <param name="Subject">The name part ("Gunsmith 5", "LEDX"); empty for an extract question.</param>
/// <param name="ArmorClass">An ammunition question's armour class, 1 to 6, where it names one.</param>
/// <param name="PriceCapRoubles">An ammunition question's price limit per round, where it names one.</param>
public sealed record AskQuestion(
    string Text,
    AskIntent Intent,
    string Subject,
    int? ArmorClass = null,
    long? PriceCapRoubles = null);

/// <summary>Where an answer's facts came from, which the card shows beside it.</summary>
public enum AskSourceKind
{
    /// <summary>The synced tarkov.dev catalog, at <see cref="AskSource.AsOfUtc"/> where known.</summary>
    Catalog,

    /// <summary>The player's own recorded progress (quests, hideout, loyalty).</summary>
    Profile,

    /// <summary>The player's last map screenshot, taken at <see cref="AskSource.AsOfUtc"/>.</summary>
    Screenshot,

    /// <summary>A rule of thumb the app applies (armour ratings, straight-line distances); never a reading.</summary>
    Modelled,

    /// <summary>The optional local model (decision 3): off by default, runs on this PC only.</summary>
    LocalModel,
}

public sealed record AskSource(AskSourceKind Kind, DateTimeOffset? AsOfUtc = null);

/// <summary>Where an answer's link goes.</summary>
public enum AskLinkKind
{
    /// <summary>The item's Intel page; <see cref="AskLink.Target"/> is the item id.</summary>
    IntelItem,

    /// <summary>Plan's quest list searched for <see cref="AskLink.Target"/>, the quest's name.</summary>
    PlanQuest,

    /// <summary>Plan › Hideout.</summary>
    PlanHideout,

    /// <summary>The Raid map.</summary>
    Raid,

    /// <summary>Intel › Ammo.</summary>
    IntelAmmo,

    /// <summary>Intel › Crafts &amp; barters.</summary>
    IntelCrafts,
}

/// <param name="Name">The thing linked to, as the catalog names it; empty for a page link.</param>
public sealed record AskLink(AskLinkKind Kind, string? Target = null, string Name = "");

/// <summary>Why a question got no answer. Each is said plainly; none is ever a guess.</summary>
public enum AskUnanswered
{
    /// <summary>The words match no question the app knows how to answer.</summary>
    NotUnderstood,

    /// <summary>The question was understood but nothing in the catalog is called that.</summary>
    NoMatch,

    /// <summary>An extract question with no raid map open, or a map without exits.</summary>
    NoRaid,

    /// <summary>The catalog has not synced the table the answer needs.</summary>
    NoData,

    /// <summary>The words name more than one thing (a "7.62" is four calibers).</summary>
    Ambiguous,
}

/// <summary>
/// One answer card: a heading, at most a handful of lines, where each fact came from, and links
/// into the page that holds the rest.
/// </summary>
/// <remarks>
/// Lines are <see cref="Phrase"/>s (#314): the Application decides what to say and the App says it
/// in the interface language. The heading and the closest matches are names from the game data,
/// shown as they are.
/// </remarks>
public sealed record AskAnswer(
    AskIntent? Intent,
    string Heading,
    IReadOnlyList<Phrase> Lines,
    IReadOnlyList<AskSource> Sources,
    IReadOnlyList<AskLink> Links,
    AskUnanswered? Unanswered = null,
    IReadOnlyList<string>? ClosestMatches = null)
{
    public bool IsAnswered => Unanswered is null;

    public IReadOnlyList<string> Closest => ClosestMatches ?? [];

    public static AskAnswer Cannot(
        AskUnanswered reason,
        AskIntent? intent = null,
        string subject = "",
        IReadOnlyList<string>? closest = null) =>
        new(intent, subject, [], [], [], reason, closest ?? []);
}

/// <summary>
/// The words an answer line is made of (key prefix <c>Ask.Line</c>). Arguments are names from the
/// catalog and numbers; the table decides the sentence around them.
/// </summary>
[PhraseCodes("Ask.Line")]
public enum AskLine
{
    // A quest: {0} trader, {1} level.
    QuestGiver,
    QuestGiverLevel,
    QuestAfter,
    QuestObjective,
    QuestObjectiveCount,
    QuestObjectiveCountFir,
    QuestKappa,
    QuestFinished,
    QuestLocked,

    // A hideout level: {0} station, {1} level; items {0} name {1} count.
    HideoutLevel,
    HideoutItem,
    HideoutStation,
    HideoutOther,
    HideoutBuilt,

    // Item uses.
    UseQuest,
    UseQuestFir,
    UseHideout,
    UseKeyQuest,
    UseCraft,
    UseBarter,
    KeyOpens,
    KeyOpensOnMap,
    KeyUses,
    NotNeeded,

    // Where to get an item.
    SourceCash,
    SourceCashLocked,
    SourceBarter,
    SourceBarterLocked,
    SourceCraft,
    SourceCraftNoLevel,
    SourceFlea,
    SourceNone,

    // The raid's exits.
    ExtractOnMap,
    ExtractYouAt,
    ExtractAt,
    ExtractAtOffered,
    ExtractNoDistance,
    ExtractNeedsSwitch,
    ExtractNeedsKey,
    ExtractNeedsPayment,
    ExtractNeedsCoOp,
    ExtractNeedsNoBackpack,
    ExtractNeedsNoArmor,
    ExtractOneTime,
    ExtractTransit,
    ExtractStraightLines,
    ExtractNoScreenshot,

    // Ammunition.
    AmmoPick,
    AmmoPickPriced,
    AmmoRule,
    AmmoNoneGood,
    AmmoNoneUnderCap,

    // Any list cut short.
    More,
}

/// <summary>A side, as an extract answer's first line names it (key prefix <c>Ask.Side</c>).</summary>
[PhraseCodes("Ask.Side")]
public enum AskSideWord
{
    Pmc,
    Scav,
    Unknown,
}
