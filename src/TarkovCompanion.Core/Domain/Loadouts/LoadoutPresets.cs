namespace TarkovCompanion.Core.Domain.Loadouts;

/// <summary>One item in a saved kit, with the slot it stood in.</summary>
/// <remarks>
/// The name is stored beside the id so a preset list reads without a catalog round trip, and so a
/// kit saved before a wipe still says what it held when the item is no longer in the catalog. The
/// id remains the authority for anything the evaluation does.
/// </remarks>
/// <param name="Slot">The slot's stable key, as the presentation layer names it.</param>
/// <param name="ItemId">The catalog id, which is what an evaluation reads.</param>
/// <param name="Name">What it was called when it was saved.</param>
public sealed record LoadoutPresetItem(string Slot, string ItemId, string Name);

/// <summary>
/// A kit somebody assembled and wants back.
/// </summary>
/// <remarks>
/// Deliberately a flat list rather than a <see cref="LoadoutSelection"/>: the selection type
/// collapses a many-item slot into an unnamed list and drops the slot a single item stood in,
/// which is exactly what a preset has to keep in order to be restored into the same board.
/// </remarks>
/// <param name="Name">What the player called it; also its identity.</param>
/// <param name="SavedUtc">When it was saved, in UTC as everything persisted here is.</param>
/// <param name="Items">The kit, in slot order.</param>
public sealed record LoadoutPreset(string Name, DateTimeOffset SavedUtc, IReadOnlyList<LoadoutPresetItem> Items)
{
    /// <summary>The schema this build writes and the only one it reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>How many kits are kept, oldest dropped first.</summary>
    /// <remarks>
    /// A cap rather than unbounded growth: this file is read on every visit to the page, and a
    /// player who saves a kit per raid would otherwise be reading a megabyte of them by the end
    /// of a wipe.
    /// </remarks>
    public const int MaximumPresets = 24;

    /// <summary>Whether the name can identify a kit at all.</summary>
    public static bool IsUsableName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 64;
}
