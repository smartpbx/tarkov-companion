using TarkovCompanion.App.Localization;
using System.Windows.Input;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One line of the import preview.</summary>
public sealed record SetupProfileTransferChangeViewModel(string Area, string Now, string After);

/// <summary>
/// Setup › Game &amp; Profile's export and import of one profile's progress as a file (#269).
/// </summary>
/// <remarks>
/// Import is two steps, like the preferences import beside it: Preview reads the file and lists
/// what would change in the chosen profile, and only Import writes. Choosing the other target
/// re-reads the preview, because "into this profile" and "as a new profile" change different
/// things and a preview for one must never be confirmed as the other.
/// </remarks>
public sealed class SetupProfileTransferViewModel : BindableViewModel
{
    private readonly ProfileBundleService _service;
    private readonly string _exportDirectory;
    private readonly TimeProvider _clock;
    private string _filePath = string.Empty;
    private string _status = string.Empty;
    private bool _statusIsError;
    private bool _importAsNew;
    private ProfileBundlePreview? _preview;

    public SetupProfileTransferViewModel(ProfileBundleService service, string exportDirectory, TimeProvider? clock = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _exportDirectory = exportDirectory ?? throw new ArgumentNullException(nameof(exportDirectory));
        _clock = clock ?? TimeProvider.System;
        ExportCommand = new AsyncDelegateCommand(ExportAsync);
        PreviewCommand = new AsyncDelegateCommand(PreviewAsync);
        ImportCommand = new AsyncDelegateCommand(ImportAsync);
        CancelCommand = new DelegateCommand(Cancel);
        IntoActiveCommand = new AsyncDelegateCommand(() => ChooseTargetAsync(asNew: false));
        AsNewCommand = new AsyncDelegateCommand(() => ChooseTargetAsync(asNew: true));
    }

    public string Heading => SetupText.TransferHeading;

    public string FileLabel => SetupText.TransferFileLabel;

    public string FilePlaceholder => SetupText.TransferFilePlaceholder;

    public string ExportLabel => SetupText.TransferExportLabel;

    public string PreviewLabel => SetupText.TransferPreviewLabel;

    public string ImportLabel => SetupText.TransferImportLabel;

    public string CancelLabel => SetupText.TransferCancelLabel;

    public string IntoActiveLabel => SetupText.TransferIntoActive;

    public string AsNewLabel => SetupText.TransferAsNew;

    public string NoChangesLabel => SetupText.TransferNoChanges;

    public AsyncDelegateCommand ExportCommand { get; }

    public AsyncDelegateCommand PreviewCommand { get; }

    public AsyncDelegateCommand ImportCommand { get; }

    public ICommand CancelCommand { get; }

    public AsyncDelegateCommand IntoActiveCommand { get; }

    public AsyncDelegateCommand AsNewCommand { get; }

    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value ?? string.Empty);
    }

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

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public bool ImportAsNew
    {
        get => _importAsNew;
        private set
        {
            if (SetProperty(ref _importAsNew, value))
            {
                OnPropertyChanged(nameof(ImportIntoActive));
            }
        }
    }

    public bool ImportIntoActive => !ImportAsNew;

    public bool HasPreview => _preview is not null;

    /// <summary>"Main (PvP · 0.16) → Alt": whose file it is, and where it would land.</summary>
    public string PreviewHeading => _preview is { } preview
        ? SetupText.TransferPreviewHeading(preview.Bundle.Profile.Name, SetupProfilesViewModel.ModeLabel(preview.Bundle.Profile.Mode), preview.Bundle.Profile.Wipe,
          preview.Target == ProfileBundleTarget.NewProfile ? SetupText.TransferNewProfileTarget(preview.TargetName) : preview.TargetName)
        : string.Empty;

    public string PreviewDetail => _preview is { } preview
        ? SetupText.TransferExportedOn(LocalTime.Date(preview.Bundle.ExportedUtc))
        : string.Empty;

    public IReadOnlyList<SetupProfileTransferChangeViewModel> Changes => _preview?.Changes
        .Select(change => new SetupProfileTransferChangeViewModel(change.Area, change.Now, change.After))
        .ToArray() ?? [];

    public bool HasNoChanges => _preview is { Changes.Count: 0 } && CanImport;

    public string Refusal => _preview?.Refusal ?? string.Empty;

    public bool HasRefusal => Refusal.Length > 0;

    public bool CanImport => _preview is { Refusal: null };

    private async Task ExportAsync()
    {
        try
        {
            var bundle = await _service.ExportAsync(CancellationToken.None).ConfigureAwait(true);
            Directory.CreateDirectory(_exportDirectory);
            var path = Path.Combine(
                _exportDirectory,
                $"{LocalTime.FileStamp(_clock.GetUtcNow())}-profile-{SafeName(bundle.Profile.Name)}.json");
            await File.WriteAllTextAsync(path, ProfileBundleCodec.Write(bundle)).ConfigureAwait(true);
            FilePath = path;
            Say(SetupText.TransferExportedTo(path), isError: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Say(SetupText.TransferExportFailed(exception.Message), isError: true);
        }
    }

    private async Task PreviewAsync()
    {
        SetPreview(null);
        var path = FilePath.Trim().Trim('"');
        if (path.Length == 0)
        {
            Say(SetupText.TransferNeedPath, isError: true);
            return;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                Say(SetupText.TransferNoFile(path), isError: true);
                return;
            }

            if (info.Length > ProfileBundleCodec.MaximumLength)
            {
                Say(SetupText.TransferTooLarge, isError: true);
                return;
            }

            var json = await File.ReadAllTextAsync(path).ConfigureAwait(true);
            await PreviewTextAsync(json).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Say(exception.Message, isError: true);
        }
    }

    /// <summary>Previews file contents already read; the file-reading half is only the path handling above.</summary>
    internal async Task PreviewTextAsync(string json)
    {
        try
        {
            var preview = await _service
                .PreviewAsync(json, ImportAsNew ? ProfileBundleTarget.NewProfile : ProfileBundleTarget.ActiveProfile, CancellationToken.None)
                .ConfigureAwait(true);
            SetPreview(preview, json);
            Say(string.Empty, isError: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetPreview(null);
            Say(exception.Message, isError: true);
        }
    }

    private string? _previewJson;

    private async Task ChooseTargetAsync(bool asNew)
    {
        ImportAsNew = asNew;
        if (_previewJson is { } json)
        {
            await PreviewTextAsync(json).ConfigureAwait(true);
        }
    }

    private async Task ImportAsync()
    {
        if (_preview is not { } preview)
        {
            return;
        }

        try
        {
            var name = await _service.ImportAsync(preview, CancellationToken.None).ConfigureAwait(true);
            SetPreview(null);
            Say(SetupText.TransferImported(name), isError: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Say(SetupText.TransferImportFailed(exception.Message), isError: true);
        }
    }

    private void Cancel()
    {
        SetPreview(null);
        Say(string.Empty, isError: false);
    }

    private void SetPreview(ProfileBundlePreview? preview, string? json = null)
    {
        _preview = preview;
        _previewJson = preview is null ? null : json;
        foreach (var name in new[]
        {
            nameof(HasPreview), nameof(PreviewHeading), nameof(PreviewDetail), nameof(Changes), nameof(HasNoChanges),
            nameof(Refusal), nameof(HasRefusal), nameof(CanImport),
        })
        {
            OnPropertyChanged(name);
        }
    }

    private void Say(string text, bool isError)
    {
        StatusIsError = isError;
        Status = text;
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)]);
        return cleaned.Length == 0 ? "profile" : cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }
}
