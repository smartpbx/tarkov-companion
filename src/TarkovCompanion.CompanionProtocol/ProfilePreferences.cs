using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>The independently versioned schema carried by profile preference state.</summary>
public readonly record struct PreferenceSchemaVersion
{
    public const int MaxMajor = 99;
    public const int MaxMinor = 999;

    [JsonConstructor]
    public PreferenceSchemaVersion(int major, int minor)
    {
        Major = major is >= 1 and <= MaxMajor
            ? major
            : throw new ArgumentOutOfRangeException(nameof(major));
        Minor = minor is >= 0 and <= MaxMinor
            ? minor
            : throw new ArgumentOutOfRangeException(nameof(minor));
    }

    public int Major { get; }

    public int Minor { get; }

    public static PreferenceSchemaVersion Current { get; } = new(1, 1);

    public bool CanRead(PreferenceSchemaVersion value) =>
        value.Major == Major && value.Minor <= Minor;
}

public readonly record struct PreferenceProfileId
{
    [JsonConstructor]
    public PreferenceProfileId(Guid value) => Value = ProtocolGuard.Id(value, nameof(value));

    public Guid Value { get; }
}

public enum PreferenceSchemaCompatibility
{
    Current = 1,
    Migratable,
    Unsupported,
}

public static class PreferenceSchemaPolicy
{
    public static PreferenceSchemaCompatibility Classify(PreferenceSchemaVersion source) =>
        source == PreferenceSchemaVersion.Current
            ? PreferenceSchemaCompatibility.Current
            : PreferenceSchemaVersion.Current.CanRead(source)
                ? PreferenceSchemaCompatibility.Migratable
                : PreferenceSchemaCompatibility.Unsupported;

    /// <summary>
    /// Migrates a readable document without interpreting unknown fields. Version 1.1 added explicit
    /// shared-personalization entries; a 1.0 document normalizes with its existing (normally empty)
    /// list. Later migrations append another reviewed step here rather than replacing this one.
    /// </summary>
    public static ProfilePreferencesDocument Normalize(ProfilePreferencesDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Classify(source.SchemaVersion) == PreferenceSchemaCompatibility.Unsupported)
        {
            throw new NotSupportedException("The preference schema is outside the readable migration window.");
        }

        return new ProfilePreferencesDocument(
            source.Context,
            PreferenceSchemaVersion.Current,
            source.Items,
            source.ProtectedItemRules,
            source.RecommendationOverrides,
            source.FavoriteLoadouts,
            source.SharedPersonalization);
    }
}

/// <summary>
/// The complete context key that prevents a preference mutation from crossing profile, mode, wipe,
/// locale, or data-snapshot boundaries. It deliberately does not depend on a device-local display
/// name or presentation setting.
/// </summary>
public sealed record PreferenceProfileContext
{
    public PreferenceProfileContext(
        PreferenceProfileId profileId,
        string generation,
        ProfileGameMode mode,
        string wipeSeason,
        string language,
        string region,
        string timeZone,
        string dataSnapshotId,
        DateTimeOffset dataSnapshotPublishedUtc)
    {
        ProfileId = profileId.Value == Guid.Empty
            ? throw new ArgumentException("A preference profile id is required.", nameof(profileId))
            : profileId;
        Generation = ProtocolGuard.Required(generation, nameof(generation), ProtocolBounds.MaxShortStringBytes);
        Mode = mode is ProfileGameMode.Pvp or ProfileGameMode.Pve or ProfileGameMode.Seasonal
            ? mode
            : throw new ArgumentOutOfRangeException(nameof(mode), "A paired preference context requires a concrete game mode.");
        WipeSeason = ProtocolGuard.Required(wipeSeason, nameof(wipeSeason), ProtocolBounds.MaxShortStringBytes);
        Language = ProtocolGuard.Required(language, nameof(language), 35);
        Region = ProtocolGuard.Required(region, nameof(region), 16).ToUpperInvariant();
        TimeZone = ProtocolGuard.Required(timeZone, nameof(timeZone), ProtocolBounds.MaxShortStringBytes);
        DataSnapshotId = ProtocolGuard.Required(dataSnapshotId, nameof(dataSnapshotId), ProtocolBounds.MaxShortStringBytes);
        DataSnapshotPublishedUtc = ProtocolGuard.Utc(dataSnapshotPublishedUtc, nameof(dataSnapshotPublishedUtc));

        // Reuse the domain grammar as well as the wire bounds so protocol and persistence cannot
        // disagree about what constitutes one profile context.
        _ = new ProfileContext(
            new ProfileIdentity(ProfileId.Value, Generation),
            Mode,
            new WipeSeason(WipeSeason),
            new ProfileLocale(Language, Region, TimeZone),
            new DataSnapshotContext(DataSnapshotId, DataSnapshotPublishedUtc));
    }

