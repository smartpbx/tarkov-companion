using TarkovCompanion.Core.Domain.Ammo;

namespace TarkovCompanion.App.Localization;

// Intel > Ammo (#314), and the V1 AmmoPageViewModel and OwnedAmmo lines that tab shows.
public static partial class IntelText
{
    public static string AmmoTitle => UiText.Get("Intel.Ammo.Title");
    public static string AmmoSubtitle => UiText.Get("Intel.Ammo.Subtitle");
    public static string AmmoNotLoaded => UiText.Get("Intel.Ammo.NotLoaded");
    public static string AmmoFilterCalibers => UiText.Get("Intel.Ammo.FilterCalibers");
    public static string AmmoReloadAmmunition => UiText.Get("Intel.Ammo.ReloadAmmunition");
    public static string AmmoReload => UiText.Get("Intel.Ammo.Reload");
    public static string AmmoNoCalibers => UiText.Get("Intel.Ammo.NoCalibers");
    public static string AmmoBeats => UiText.Get("Intel.Ammo.Beats");
    public static string AmmoOrder => UiText.Get("Intel.Ammo.Order");
    public static string AmmoHeadRound => UiText.Get("Intel.Ammo.HeadRound");
    public static string AmmoHeadDamage => UiText.Get("Intel.Ammo.HeadDamage");
    public static string AmmoHeadPen => UiText.Get("Intel.Ammo.HeadPen");
    public static string AmmoHeadArmorDamage => UiText.Get("Intel.Ammo.HeadArmorDamage");
    public static string AmmoHeadFrag => UiText.Get("Intel.Ammo.HeadFrag");
    public static string AmmoHeadTier => UiText.Get("Intel.Ammo.HeadTier");
    public static string AmmoHeadClasses => UiText.Get("Intel.Ammo.HeadClasses");
    public static string AmmoOpenInIntel => UiText.Get("Intel.Ammo.OpenInIntel");
    public static string AmmoPricesAndNeeds => UiText.Get("Intel.Ammo.PricesAndNeeds");
    public static string AmmoAgainstArmor => UiText.Get("Intel.Ammo.AgainstArmor");
    public static string AmmoRound => UiText.Get("Intel.Ammo.Round");
    public static string AmmoTier => UiText.Get("Intel.Ammo.Tier");
    public static string AmmoAnyArmor => UiText.Get("Intel.Ammo.AnyArmor");
    public static string AmmoClass(int armorClass) => UiText.Format("Intel.Ammo.Class", armorClass);
    public static string AmmoSortPenetration => UiText.Get("Intel.Ammo.Sort.Penetration");
    public static string AmmoSortDamage => UiText.Get("Intel.Ammo.Sort.Damage");
    public static string AmmoSortName => UiText.Get("Intel.Ammo.Sort.Name");
    public static string AmmoSortBestFirst => UiText.Get("Intel.Ammo.Sort.BestFirst");
    public static string AmmoNoneBeatsClass(int armorClass) => UiText.Format("Intel.Ammo.NoneBeatsClass", armorClass);
    public static string AmmoPickCaliber => UiText.Get("Intel.Ammo.PickCaliber");
    public static string AmmoNoRoundsCached => UiText.Get("Intel.Ammo.NoRoundsCached");
    public static string AmmoRoundCount(long count) => UiText.Plural("Intel.Ammo.RoundCount", count);
    public static string AmmoRoundsShownOf(int shown, int total) => UiText.Format("Intel.Ammo.RoundsShownOf", shown, total);
    public static string AmmoNoRoundSelected => UiText.Get("Intel.Ammo.NoRoundSelected");
    public static string AmmoLoading => UiText.Get("Intel.Ammo.Loading");
    public static string AmmoPickACaliber => UiText.Get("Intel.Ammo.PickACaliber");
    public static string AmmoNoRoundHeading => UiText.Get("Intel.Ammo.NoRoundHeading");
    public static string AmmoHeuristicNotice => UiText.Get("Intel.Ammo.HeuristicNotice");
    public static string AmmoEvidence(object availability, int itemCount) => UiText.Format("Intel.Ammo.Evidence", availability, itemCount);
    public static string AmmoReading => UiText.Get("Intel.Ammo.Reading");
    public static string AmmoNoneCached => UiText.Get("Intel.Ammo.NoneCached");
    public static string AmmoCaliberSummary(int rounds, int bestPenetration) => UiText.Format("Intel.Ammo.CaliberSummary", rounds, bestPenetration);
    public static string AmmoStatus(int calibers, int rounds) => UiText.Format("Intel.Ammo.Status", calibers, rounds);
    public static string AmmoUnreadable(string reason) => UiText.Format("Intel.Ammo.Unreadable", reason);
    public static string AmmoTableNotLoaded => UiText.Get("Intel.Ammo.TableNotLoaded");
    public static string AmmoRanking(string caliber) => UiText.Format("Intel.Ammo.Ranking", caliber);
    public static string AmmoNoRoundsFor(string caliber) => UiText.Format("Intel.Ammo.NoRoundsFor", caliber);
    public static string AmmoRoundsIn(int rounds, string caliber) => UiText.Format("Intel.Ammo.RoundsIn", rounds, caliber);
    public static string AmmoRank(int rank, int total) => UiText.Format("Intel.Ammo.Rank", rank, total);
    public static string AmmoPercent(int percent) => UiText.Format("Intel.Ammo.Percent", percent);
    public static string AmmoNotStated => UiText.Get("Intel.Ammo.NotStated");
    public static string AmmoProvenance(string updated, double confidence) => UiText.Format("Intel.Ammo.Provenance", updated, confidence);
    public static string AmmoNoRating => UiText.Get("Intel.Ammo.NoRating");
    public static string AmmoRating(ArmorEffectiveness rating) => rating switch
    {
        ArmorEffectiveness.Excellent => UiText.Get("Intel.Ammo.Rating.Excellent"),
        ArmorEffectiveness.Good => UiText.Get("Intel.Ammo.Rating.Good"),
        ArmorEffectiveness.Fair => UiText.Get("Intel.Ammo.Rating.Fair"),
        ArmorEffectiveness.Limited => UiText.Get("Intel.Ammo.Rating.Limited"),
        ArmorEffectiveness.Poor => UiText.Get("Intel.Ammo.Rating.Poor"),
        _ => rating.ToString(),
    };
    public static string AmmoProjectiles(int count) => UiText.Format("Intel.Ammo.Projectiles", count);
    public static string AmmoVelocity(double metresPerSecond) => UiText.Format("Intel.Ammo.Velocity", metresPerSecond);
    public static string AmmoRecoil(double modifier) => UiText.Format(modifier > 0 ? "Intel.Ammo.RecoilAdded" : "Intel.Ammo.Recoil", modifier);
    public static string AmmoSubsonic => UiText.Get("Intel.Ammo.Subsonic");
    public static string AmmoTracer => UiText.Get("Intel.Ammo.Tracer");
    public static string AmmoNoTraits => UiText.Get("Intel.Ammo.NoTraits");
    public static string AmmoNoTimestamp => UiText.Get("Intel.Ammo.NoTimestamp");
    public static string AmmoNoneOwned => UiText.Get("Intel.Ammo.NoneOwned");
    public static string AmmoOwnedShort(int rounds) => UiText.Format("Intel.Ammo.OwnedShort", rounds);
    public static string AmmoOwnedNotScanned => UiText.Get("Intel.Ammo.OwnedNotScanned");
    public static string AmmoYouOwnNone => UiText.Get("Intel.Ammo.YouOwnNone");
    public static string AmmoYouOwnRounds(int rounds) => UiText.Format("Intel.Ammo.YouOwnRounds", rounds);
}
