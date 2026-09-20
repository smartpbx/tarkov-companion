using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// Setup > Data (#292 task 3): the database's migration state and its newest verified backup.
/// </summary>
/// <remarks>
/// Every number here comes from <see cref="SqliteMigrationRunner"/>, which already makes and
/// verifies these backups before a destructive migration; this reads that state and adds one way
/// to ask for a backup outside a migration (<see cref="BackUpNowCommand"/>, which runs through the
/// exact same <c>VACUUM INTO</c> plus integrity-check path, not a second implementation of it).
/// </remarks>
public sealed class SetupDatabaseStatusViewModel : BindableViewModel
{
    private readonly SqliteMigrationRunner _migrations;
    private string? _backupFolderPath;
    private string _versionLine = string.Empty;
    private string _backupLine = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBackingUp;

    public SetupDatabaseStatusViewModel(SqliteMigrationRunner migrations)
    {
        _migrations = migrations ?? throw new ArgumentNullException(nameof(migrations));
        BackUpNowCommand = new AsyncDelegateCommand(BackUpNowAsync);
    }

    public string VersionLine
    {
        get => _versionLine;
        private set => SetProperty(ref _versionLine, value);
    }

    public string BackupLine
    {
        get => _backupLine;
        private set => SetProperty(ref _backupLine, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBackingUp
    {
        get => _isBackingUp;
        private set => SetProperty(ref _isBackingUp, value);
    }

    /// <summary>The folder <c>Open backup folder</c> opens; null until a backup exists.</summary>
    public string? BackupFolderPath
    {
        get => _backupFolderPath;
        private set
        {
            if (SetProperty(ref _backupFolderPath, value))
            {
                OnPropertyChanged(nameof(CanOpenBackupFolder));
            }
        }
    }

    public bool CanOpenBackupFolder => BackupFolderPath is not null;

    public ICommand BackUpNowCommand { get; }

    public string HeadingLabel => V2ShellText.Get("V2.Setup.Data.DatabaseHeading");

    public string BackUpNowLabel => V2ShellText.Get("V2.Setup.Data.BackUpNowLabel");

    public string OpenBackupFolderLabel => V2ShellText.Get("V2.Setup.Data.OpenBackupFolderLabel");

    /// <summary>Reads the current state fresh. Called on construction and whenever Data is opened.</summary>
    public async Task RefreshAsync()
    {
        var status = await _migrations.GetStatusAsync(CancellationToken.None).ConfigureAwait(true);
        VersionLine = status is { CurrentVersion: { } version }
            ? V2ShellText.Format(
                "V2.Setup.Data.VersionLine",
                System.Globalization.CultureInfo.CurrentCulture,
                version,
                status.LastAppliedUtc is { } applied ? LocalTime.Moment(applied) : V2ShellText.Get("V2.Setup.Data.UnknownTime"))
            : V2ShellText.Get("V2.Setup.Data.NoMigrations");

        if (status.LastVerifiedBackupPath is { } path && status.LastVerifiedBackupBytes is { } bytes && status.LastVerifiedBackupUtc is { } backedUpAt)
        {
            BackupLine = V2ShellText.Format(
                "V2.Setup.Data.BackupLine",
                System.Globalization.CultureInfo.CurrentCulture,
                SetupCleanupViewModel.FormatBytes(bytes),
                LocalTime.Moment(backedUpAt));
            BackupFolderPath = Path.GetDirectoryName(path);
        }
        else
        {
            BackupLine = V2ShellText.Get("V2.Setup.Data.NoBackup");
            BackupFolderPath = null;
        }
    }

    private async Task BackUpNowAsync()
    {
        if (IsBackingUp)
        {
            return;
        }

        IsBackingUp = true;
        StatusMessage = V2ShellText.Get("V2.Setup.Data.BackingUp");
        try
        {
            await _migrations.BackUpNowAsync(CancellationToken.None).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            StatusMessage = V2ShellText.Get("V2.Setup.Data.BackedUp");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusMessage = V2ShellText.Format(
                "V2.Setup.Data.BackupFailed",
                System.Globalization.CultureInfo.CurrentCulture,
                exception.Message);
        }
        finally
        {
            IsBackingUp = false;
        }
    }
}