    public PreferenceProfileId ProfileId { get; }

    public string Generation { get; }

    public ProfileGameMode Mode { get; }

    public string WipeSeason { get; }

    public string Language { get; }

    public string Region { get; }

    public string TimeZone { get; }

    public string DataSnapshotId { get; }

    public DateTimeOffset DataSnapshotPublishedUtc { get; }

    public static PreferenceProfileContext From(ProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new PreferenceProfileContext(
            new PreferenceProfileId(context.Identity.ProfileId),
            context.Identity.Generation,
            context.Mode,
            context.WipeSeason.Value,
            context.Locale.Language,
            context.Locale.Region,
            context.Locale.TimeZone,
            context.DataSnapshot.SnapshotId,
            context.DataSnapshot.PublishedUtc);
    }
}

public sealed record ItemPreference
{
    public ItemPreference(string itemId, bool pinned, bool wishlist, int sortOrder)
    {
        ItemId = ProtocolGuard.Required(itemId, nameof(itemId), ProtocolBounds.MaxShortStringBytes);
        if (!pinned && !wishlist)
        {
            throw new ArgumentException("An item preference must pin or wishlist the item.");
        }

        Pinned = pinned;
        Wishlist = wishlist;
        SortOrder = sortOrder is >= 0 and <= ProtocolBounds.MaxPreferenceSortOrder
            ? sortOrder
            : throw new ArgumentOutOfRangeException(nameof(sortOrder));
    }

    public string ItemId { get; }

    public bool Pinned { get; }

    public bool Wishlist { get; }

    public int SortOrder { get; }
}

public enum ProtectedItemSelectorKind
{
    Item = 1,
    Category,
    Tag,
}

public enum ProtectedItemDisposition
{
    AlwaysKeep = 1,
    AskBeforeDiscard,
}

public sealed record ProtectedItemRule
{
    public ProtectedItemRule(
        string ruleId,
        ProtectedItemSelectorKind selectorKind,
        string selectorId,
        ProtectedItemDisposition disposition,
        int minimumQuantity)
    {
        RuleId = ProtocolGuard.Required(ruleId, nameof(ruleId), ProtocolBounds.MaxShortStringBytes);
        SelectorKind = ProtocolGuard.Defined(selectorKind, nameof(selectorKind));
        SelectorId = ProtocolGuard.Required(selectorId, nameof(selectorId), ProtocolBounds.MaxShortStringBytes);
        Disposition = ProtocolGuard.Defined(disposition, nameof(disposition));
        MinimumQuantity = minimumQuantity is >= 0 and <= ProtocolBounds.MaxPreferenceQuantity
            ? minimumQuantity
            : throw new ArgumentOutOfRangeException(nameof(minimumQuantity));
    }

    public string RuleId { get; }

    public ProtectedItemSelectorKind SelectorKind { get; }

    public string SelectorId { get; }

    public ProtectedItemDisposition Disposition { get; }

    public int MinimumQuantity { get; }
}

public enum RecommendationOverrideAction
{
    Keep = 1,
    Prioritize,
    Sell,
    Ignore,
}

public sealed record RecommendationOverride
{
    public RecommendationOverride(string itemId, RecommendationOverrideAction action, string? reason)
    {
        ItemId = ProtocolGuard.Required(itemId, nameof(itemId), ProtocolBounds.MaxShortStringBytes);
        Action = ProtocolGuard.Defined(action, nameof(action));
        Reason = ProtocolGuard.Optional(reason, nameof(reason));
    }

    public string ItemId { get; }

    public RecommendationOverrideAction Action { get; }

    public string? Reason { get; }
}

public sealed record FavoriteLoadoutItem
{
    public FavoriteLoadoutItem(string slotId, string itemId, int quantity)
    {
        SlotId = ProtocolGuard.Required(slotId, nameof(slotId), ProtocolBounds.MaxShortStringBytes);
        ItemId = ProtocolGuard.Required(itemId, nameof(itemId), ProtocolBounds.MaxShortStringBytes);
        Quantity = quantity is >= 1 and <= ProtocolBounds.MaxPreferenceQuantity
            ? quantity
            : throw new ArgumentOutOfRangeException(nameof(quantity));
    }

    public string SlotId { get; }

    public string ItemId { get; }

    public int Quantity { get; }
}

