using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.App.ViewModels.V2.Debrief;

/// <summary>
/// [#902 P8] Debrief remembers its list or stats view, the "Show all" switch and the last filters,
/// across a visit elsewhere and a restart. The notes search is never kept.
/// </summary>
/// <remarks>
/// <para>
/// Saved views (<see cref="DebriefSavedViewStore"/>) are the player's named sets; this is only the
/// state the page was last left in, under its own one key.
/// </para>
/// <para>
/// [#935] The From/To dates and the archive view are not kept. A remembered To date, or the archive
/// view, opened Debrief days later without the newest raids, although nothing about it said a raid
/// had just been recorded. A layout replaced by Reset everything or an import is read again at
/// once: the page used to keep its old filters in memory and write them straight back.
/// </para>
/// </remarks>
public sealed partial class DebriefWorkspaceViewModel
{
    private PageState _pageState = new(null, WorkspaceLayoutKeys.PageDebrief);
    private bool _restoringPageState;
    private ICommand? _showEverything;

    /// <summary>Reads what is stored, and again whenever Backup &amp; reset replaces it; called once, from the constructor.</summary>
    private void RestorePageState(IWorkspaceLayoutStore? layout)
    {
        ReadPageState(layout);
        WorkspaceLayoutReplaced.Reread(layout, () =>
        {
            ReadPageState(layout);
            ApplyFilters();
            OnPropertyChanged(string.Empty);
        });
    }

    private void ReadPageState(IWorkspaceLayoutStore? layout)
    {
        _pageState = new(layout, WorkspaceLayoutKeys.PageDebrief);
        _restoringPageState = true;
        try
        {
            _isStatsView = _pageState.Get("view") == "stats";
            _showAllContexts = _pageState.Bool("all-contexts", false);
            _mapFilter = _pageState.Get("map");
            _tagFilter = _pageState.Get("tag");
            _outcomeFilter = _pageState.Enum("outcome", DebriefOutcomeFilter.Any);
            _sideFilter = _pageState.Enum("side", DebriefSideFilter.Any);
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
        _pageState.SetBool("all-contexts", _showAllContexts, false);
        _pageState.Set("map", _mapFilter);
        _pageState.Set("tag", _tagFilter);
        _pageState.SetEnum("outcome", _outcomeFilter, DebriefOutcomeFilter.Any);
        _pageState.SetEnum("side", _sideFilter, DebriefSideFilter.Any);

        // [#935] Written by older builds; removed so they are not read back by one.
        _pageState.Set("archived", null);
        _pageState.Set("from", null);
        _pageState.Set("to", null);
    }

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
