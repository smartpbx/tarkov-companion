using System.Collections.ObjectModel;
using System.Globalization;
using TarkovCompanion.App.Services.V2.Setup;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>Setup › Data: source, coverage, attempt, success and next refresh, redrawn as the runtime changes (#292).</summary>
public sealed class SetupDataDetailViewModel : BindableViewModel, IDisposable
{
    private readonly IRuntimeStateStore _store;
    private readonly IProfileRuntimeContextService? _profile;
    private readonly RuntimeOptions _options;
    private readonly TimeProvider _clock;
    private readonly Action<Action> _post;
    private readonly EventHandler _onChanged;
    private string? _reason;
    private bool _needsRetry;

    public SetupDataDetailViewModel(
        IRuntimeStateStore store,
        RuntimeOptions options,
        TimeProvider clock,
        IProfileRuntimeContextService? profile = null,
        Action<Action>? post = null,
        Func<Task>? retry = null)
    {
        RetryCommand = new AsyncDelegateCommand(retry ?? (() => Task.CompletedTask));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _profile = profile;
        _post = post ?? (action => action());
        _onChanged = (_, _) => _post(Refresh);
        _store.Changed += _onChanged;
        Refresh();
    }

    public ObservableCollection<SetupFact> Facts { get; } = [];

    /// <summary>Runs the same forced refresh as Sync now; shown only when the last one left nothing fresh.</summary>
    public System.Windows.Input.ICommand RetryCommand { get; }

    public string RetryLabel => V2ShellText.Get("V2.Setup.Data.RetryLabel");

    public string ReasonLabel => V2ShellText.Get("V2.Setup.Data.ReasonLabel");

    public string? Reason
    {
        get => _reason;
        private set
        {
            if (SetProperty(ref _reason, value))
            {
                OnPropertyChanged(nameof(HasReason));
            }
        }
    }

    public bool HasReason => !string.IsNullOrEmpty(Reason);

    /// <summary>True when the last refresh left no data, or no fresh copy: the sync button then says "Retry now".</summary>
    public bool NeedsRetry
    {
        get => _needsRetry;
        private set
        {
            if (SetProperty(ref _needsRetry, value))
            {
                OnPropertyChanged(nameof(SyncButtonLabel));
            }
        }
    }

    public string SyncButtonLabel => NeedsRetry ? V2ShellText.Get("V2.Setup.Data.RetryLabel") : V2ShellText.Get("V2.Setup.Data.SyncLabel");

    /// <summary>Recomputes every line; also called when the page is opened, so ages are not stale.</summary>
    public void Refresh()
    {
        var detail = SetupDataDetail.Describe(_store.Current, ScopeText(), _options.DataFreshFor, _clock.GetUtcNow(), CultureInfo.CurrentCulture);
        Facts.Clear();
        foreach (var fact in detail.Facts)
        {
            Facts.Add(fact);
        }

        Reason = detail.Reason;
        NeedsRetry = detail.NeedsRetry;
    }

    public void Dispose() => _store.Changed -= _onChanged;

    private static string ModeText(ProfileGameMode mode) => mode switch
    {
        ProfileGameMode.Pve => "PvE",
        ProfileGameMode.Seasonal => "Seasonal",
        _ => "PvP",
    };

    private string ScopeText()
    {
        var current = _profile?.Current;
        if (current?.CatalogScope is { } scope)
        {
            return $"{ModeText(current.ActiveProfile!.Context.Mode)} · {scope.Language}";
        }

        var mode = _options.GameMode switch
        {
            GameMode.Pve => "PvE",
            GameMode.PvpSeason => "Seasonal",
            _ => "PvP",
        };
        return $"{mode} · {_options.Language}";
    }
}