public sealed record FavoriteLoadout
{
    public FavoriteLoadout(string loadoutId, string name, IReadOnlyList<FavoriteLoadoutItem> items)
    {
        LoadoutId = ProtocolGuard.Required(loadoutId, nameof(loadoutId), ProtocolBounds.MaxShortStringBytes);
        Name = ProtocolGuard.Required(name, nameof(name), ProtocolBounds.MaxShortStringBytes);
        var sorted = ProtocolGuard.List(items, nameof(items), ProtocolBounds.MaxFavoriteLoadoutItems)
            .OrderBy(item => item.SlotId, StringComparer.Ordinal)
            .ToArray();
        if (sorted.Select(item => item.SlotId).Distinct(StringComparer.Ordinal).Count() != sorted.Length)
        {
            throw new ArgumentException("A favorite loadout has one item per slot.", nameof(items));
        }

        Items = Array.AsReadOnly(sorted);
    }

    public string LoadoutId { get; }

    public string Name { get; }

    public IReadOnlyList<FavoriteLoadoutItem> Items { get; }
}

public enum SharedPersonalizationKind
{
    Loadout = 1,
    Plan,
    ObjectiveSet,
    Route,
}

/// <summary>An explicit opt-in to publish one non-device-local preference through a team adapter.</summary>
public sealed record SharedPersonalization
{
    public SharedPersonalization(SharedPersonalizationKind kind, string referenceId, bool enabled)
    {
        Kind = ProtocolGuard.Defined(kind, nameof(kind));
        ReferenceId = ProtocolGuard.Required(referenceId, nameof(referenceId), ProtocolBounds.MaxShortStringBytes);
        Enabled = enabled;
    }

    public SharedPersonalizationKind Kind { get; }

    public string ReferenceId { get; }

    public bool Enabled { get; }
}

/// <summary>
/// One active profile's canonical, bounded preference document. Device-local presentation and
/// accessibility settings never enter this document; ephemeral pings remain in the marks aggregate.
/// </summary>
public sealed record ProfilePreferencesDocument
{
    public ProfilePreferencesDocument(
        PreferenceProfileContext context,
        PreferenceSchemaVersion schemaVersion,
        IReadOnlyList<ItemPreference> items,
        IReadOnlyList<ProtectedItemRule> protectedItemRules,
        IReadOnlyList<RecommendationOverride> recommendationOverrides,
        IReadOnlyList<FavoriteLoadout> favoriteLoadouts,
        IReadOnlyList<SharedPersonalization>? sharedPersonalization = null)
    {
        Context = ProtocolGuard.NotNull(context, nameof(context));
        SchemaVersion = schemaVersion;
        Items = DistinctSorted(items, item => item.ItemId, nameof(items), ProtocolBounds.MaxProfilePreferenceItems);
        ProtectedItemRules = DistinctSorted(
            protectedItemRules,
            rule => rule.RuleId,
            nameof(protectedItemRules),
            ProtocolBounds.MaxProtectedItemRules);
        RecommendationOverrides = DistinctSorted(
            recommendationOverrides,
            value => value.ItemId,
            nameof(recommendationOverrides),
            ProtocolBounds.MaxRecommendationOverrides);
        FavoriteLoadouts = DistinctSorted(
            favoriteLoadouts,
            loadout => loadout.LoadoutId,
            nameof(favoriteLoadouts),
            ProtocolBounds.MaxFavoriteLoadouts);
        var sortedShared = ProtocolGuard.List(
                sharedPersonalization ?? [],
                nameof(sharedPersonalization),
                ProtocolBounds.MaxSharedPersonalization)
            .OrderBy(value => value.Kind)
            .ThenBy(value => value.ReferenceId, StringComparer.Ordinal)
            .ToArray();
        if (sortedShared.Select(value => (value.Kind, value.ReferenceId)).Distinct().Count() != sortedShared.Length)
        {
            throw new ArgumentException("Shared personalization keys are unique.", nameof(sharedPersonalization));
        }

        SharedPersonalization = Array.AsReadOnly(sortedShared);
    }

    public PreferenceProfileContext Context { get; }

    public PreferenceSchemaVersion SchemaVersion { get; }

    public IReadOnlyList<ItemPreference> Items { get; }

    public IReadOnlyList<ProtectedItemRule> ProtectedItemRules { get; }

    public IReadOnlyList<RecommendationOverride> RecommendationOverrides { get; }

    public IReadOnlyList<FavoriteLoadout> FavoriteLoadouts { get; }

    public IReadOnlyList<SharedPersonalization> SharedPersonalization { get; }

    public static ProfilePreferencesDocument Empty(PreferenceProfileContext context) =>
        new(context, PreferenceSchemaVersion.Current, [], [], [], [], []);

