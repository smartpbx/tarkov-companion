using System.Collections.ObjectModel;
using System.Globalization;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>A profile the compare can be pointed at.</summary>
public sealed record SetupProfileCompareChoice(Guid Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One line of the compare: what is counted, and each side's value.</summary>
public sealed record SetupProfileCompareRow(string Label, string Left, string Right)
{
    public bool Differs => !string.Equals(Left, Right, StringComparison.Ordinal);
}

/// <summary>
/// Setup › Game &amp; Profile's compare (#269): two profiles side by side, read-only. Nothing here
/// switches, writes or merges; it reads each profile's own progress file, quest records and raids.
/// </summary>
public sealed class SetupProfileCompareViewModel : BindableViewModel
{
    private readonly Func<ProfileRecord, ProfileRecord, CancellationToken, Task<(ProfileCompareSide Left, ProfileCompareSide Right)>> _compare;
    private readonly Dictionary<Guid, ProfileRecord> _records = [];
    private SetupProfileCompareChoice? _left;
    private SetupProfileCompareChoice? _right;
    private IReadOnlyList<SetupProfileCompareRow> _rows = [];
    private string _status = string.Empty;
    private int _generation;
    private bool _applying;
    private bool _chosen;

    public SetupProfileCompareViewModel(
        Func<ProfileRecord, ProfileRecord, CancellationToken, Task<(ProfileCompareSide Left, ProfileCompareSide Right)>> compare)
    {
        _compare = compare ?? throw new ArgumentNullException(nameof(compare));
    }

    public string Heading => "Compare profiles";

    public ObservableCollection<SetupProfileCompareChoice> Choices { get; } = [];

    /// <summary>Two profiles are needed before there is anything to compare.</summary>
    public bool CanCompare => Choices.Count >= 2;

    public SetupProfileCompareChoice? Left
    {
        get => _left;
        set
        {
            if (SetProperty(ref _left, value) && !_applying)
            {
                _chosen = true;
                _ = RefreshAsync();
            }
        }
    }

    public SetupProfileCompareChoice? Right
    {
        get => _right;
        set
        {
            if (SetProperty(ref _right, value) && !_applying)
            {
                _chosen = true;
                _ = RefreshAsync();
            }
        }
    }

    public IReadOnlyList<SetupProfileCompareRow> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
            {
                OnPropertyChanged(nameof(HasRows));
            }
        }
    }

    public bool HasRows => Rows.Count > 0;

    /// <summary>One line: a mode difference, a failure, or nothing.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => Status.Length > 0;

    /// <summary>
    /// Redraws the choices from the workspace. The active profile starts on the left and the first
    /// other one that is not archived on the right, following a switch; once the player picks, their
    /// picks stay while those profiles exist.
    /// </summary>
    public Task SetProfilesAsync(ProfileWorkspaceSnapshot workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _records.Clear();
        foreach (var record in workspace.Profiles)
        {
            _records[record.Context.Identity.ProfileId] = record;
        }

        var ordered = workspace.Profiles
            .OrderBy(record => record.Lifecycle == ProfileLifecycle.Archived ? 1 : 0)
            .ThenBy(record => record.Context.Identity.ProfileId == workspace.ActiveProfileId ? 0 : 1)
            .ThenBy(record => record.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(record => new SetupProfileCompareChoice(
                record.Context.Identity.ProfileId,
                record.Lifecycle == ProfileLifecycle.Archived ? $"{record.Name} (archived)" : record.Name))
            .ToArray();

        var keepLeft = _chosen ? _left?.Id : null;
        var keepRight = _chosen ? _right?.Id : null;
        _applying = true;
        try
        {
            Choices.Clear();
            foreach (var choice in ordered)
            {
                Choices.Add(choice);
            }

            var left = ordered.FirstOrDefault(choice => choice.Id == keepLeft)
                ?? ordered.FirstOrDefault(choice => choice.Id == workspace.ActiveProfileId)
                ?? ordered.FirstOrDefault();
            var right = ordered.FirstOrDefault(choice => choice.Id == keepRight && choice.Id != left?.Id)
                ?? ordered.FirstOrDefault(choice => choice.Id != left?.Id);
            Left = left;
            Right = right;
        }
        finally
        {
            _applying = false;
        }

        OnPropertyChanged(nameof(CanCompare));
        return RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var generation = ++_generation;
        if (_left is not { } leftChoice || _right is not { } rightChoice ||
            !_records.TryGetValue(leftChoice.Id, out var left) || !_records.TryGetValue(rightChoice.Id, out var right))
        {
            Rows = [];
            Status = string.Empty;
            return;
        }

        try
        {
            var (a, b) = await _compare(left, right, CancellationToken.None).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            Rows = BuildRows(a, b);
            Status = a.Mode != b.Mode ? "Different game modes: their progress is kept apart." : string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (generation == _generation)
            {
                Rows = [];
                Status = $"Could not compare · {exception.Message}";
            }
        }
    }

    internal static IReadOnlyList<SetupProfileCompareRow> BuildRows(ProfileCompareSide a, ProfileCompareSide b)
    {
        static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
        static string Rate(ProfileCompareSide side) => side.Survived + side.Died == 0
            ? "—"
            : (side.Survived / (double)(side.Survived + side.Died)).ToString("P0", CultureInfo.CurrentCulture);
        return
        [
            new("Game mode", SetupProfilesViewModel.ModeLabel(a.Mode), SetupProfilesViewModel.ModeLabel(b.Mode)),
            new("Wipe", a.Wipe, b.Wipe),
            new("Level", N(a.Level), N(b.Level)),
            new("Quests completed", N(a.QuestsCompleted), N(b.QuestsCompleted)),
            new("Hideout levels", N(a.HideoutLevels), N(b.HideoutLevels)),
            new("Keys owned", N(a.KeysOwned), N(b.KeysOwned)),
            new("Ammo owned (rounds)", N(a.AmmoRounds), N(b.AmmoRounds)),
            new("Raids", N(a.Raids), N(b.Raids)),
            new("Survived", N(a.Survived), N(b.Survived)),
            new("Died", N(a.Died), N(b.Died)),
            new("Survival rate", Rate(a), Rate(b)),
        ];
    }
}
