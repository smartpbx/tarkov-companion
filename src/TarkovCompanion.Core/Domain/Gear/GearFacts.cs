using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.Core.Domain.Gear;

/// <summary>
/// What the catalog states about one piece of armor, plate, helmet, headset, rig, bag or medicine.
/// </summary>
/// <remarks>
/// Every figure is null when the source does not state it. A missing armor class is not class 0 and
/// a missing durability is not a broken item, so nothing here is defaulted, and a caller that adds
/// figures up has to say how many it had. The numbers are the source's own and carry no opinion:
/// what a headset sounds like or whether a helmet is worth its price is a different, subjective
/// kind of statement that belongs to a dated, sourced note and not to this record. Penalties are
/// fractions as the source gives them (-0.09 is nine percent).
/// </remarks>
public sealed record GearFacts(
    string ItemId,
    ItemCategory Category,
    int? ArmorClass,
    double? Durability,
    string? Material,
    string? ArmorType,
    IReadOnlyList<string> Zones,
    double? BluntThroughput,
    double? BlindnessProtection,
    double? SpeedPenalty,
    double? TurnPenalty,
    double? ErgoPenalty,
    int? CarryCells,
    int? Uses,
    double? UseTimeSeconds,
    IReadOnlyList<string> Cures,
    HeadsetAudio? Audio,
    IReadOnlyList<PlateSlot> PlateSlots,
    DataProvenance Provenance);

/// <summary>A place on a body armor that takes a plate, and which plates the source says fit it.</summary>
public sealed record PlateSlot(string SlotId, IReadOnlyList<string> Zones, IReadOnlySet<string> AllowedPlateItemIds);

/// <summary>A headset's audio settings, as the source states them and unrated.</summary>
public sealed record HeadsetAudio(
    double? AmbientVolume,
    double? CompressorGain,
    double? CompressorThreshold,
    double? CompressorAttack,
    double? CompressorRelease,
    double? DistanceModifier,
    double? Distortion,
    double? DryVolume);
