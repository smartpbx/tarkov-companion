using TarkovCompanion.Core.Domain.Loadouts;

namespace TarkovCompanion.App.Localization;

/// <summary>A loadout finding's words (#314): the evaluator names the check and what it is about.</summary>
public static partial class PlanText
{
    /// <summary>"M4A1 is not valid for the helmet slot.", "Armor is selected without any known plate selection."…</summary>
    public static string LoadoutFinding(LoadoutFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var key = PhraseText.Key(finding.Kind);
        return finding.Kind switch
        {
            LoadoutFindingKind.NotInCatalog or
            LoadoutFindingKind.MagazineDoesNotFitWeapon or
            LoadoutFindingKind.PlateDoesNotFit => UiText.Format(key, finding.Item),
            LoadoutFindingKind.WrongSlot => UiText.Format(key, finding.Item, SlotWord(finding)),
            LoadoutFindingKind.CaliberMismatch => UiText.Format(key, finding.Item, finding.ItemCaliber, finding.Other, finding.OtherCaliber),
            LoadoutFindingKind.MagazineCaliberMismatch => UiText.Format(key, finding.Item, finding.OtherCaliber),
            LoadoutFindingKind.WeakAmmunitionForKit => UiText.Format(key, finding.Tier, finding.Roubles),
            LoadoutFindingKind.AmmunitionNotObtainable or
            LoadoutFindingKind.ArmorWithoutPlates => UiText.Get(key),
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "No words for this loadout finding."),
        };
    }

    /// <summary>Why the finding was raised: which fact the check compared.</summary>
    public static string LoadoutFindingWhy(LoadoutFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var key = PhraseText.Key(finding.Kind) + ".Why";
        return finding.Kind switch
        {
            LoadoutFindingKind.WrongSlot => UiText.Format(key, finding.Category, SlotWord(finding)),
            LoadoutFindingKind.WeakAmmunitionForKit => UiText.Format(key, Or(finding.WeakTiers ?? []), finding.ThresholdRoubles),
            _ => UiText.Get(key),
        };
    }

    private static string SlotWord(LoadoutFinding finding) =>
        finding.Slot is { } slot ? PhraseText.Say(slot) : string.Empty;

    /// <summary>"B or C or D": each tier joined by the table's "or".</summary>
    private static string Or(IReadOnlyList<string> tiers) =>
        tiers.Count == 0 ? string.Empty : tiers.Skip(1).Aggregate(tiers[0], (joined, next) => UiText.Format("Plan.Loadout.TierOr", joined, next));
}