    private static IReadOnlyList<T> DistinctSorted<T>(
        IReadOnlyList<T> values,
        Func<T, string> key,
        string parameterName,
        int maximum)
    {
        var copy = ProtocolGuard.List(values, parameterName, maximum)
            .OrderBy(key, StringComparer.Ordinal)
            .ToArray();
        if (copy.Select(key).Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("Preference identifiers are unique within their collection.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }
}

public sealed record ProfilePreferencesAggregate
{
    public ProfilePreferencesAggregate(
        AggregateCursor cursor,
        ProfilePreferencesDocument? activeProfile,
        WorkspaceOrigin? lastChangedOrigin,
        DateTimeOffset? changedUtc)
    {
        Cursor = ProtocolGuard.NotNull(cursor, nameof(cursor));
        ActiveProfile = activeProfile;
        LastChangedOrigin = lastChangedOrigin;
        ChangedUtc = ProtocolGuard.UtcOptional(changedUtc, nameof(changedUtc));
        if (activeProfile is not null && activeProfile.SchemaVersion != PreferenceSchemaVersion.Current)
        {
            throw new ArgumentException("Canonical preference state uses the current normalized schema.", nameof(activeProfile));
        }

        if (cursor.Revision.Value == 0 && activeProfile is not null)
        {
            throw new ArgumentException("Revision-zero preference state cannot contain an active profile.", nameof(activeProfile));
        }

        var hasAttribution = lastChangedOrigin is not null && ChangedUtc is not null;
        if ((cursor.Revision.Value == 0 && (lastChangedOrigin is not null || ChangedUtc is not null)) ||
            (cursor.Revision.Value > 0 && !hasAttribution))
        {
            throw new ArgumentException("A positive preference revision carries origin and UTC; revision zero carries neither.");
        }
    }

    public AggregateCursor Cursor { get; }

    public ProfilePreferencesDocument? ActiveProfile { get; }

    public WorkspaceOrigin? LastChangedOrigin { get; }

    public DateTimeOffset? ChangedUtc { get; }

    public static ProfilePreferencesAggregate Empty { get; } = new(AggregateCursor.Empty, null, null, null);

    internal ProfilePreferencesAggregate WithAttribution(WorkspaceOrigin origin, DateTimeOffset changedUtc) =>
        new(Cursor, ActiveProfile, ProtocolGuard.NotNull(origin, nameof(origin)), changedUtc);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetItemPreferenceMutation), "setItem")]
[JsonDerivedType(typeof(RemoveItemPreferenceMutation), "removeItem")]
[JsonDerivedType(typeof(UpsertProtectedItemRuleMutation), "upsertProtectedRule")]
[JsonDerivedType(typeof(DeleteProtectedItemRuleMutation), "deleteProtectedRule")]
[JsonDerivedType(typeof(SetRecommendationOverrideMutation), "setRecommendationOverride")]
[JsonDerivedType(typeof(DeleteRecommendationOverrideMutation), "deleteRecommendationOverride")]
[JsonDerivedType(typeof(UpsertFavoriteLoadoutMutation), "upsertFavoriteLoadout")]
[JsonDerivedType(typeof(DeleteFavoriteLoadoutMutation), "deleteFavoriteLoadout")]
[JsonDerivedType(typeof(SetSharedPersonalizationMutation), "setSharedPersonalization")]
[JsonDerivedType(typeof(DeleteSharedPersonalizationMutation), "deleteSharedPersonalization")]
public abstract record ProfilePreferenceMutation
{
    private protected ProfilePreferenceMutation()
    {
    }
}

public sealed record SetItemPreferenceMutation(ItemPreference Preference) : ProfilePreferenceMutation
{
    public ItemPreference Preference { get; } = ProtocolGuard.NotNull(Preference, nameof(Preference));
}

public sealed record RemoveItemPreferenceMutation(string ItemId) : ProfilePreferenceMutation
{
    public string ItemId { get; } = ProtocolGuard.Required(ItemId, nameof(ItemId), ProtocolBounds.MaxShortStringBytes);
}

public sealed record UpsertProtectedItemRuleMutation(ProtectedItemRule Rule) : ProfilePreferenceMutation
{
    public ProtectedItemRule Rule { get; } = ProtocolGuard.NotNull(Rule, nameof(Rule));
}

public sealed record DeleteProtectedItemRuleMutation(string RuleId) : ProfilePreferenceMutation
{
    public string RuleId { get; } = ProtocolGuard.Required(RuleId, nameof(RuleId), ProtocolBounds.MaxShortStringBytes);
}

public sealed record SetRecommendationOverrideMutation(RecommendationOverride Override) : ProfilePreferenceMutation
{
    public RecommendationOverride Override { get; } = ProtocolGuard.NotNull(Override, nameof(Override));
}

public sealed record DeleteRecommendationOverrideMutation(string ItemId) : ProfilePreferenceMutation
{
    public string ItemId { get; } = ProtocolGuard.Required(ItemId, nameof(ItemId), ProtocolBounds.MaxShortStringBytes);
}

public sealed record UpsertFavoriteLoadoutMutation(FavoriteLoadout Loadout) : ProfilePreferenceMutation
{
    public FavoriteLoadout Loadout { get; } = ProtocolGuard.NotNull(Loadout, nameof(Loadout));
}

public sealed record DeleteFavoriteLoadoutMutation(string LoadoutId) : ProfilePreferenceMutation
{
    public string LoadoutId { get; } = ProtocolGuard.Required(LoadoutId, nameof(LoadoutId), ProtocolBounds.MaxShortStringBytes);
}

public sealed record SetSharedPersonalizationMutation(SharedPersonalization Value) : ProfilePreferenceMutation
{
    public SharedPersonalization Value { get; } = ProtocolGuard.NotNull(Value, nameof(Value));
}

public sealed record DeleteSharedPersonalizationMutation(SharedPersonalizationKind Kind, string ReferenceId) : ProfilePreferenceMutation
{
    public SharedPersonalizationKind Kind { get; } = ProtocolGuard.Defined(Kind, nameof(Kind));

