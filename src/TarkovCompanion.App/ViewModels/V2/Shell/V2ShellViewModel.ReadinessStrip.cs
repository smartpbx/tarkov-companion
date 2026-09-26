using System.ComponentModel;
using TarkovCompanion.App.Services.Network;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.FormatGuards;
using TarkovCompanion.Application.Services.Readiness;
using TarkovCompanion.Core.Network;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// [#712 1-13] The readiness strip at the top of the Raid page, and the fixes behind its buttons.
/// </summary>
/// <remarks>
/// A separate partial, the shape <c>V2ShellViewModel.NowPanel.cs</c> uses. It follows the shell's
/// own refresh: every runtime change and every second end in a <c>Readiness</c> property change on
/// the UI thread, and the strip re-decides then, so nothing here starts a timer or a subscription
/// of its own on the runtime. Format health and Local only change without a runtime change, so
/// those two are listened to directly.
/// </remarks>
public sealed partial class V2ShellViewModel
{
    private ReadinessStripViewModel? _readinessStrip;
    private FormatHealthMonitor? _stripFormat;

    public ReadinessStripViewModel ReadinessStrip => _readinessStrip ??= new(RunReadinessFix, SaveChosenFolderAsync);

    /// <summary>On the Raid page, and only while something needs the player.</summary>
    public bool ShowsReadinessStrip => ShowsRaidCockpit && ReadinessStrip.IsVisible;

    private void WireReadinessStrip(FormatHealthMonitor? format)
    {
        _stripFormat = format;
        PropertyChanged += ReadinessStripShellChanged;
        ReadinessStrip.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReadinessStripViewModel.IsVisible))
            {
                OnPropertyChanged(nameof(ShowsReadinessStrip));
            }
        };
        if (format is not null)
        {
            format.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshReadinessStrip);
        }

        AppNetworkPolicy.Current.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshReadinessStrip);
        RefreshReadinessStrip();
    }

    private void ReadinessStripShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Readiness))
        {
            RefreshReadinessStrip();
        }
        else if (e.PropertyName == nameof(ShowsRaidCockpit))
        {
            OnPropertyChanged(nameof(ShowsReadinessStrip));
        }
    }

    private void RefreshReadinessStrip()
    {
        if (_disposed)
        {
            return;
        }

        var localOnly = AppNetworkPolicy.Current.Check(NetworkService.GameData) == NetworkVerdict.LocalOnly;
        ReadinessStrip.Apply(ReadinessStripRules.Evaluate(
            ReadinessStripInput.From(_runtime.Current, localOnly, _stripFormat?.Current)));
    }

    private void RunReadinessFix(ReadinessFix fix)
    {
        switch (fix)
        {
            case ReadinessFix.RetryData when Legacy is not null:
                Legacy.Settings.SyncCommand.Execute(null);
                Announce(V2ShellText.Get("V2.Shell.Announce.SyncStarted"), V2Announcement.Polite);
                break;
            case ReadinessFix.OpenDataNetwork:
                GoTo(V2Routes.Setup);
                SetupWorkspace?.Select(V2SetupSection.DataNetwork);
                break;
            case ReadinessFix.OpenSquad:
                GoTo(V2Routes.Group);
                break;
            case ReadinessFix.OpenDetails:
                GoTo(V2Routes.Setup);
                SetupWorkspace?.Select(V2SetupSection.Overview);
                break;
            case ReadinessFix.OpenFormatHealth:
                GoTo(V2Routes.Setup);
                SetupWorkspace?.Select(V2SetupSection.UpdatesDiagnostics);
                break;
            default:
                Announce(V2ShellText.Get("V2.Shell.Announce.ActionUnavailable"), V2Announcement.Assertive);
                break;
        }
    }

    /// <summary>
    /// Saves a picked folder the way Setup's own game-folder fields do, which also makes discovery
    /// look again at once rather than at the next launch.
    /// </summary>
    private async Task SaveChosenFolderAsync(ReadinessItemKind kind, string folder)
    {
        if (Legacy?.Settings is not { } settings)
        {
            Announce(V2ShellText.Get("V2.Shell.Announce.ActionUnavailable"), V2Announcement.Assertive);
            return;
        }

        if (kind == ReadinessItemKind.GameLogs)
        {
            settings.LogFolder = folder;
        }
        else
        {
            settings.ScreenshotFolder = folder;
        }

        await settings.SaveGameFoldersCommand.ExecuteAsync().ConfigureAwait(true);
    }
}
