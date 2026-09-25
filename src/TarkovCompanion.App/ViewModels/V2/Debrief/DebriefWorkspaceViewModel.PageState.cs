using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.App.ViewModels.V2.Debrief;

/// <summary>
/// [#902 P8] Debrief remembers its list or stats view, the archive and "Show all" switches and the
/// last filters, across a visit elsewhere and a restart. The notes search is never kept.
/// </summary>
/// <remarks>
/// Saved views (<see cref="DebriefSavedViewStore"/>) are the player's named sets; this is only the
/// state the page was last left in, under its own one key.
/// </remarks>
public sealed partial class DebriefWorkspaceViewModel
{
    private PageState _pageState = new(null, WorkspaceLayoutKeys.PageDebrief);
    private bool _restoringPageState;
    private ICommand? _showEverything;

    private void RestorePageState(IWorkspaceLayoutStore? layout)
    {
        _pageState = new(layout, WorkspaceLayoutKeys.PageDebrief);
        _restoringPageState = true;
        try
        {
            _isStatsView = _pageState.Get("view") == "stats";
            _showArchived = _pageState.Bool("archived", false);
            _showAllContexts = _pageState.Bool("all-contexts", false);
            _mapFilter = _pageState.Get("map");
            _tagFilter = _pageState.Get("tag");
            _outcomeFilter = _pageState.Enum("outcome", DebriefOutcomeFilter.Any);
            _sideFilter = _pageState.Enum("side", DebriefSideFilter.Any);
            _dateFrom = Date(_pageState.Get("from"));
            _dateTo = Date(_pageState.Get("to"));
        }
        finally
        {
            _restoringPageState = false;
        }
    }

    /// <summary>Written on every filter pass; the store skips a value that has not changed.</summary>
    private void SavePageState()
    {
        if (_restoringPageState)
        {
            return;
        }

        _pageState.Set("view", _isStatsView ? "stats" : null);
        _pageState.SetBool("archived", _showArchived, false);
        _pageState.SetBool("all-contexts", _showAllContexts, false);
        _pageState.Set("map", _mapFilter);
        _pageState.Set("tag", _tagFilter);
        _pageState.SetEnum("outcome", _outcomeFilter, DebriefOutcomeFilter.Any);
        _pageState.SetEnum("side", _sideFilter, DebriefSideFilter.Any);
        _pageState.Set("from", _dateFrom?.ToString("O", CultureInfo.InvariantCulture));
        _pageState.Set("to", _dateTo?.ToString("O", CultureInfo.InvariantCulture));
    }

    private static DateTimeOffset? Date(string? stored) =>
        DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;

    /// <summary>
    /// [#902 P8] The list is empty only because of what the page remembers: a filter, the archive
    /// view, or the active mode and wipe. One click undoes whichever it is.
    /// </summary>
    public bool ShowsEmptyReset => ShowsNoRaids && _allRecords.Count > 0;

    public string EmptyResetLabel => HasActiveFilters || _showArchived
        ? DebriefText.ClearFilters
        : DebriefText.ShowAll(OtherContextCount);

    public ICommand ShowEverythingCommand => _showEverything ??= new DelegateCommand(() =>
    {
        if (HasActiveFilters || _showArchived)
        {
            ShowArchived = false;
            ClearFilters();
            return;
        }

        ShowAllContexts = true;
    });
}
