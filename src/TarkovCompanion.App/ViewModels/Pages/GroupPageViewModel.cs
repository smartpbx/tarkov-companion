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
/// This is the only page in the application that causes anything to leave the machine, so it
/// is also the page that has to say so. Everything that can be sent is listed on it in plain
/// words, and the two optional parts each have their own switch, because agreeing to share
/// where you are is not agreeing to share what you are carrying.
///
/// There is no hosted service and no default server. A group runs their own, which is the only
/// arrangement in which "who can see this" has an answer the group controls.
/// </remarks>
public sealed class GroupPageViewModel : PageViewModel
{
    private readonly IGroupSettingsStore _settings;
    private IReadOnlyList<GroupMemberRowViewModel> _members = [];
    private string _status = "Not sharing. Nothing about this session leaves the machine.";
    private string _saveStatus = "Fill these in and turn sharing on when the group is ready.";
    private bool _isEnabled;
    private string _serverUri = string.Empty;
    private string _room = string.Empty;
    private string _displayName = string.Empty;
    private string _secret = string.Empty;
    private bool _sharesLoadout;
    private bool _sharesQuests;
    private DateTimeOffset _rendered = DateTimeOffset.MinValue;

    public GroupPageViewModel(IGroupSettingsStore settings)
        : base(
            "Group",
            "Share this session with your group, and see theirs",
            "Nothing is shared until you turn it on")
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

    public string Room
    {
        get => _room;
        set => SetProperty(ref _room, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string Secret
    {
        get => _secret;
        set => SetProperty(ref _secret, value);
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
        Room = stored.Room ?? string.Empty;
        DisplayName = stored.DisplayName ?? string.Empty;
        Secret = stored.Secret ?? string.Empty;
        SharesLoadout = stored.SharesLoadout;
        SharesQuests = stored.SharesQuests;
    }

    private async Task SaveAsync()
    {
        var settings = new GroupSharingSettings(
            IsEnabled,
            string.IsNullOrWhiteSpace(ServerUri) ? null : ServerUri.Trim(),
            string.IsNullOrWhiteSpace(Room) ? null : Room.Trim(),
            string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim(),
            string.IsNullOrWhiteSpace(Secret) ? null : Secret,
            SharesLoadout,
            SharesQuests);

        await _settings.SaveAsync(settings, CancellationToken.None).ConfigureAwait(true);
        SaveStatus = !settings.IsEnabled
            ? "Saved. Sharing is off, so nothing leaves this machine."
            : settings.MissingPiece is { } missing
                ? $"Saved, but sharing needs {missing} before it can start."
                : "Saved. Sharing starts within a few seconds.";
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
        Evidence = group.IsSharing
            ? "Sharing with your group"
            : "Nothing is shared until you turn it on";
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