    public string ReferenceId { get; } = ProtocolGuard.Required(ReferenceId, nameof(ReferenceId), ProtocolBounds.MaxShortStringBytes);
}

/// <summary>Desktop-only activation or profile switch; tablet mutations are always field-scoped.</summary>
public sealed record ActivateProfilePreferencesCommand : CompanionCommand
{
    public ActivateProfilePreferencesCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        ProfilePreferencesDocument preferences)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null) =>
        Preferences = ProtocolGuard.NotNull(preferences, nameof(preferences));

    public ProfilePreferencesDocument Preferences { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.ProfilePreferences;
}

public sealed record MutateProfilePreferencesCommand : CompanionCommand
{
    public MutateProfilePreferencesCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        PreferenceProfileContext context,
        PreferenceSchemaVersion schemaVersion,
        ProfilePreferenceMutation mutation)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null)
    {
        Context = ProtocolGuard.NotNull(context, nameof(context));
        SchemaVersion = schemaVersion;
        Mutation = ProtocolGuard.NotNull(mutation, nameof(mutation));
    }

    public PreferenceProfileContext Context { get; }

    public PreferenceSchemaVersion SchemaVersion { get; }

    public ProfilePreferenceMutation Mutation { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.ProfilePreferences;
}

public sealed record ResetProfilePreferencesCommand : CompanionCommand
{
    public ResetProfilePreferencesCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        PreferenceProfileContext context,
        PreferenceSchemaVersion schemaVersion)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null)
    {
        Context = ProtocolGuard.NotNull(context, nameof(context));
        SchemaVersion = schemaVersion;
    }

    public PreferenceProfileContext Context { get; }

    public PreferenceSchemaVersion SchemaVersion { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.ProfilePreferences;
}

public sealed record DeleteProfilePreferencesCommand : CompanionCommand
{
    public DeleteProfilePreferencesCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        PreferenceProfileContext context,
        PreferenceSchemaVersion schemaVersion)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null)
    {
        Context = ProtocolGuard.NotNull(context, nameof(context));
        SchemaVersion = schemaVersion;
    }

    public PreferenceProfileContext Context { get; }

    public PreferenceSchemaVersion SchemaVersion { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.ProfilePreferences;
}

public sealed record ProfilePreferencesCanonicalUpdate : CanonicalUpdate
{
    public ProfilePreferencesCanonicalUpdate(
        AuthorityEpoch authorityEpoch,
        GlobalRevision globalRevision,
        CommandId changeId,
        DateTimeOffset changedUtc,
        WorkspaceOrigin origin,
        V2ContractVersion contractVersion,
        ProfilePreferencesAggregate state)
        : base(authorityEpoch, globalRevision, changeId, changedUtc, origin, contractVersion)
    {
        State = ProtocolGuard.NotNull(state, nameof(state));
        if (State.Cursor.LastChangeId != changeId ||
            State.LastChangedOrigin != origin ||
            State.ChangedUtc != changedUtc)
        {
            throw new ArgumentException("A preference update and its snapshot attribution describe the same change.", nameof(state));
        }
    }

    public ProfilePreferencesAggregate State { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.ProfilePreferences;
}
