using System.Globalization;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.ViewModels;

/// <summary>One other person in the group, rendered as finished text.</summary>
/// <param name="Name">The name they chose.</param>
/// <param name="Where">Which map, and what they are doing there.</param>
/// <param name="Position">Where they were, and how long ago.</param>
/// <param name="Detail">Their loadout or quests, where they chose to share them.</param>
/// <param name="IsInRaid">Drives the colour: live members read differently from idle ones.</param>
public sealed record GroupMemberRowViewModel(
    string Name,
    string Where,
    string Position,
    string Detail,
    bool IsInRaid)
{
    public string StateColor => IsInRaid ? "#77B895" : "#8F9BA6";
}

/// <summary>
/// Sharing this session with a group, and seeing theirs.
/// </summary>
/// <remarks>
/// One key, one server, and two switches for the parts that are not sent by default. There is
/// no hosted service and no default server: a group runs their own.
///
/// This page used to explain itself at length, on the reading that sharing a position needed
/// arguing for. It does not. The people in the group are on the same voice call.
/// </remarks>
public sealed class GroupPageViewModel : PageViewModel
{
    private readonly IGroupSettingsStore _settings;
    private IReadOnlyList<GroupMemberRowViewModel> _members = [];
    private string _status = "Not sharing";
    private string _saveStatus = "Fill these in, then turn sharing on";
    private bool _isEnabled;
    private string _serverUri = string.Empty;
    private string _displayName = string.Empty;
    private string _key = string.Empty;
    private bool _sharesLoadout;
    private bool _sharesQuests;
    private DateTimeOffset _rendered = DateTimeOffset.MinValue;

    public GroupPageViewModel(IGroupSettingsStore settings)
        : base(
            "Group",
            "Share this session, and see theirs",
            "Off")
    {
        _settings = settings;
        SaveCommand = new AsyncDelegateCommand(SaveAsync);
    }

    public AsyncDelegateCommand SaveCommand { get; }

    public IReadOnlyList<GroupMemberRowViewModel> Members
    {
        get => _members;
        private set
        {
            SetProperty(ref _members, value);
            OnPropertyChanged(nameof(HasMembers));
            OnPropertyChanged(nameof(HasNoMembers));
        }
    }

    public bool HasMembers => Members.Count > 0;

    public bool HasNoMembers => Members.Count == 0;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string SaveStatus
    {
        get => _saveStatus;
        private set => SetProperty(ref _saveStatus, value);
    }

    /// <summary>Whether anything is published at all.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string ServerUri
    {
        get => _serverUri;
        set => SetProperty(ref _serverUri, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    /// <summary>
    /// The one thing a group agrees between themselves.
    /// </summary>
    /// <remarks>
    /// There used to be a room name and a secret. Two values meant two ways to be wrong and
    /// the failure looked identical either way, which is exactly what happened: a member typed
    /// their own secret, every publish was refused, and the list was simply empty.
    ///
    /// This is now both at once. Whoever types the same key is in the same group, and a key
    /// nobody else uses is a room nobody else is in rather than a refusal.
    /// </remarks>
    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
    }

    public bool SharesLoadout
    {
        get => _sharesLoadout;
        set => SetProperty(ref _sharesLoadout, value);
    }

    public bool SharesQuests
    {
        get => _sharesQuests;
        set => SetProperty(ref _sharesQuests, value);
    }

    /// <summary>Reads the stored settings into the form, once, at startup.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var stored = await _settings.GetAsync(cancellationToken).ConfigureAwait(true);
        IsEnabled = stored.IsEnabled;
        ServerUri = stored.ServerUri ?? string.Empty;
        DisplayName = stored.DisplayName ?? string.Empty;
        Key = stored.Key ?? string.Empty;
        SharesLoadout = stored.SharesLoadout;
        SharesQuests = stored.SharesQuests;
    }

    private async Task SaveAsync()
    {
        var settings = new GroupSharingSettings(
            IsEnabled,
            string.IsNullOrWhiteSpace(ServerUri) ? null : ServerUri.Trim(),
            string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim(),
            string.IsNullOrWhiteSpace(Key) ? null : Key.Trim(),
            SharesLoadout,
            SharesQuests);

        await _settings.SaveAsync(settings, CancellationToken.None).ConfigureAwait(true);
        SaveStatus = !settings.IsEnabled
            ? "Saved · sharing is off"
            : settings.MissingPiece is { } missing
                ? $"Saved · still needs {missing}"
                : "Saved · sharing starts in a few seconds";
    }

    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var group = snapshot.Group;
        if (group.UpdatedUtc == _rendered)
        {
            return;
        }

        _rendered = group.UpdatedUtc;
        Status = group.Detail;
        Members = group.Members.Select(Describe).ToArray();
        Evidence = group.IsSharing ? "Sharing" : "Off";
    }

    private static GroupMemberRowViewModel Describe(GroupMemberView member) => new(
        member.Name,
        member.MapId is { Length: > 0 } map
            ? member.Side is { Length: > 0 } side
                ? $"{map} · {member.RaidState} · {side}"
                : $"{map} · {member.RaidState}"
            : member.RaidState.ToString(),
        member.Position is { } position
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"{position.X:F0}, {position.Z:F0} · from a screenshot {Age(member.PositionAge)}")
            : "No screenshot position shared.",
        member.Loadout.Count > 0 || member.Quests.Count > 0
            ? string.Join(" · ", member.Loadout.Concat(member.Quests))
            : "Nothing else shared.",
        member.RaidState == Core.Domain.Raids.RaidLifecycleState.InRaid);

    private static string Age(TimeSpan? age) => age is not { } value
        ? "at an unknown time"
        : value < TimeSpan.FromMinutes(1)
            ? string.Create(CultureInfo.CurrentCulture, $"{Math.Max(0, (int)value.TotalSeconds)}s ago")
            : string.Create(CultureInfo.CurrentCulture, $"{(int)value.TotalMinutes}m ago");
}
