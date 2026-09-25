using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>What a loot rule says about one item.</summary>
public enum LootRuleKind
{
    Pinned,
    Wishlist,
    AlwaysTake,
    AlwaysLeave,

    /// <summary>A rule this build cannot read: listed so it can still be removed.</summary>
    Unrecognised,
}

/// <summary>One choice the player made about an item, as the profile holds it.</summary>
public sealed record LootRuleEntry(string ItemId, LootRuleKind Kind);

/// <summary>Reads every loot choice out of a profile, in the order the list shows them.</summary>
/// <remarks>
/// The same meanings <see cref="LootScanProfileRules"/> gives a scan: a sell rule is about the
/// stash, not about picking something up, so it is not a loot rule and is not listed; "Protected"
/// is not something a Loot Scan sets, so it is left alone too.
/// </remarks>
public static class LootRules
{
    public static IReadOnlyList<LootRuleEntry> From(ProfileProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var entries = new List<LootRuleEntry>();
        entries.AddRange(progress.Pins
            .Where(pin => string.Equals(pin.TargetKind, LootScanProfileRules.ItemPinKind, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pin => pin.SortOrder)
            .Select(pin => new LootRuleEntry(pin.TargetId, LootRuleKind.Pinned)));
        entries.AddRange(progress.WishlistItemIds.Select(id => new LootRuleEntry(id, LootRuleKind.Wishlist)));
        foreach (var itemId in progress.ItemOverrides.Keys.Order(StringComparer.Ordinal))
        {
            var kind = LootScanProfileRules.RuleFor(progress, itemId) switch
            {
                LootScanItemRule.AlwaysTake => LootRuleKind.AlwaysTake,
                LootScanItemRule.AlwaysLeave => LootRuleKind.AlwaysLeave,
                LootScanItemRule.Unrecognised => LootRuleKind.Unrecognised,
                _ => (LootRuleKind?)null,
            };
            if (kind is { } rule)
            {
                entries.Add(new(itemId, rule));
            }
        }

        return entries;
    }
}

/// <summary>One row of Plan › Keep › Loot rules: the item, the choice, and Remove.</summary>
public sealed class LootRuleRowViewModel(LootRuleEntry entry, string name, Func<LootRuleEntry, Task> remove)
{
    public LootRuleEntry Entry { get; } = entry;

    public string ItemId => Entry.ItemId;

    public string Name { get; } = name;

    public string KindLabel => Entry.Kind switch
    {
        LootRuleKind.Pinned => PlanText.LootRulePinned,
        LootRuleKind.Wishlist => PlanText.LootRuleWishlist,
        LootRuleKind.AlwaysTake => PlanText.LootRuleAlwaysTake,
        LootRuleKind.AlwaysLeave => PlanText.LootRuleAlwaysLeave,
        _ => PlanText.LootRuleUnrecognised,
    };

    public bool IsLeave => Entry.Kind == LootRuleKind.AlwaysLeave;

    public string RemoveLabel => PlanText.LootRuleRemove;

    public string RemoveAutomationName => PlanText.LootRuleRemoveNamed(KindLabel, Name);

    public ICommand RemoveCommand { get; } = new AsyncDelegateCommand(() => remove(entry));
}

/// <summary>
/// Plan › Keep › Loot rules: every Pin, Wishlist, Always take and Always leave the player has set
/// from a Loot Scan, each with Remove.
/// </summary>
/// <remarks>
/// [#902 P9] These were written only by the buttons on a Loot Scan result, and nothing listed them,
/// so an "Always leave" pressed by mistake made every later scan say Leave for that item, and the
/// only way to clear it was to scan that exact item again in a raid. Removing goes through the same
/// <see cref="ILootScanWorkspaceControls"/> the scan writes with, so the scan on screen is decided
/// again at once.
/// </remarks>
public sealed class LootRulesViewModel : BindableViewModel
{
    private readonly IProfileRuntimeContextService _profiles;
    private readonly ILootScanWorkspaceControls _controls;
    private readonly Func<string, CancellationToken, Task<string?>> _nameOf;
    private readonly SynchronizationContext? _context;
    private IReadOnlyList<LootRuleRowViewModel> _rows = [];
    private bool _isOpen;
    private int _refreshGeneration;

    public LootRulesViewModel(
        IProfileRuntimeContextService profiles,
        ILootScanWorkspaceControls controls,
        Func<string, CancellationToken, Task<string?>> nameOf)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _controls = controls ?? throw new ArgumentNullException(nameof(controls));
        _nameOf = nameOf ?? throw new ArgumentNullException(nameof(nameOf));
        _context = SynchronizationContext.Current;
        ToggleCommand = new DelegateCommand(() => IsOpen = !IsOpen);
        _profiles.ContextChanged += ProfileChanged;
    }

    public IReadOnlyList<LootRuleRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
            {
                OnPropertyChanged(nameof(HasRows));
                OnPropertyChanged(nameof(HasNoRows));
                OnPropertyChanged(nameof(ButtonLabel));
            }
        }
    }

    public bool HasRows => Rows.Count > 0;

    public bool HasNoRows => !HasRows;

    /// <summary>Whether the Keep page shows the rules instead of the keep list.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetProperty(ref _isOpen, value))
            {
                OnPropertyChanged(nameof(IsClosed));
                if (value)
                {
                    RefreshAsync().Observe("keep", "refresh loot rules");
                }
            }
        }
    }

    public bool IsClosed => !IsOpen;

    public string ButtonLabel => HasRows ? PlanText.LootRulesCount(Rows.Count) : PlanText.LootRules;

    public string Heading => PlanText.LootRules;

    public string Hint => PlanText.LootRulesHint;

    public string Empty => PlanText.LootRulesEmpty;

    public ICommand ToggleCommand { get; }

    public void Open() => IsOpen = true;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var entries = _profiles.Current.ActiveProfile is { } profile
            ? LootRules.From(profile.Progress)
            : [];
        var rows = new List<LootRuleRowViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            var name = await _nameOf(entry.ItemId, cancellationToken).ConfigureAwait(true) ?? entry.ItemId;
            rows.Add(new(entry, name, RemoveAsync));
        }

        if (generation == Volatile.Read(ref _refreshGeneration))
        {
            Rows = rows;
        }
    }

    /// <summary>Removes one choice: the item goes back to being decided on its price and needs.</summary>
    public async Task RemoveAsync(LootRuleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await (entry.Kind switch
        {
            LootRuleKind.Pinned => _controls.SetPinnedAsync(entry.ItemId, false),
            LootRuleKind.Wishlist => _controls.SetWishlistedAsync(entry.ItemId, false),
            _ => _controls.SetRuleAsync(entry.ItemId, LootScanItemRule.None),
        }).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private void ProfileChanged(ProfileRuntimeContextChanged change)
    {
        if (_context is { } context && SynchronizationContext.Current != context)
        {
            context.Post(_ => RefreshAsync().Observe("keep", "refresh loot rules"), null);
            return;
        }

        RefreshAsync().Observe("keep", "refresh loot rules");
    }
}
