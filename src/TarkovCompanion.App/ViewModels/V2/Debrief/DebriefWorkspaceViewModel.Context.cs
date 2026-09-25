using System.Globalization;
using TarkovCompanion.App.Localization;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.App.ViewModels.V2.Debrief;

/// <summary>
/// [#269] Debrief lists and counts the active profile's raids in its mode and wipe, and says so.
/// </summary>
/// <remarks>
/// The raid table holds every profile's raids. Before this, a PvE raid, another profile's raid and
/// last wipe's raids all went into one survival rate and one "By map" strip, which described no
/// game anybody played. They are hidden, not deleted, and one button shows everything again, with
/// the status line saying the totals now mix contexts.
/// </remarks>
public sealed partial class DebriefWorkspaceViewModel
{
    private readonly IRaidContextSource? _raidContext;
    private RaidContextView _context = RaidContextView.None;
    private bool _showAllContexts;
    private ICommand? _toggleContext;

    /// <summary>Every raid, or only the active context's while "Show all" is off.</summary>
    private IEnumerable<DebriefRaidRecord> ContextRecords => _showAllContexts || _context.Active is null
        ? _allRecords
        : _allRecords.Where(record => record.Match == RaidContextMatch.Same);

    /// <summary>Whether raids from other profiles, modes and wipes are listed and counted too.</summary>
    public bool ShowAllContexts
    {
        get => _showAllContexts;
        set
        {
            if (SetProperty(ref _showAllContexts, value))
            {
                ApplyFilters();
            }
        }
    }

    /// <summary>Raids the active context leaves out.</summary>
    public int OtherContextCount => _context.Active is null ? 0 : _allRecords.Count(record => record.Match != RaidContextMatch.Same);

    public bool HasOtherContexts => OtherContextCount > 0 || ShowAllContexts;

    public string ContextToggleLabel => ShowAllContexts
        ? DebriefText.ThisProfileOnly
        : DebriefText.ShowAll(OtherContextCount);

    public ICommand ToggleContextCommand => _toggleContext ??= new DelegateCommand(() => ShowAllContexts = !ShowAllContexts);

    public string SelectedWipeLabel => SelectedRecord?.Wipe ?? DebriefText.Unknown;

    /// <summary>What the list and its totals cover, after the count in the status line.</summary>
    private string ContextSuffix => _context.Active is not { } active
        ? string.Empty
        : ShowAllContexts
            ? " · " + DebriefText.AllModesAndWipes
            : $" · {GameModeLabel.Of(active.Context.Mode)} · {active.Context.WipeSeason.Value}";

    /// <summary>
    /// [#902 P10] After a wipe or a switch to PvE the page said only that this mode and wipe had no
    /// raids, with the way back a small header link. It names the mode and wipe and how many raids
    /// the context hides; the Show all button sits under it.
    /// </summary>
    private string NoRaidsInContextMessage => _context.Active is { } active
        ? DebriefText.NoRaidsInNamedContext(
            $"{GameModeLabel.Of(active.Context.Mode)} · {active.Context.WipeSeason.Value}",
            OtherContextCount)
        : DebriefText.NoRaidsInContext;

    /// <summary>Reads the context once per load and places every raid in it.</summary>
    private IReadOnlyList<DebriefRaidRecord> StampContext(IReadOnlyList<DebriefRaidRecord> records)
    {
        _context = _raidContext?.Current() ?? RaidContextView.None;
        if (_context.Active is not { } active)
        {
            return records;
        }

        var stamped = records
            .Select(record => record with
            {
                Match = RaidContextRules.Match(record.Raid, active),
                Wipe = _context.WipeOf(record.Raid),
            })
            .ToArray();
        return stamped;
    }

    private void RaiseContext()
    {
        OnPropertyChanged(nameof(OtherContextCount));
        OnPropertyChanged(nameof(HasOtherContexts));
        OnPropertyChanged(nameof(ContextToggleLabel));
    }
}
