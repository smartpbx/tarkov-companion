using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.ReleaseExperience;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.V2.ReleaseExperience;

/// <summary>The one after-update banner and the short list it opens.</summary>
/// <remarks>
/// The first run records a baseline rather than claiming an update happened. Later builds keep
/// offering their list until it is dismissed, but both the banner and an open list disappear as
/// soon as a raid starts. The runtime event can arrive off the UI thread, so presentation changes
/// cross the dispatcher supplied by composition.
/// </remarks>
public sealed class ReleaseExperienceViewModel : BindableViewModel, IDisposable
{
    private readonly PlayerChangelog _changelog;
    private readonly IReleaseExperienceStateStore _store;
    private readonly IRuntimeStateStore _runtime;
    private readonly Action<Action> _dispatch;
    private readonly string _currentVersion;
    private PlayerChangelogRelease? _release;
    private bool _eligible;
    private bool _isBannerVisible;
    private bool _isListOpen;
    private bool _openedOnRequest;
    private bool _disposed;

    public ReleaseExperienceViewModel(
        PlayerChangelog changelog,
        IReleaseExperienceStateStore store,
        IRuntimeStateStore runtime,
        AppBuildIdentity? identity = null,
        Action<Action>? dispatch = null)
    {
        _changelog = changelog ?? throw new ArgumentNullException(nameof(changelog));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _currentVersion = (identity ?? AppBuildIdentity.Current).Version;
        _dispatch = dispatch ?? (static action => action());
        OpenCommand = new DelegateCommand(Open);
        CloseCommand = new DelegateCommand(Close);
        DismissCommand = new AsyncDelegateCommand(DismissAsync);
        _runtime.Changed += RuntimeChanged;
    }

    public string Version => _release?.Version ?? _currentVersion;

    public string BannerText => ShellText.UpdatedTo(Version);

    public string OpenLabel => ShellText.WhatsNew;

    public string ListTitle => ShellText.WhatsNewIn(Version);

    public IReadOnlyList<string> Changes => _release?.Changes ?? [];

    public bool IsBannerVisible
    {
        get => _isBannerVisible;
        private set => SetProperty(ref _isBannerVisible, value);
    }

    public bool IsListOpen
    {
        get => _isListOpen;
        private set => SetProperty(ref _isListOpen, value);
    }

    public ICommand OpenCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand DismissCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var prior = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
        var release = _changelog.Find(_currentVersion);
        var eligible = prior is not null &&
            release is not null &&
            !string.Equals(prior.DismissedVersion, _currentVersion, StringComparison.OrdinalIgnoreCase);

        await _store.SaveAsync(
            new ReleaseExperienceState(_currentVersion, prior?.DismissedVersion),
            cancellationToken).ConfigureAwait(false);

        _dispatch(() =>
        {
            if (_disposed)
            {
                return;
            }

            _release = release;
            _eligible = eligible;
            OnPropertyChanged(nameof(Version));
            OnPropertyChanged(nameof(BannerText));
            OnPropertyChanged(nameof(ListTitle));
            OnPropertyChanged(nameof(Changes));
            RefreshVisibility();
        });
    }

    /// <summary>Drives the real presentation in the headless renderer without changing its throwaway config.</summary>
    internal void PresentForPreview(bool openList)
    {
        _release = _changelog.Find(_currentVersion) ?? _changelog.Releases[0];
        _eligible = true;
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(Changes));
        RefreshVisibility();
        IsListOpen = openList && IsBannerVisible;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.Changed -= RuntimeChanged;
    }

    private bool RaidIsActive => _runtime.Current.Raid.State is RaidLifecycleState.LoadingRaid or RaidLifecycleState.InRaid;

    private void RuntimeChanged(object? sender, EventArgs e) => _dispatch(RefreshVisibility);

    private void RefreshVisibility()
    {
        IsBannerVisible = _eligible && !RaidIsActive;
        // [#902 P9] Only a raid closes the list. It used to close whenever the banner was not
        // showing, which is always once it has been dismissed, so a list opened again from About
        // or the palette shut on the next runtime change. One the player asked for stays open.
        if (RaidIsActive && !_openedOnRequest)
        {
            IsListOpen = false;
        }
    }

    /// <summary>
    /// Opens this build's change list whether or not the banner is up, for About and the palette.
    /// </summary>
    /// <remarks>
    /// [#902 P9] Once dismissed, or once a raid had started, the list for a build could never be
    /// read again. A build with no entry of its own shows the newest entry, titled with that
    /// entry's version, rather than an empty list.
    /// </remarks>
    public void Show()
    {
        _release ??= _changelog.Find(_currentVersion) ?? _changelog.Releases.FirstOrDefault();
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(Changes));
        _openedOnRequest = true;
        IsListOpen = true;
    }

    private void Open()
    {
        if (IsBannerVisible)
        {
            IsListOpen = true;
        }
    }

    private void Close()
    {
        _openedOnRequest = false;
        IsListOpen = false;
    }

    private async Task DismissAsync()
    {
        _eligible = false;
        _openedOnRequest = false;
        IsListOpen = false;
        IsBannerVisible = false;
        await _store.SaveAsync(
            new ReleaseExperienceState(_currentVersion, _currentVersion),
            CancellationToken.None).ConfigureAwait(true);
    }
}
