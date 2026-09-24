using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.Views.V2.Primitives;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using V2RecommendationAction = TarkovCompanion.Core.Abstractions.V2.RecommendationAction;
using V2RecommendationReason = TarkovCompanion.Core.Abstractions.V2.RecommendationReason;

namespace TarkovCompanion.App.ViewModels.V2.LootScan;

/// <summary>Dense, review-first presentation of a single immutable Loot Scan result.</summary>
public sealed class LootScanViewModel : BindableViewModel
{
    private const int DecisionsPerPage = 24;
    private const int VisibleIssueLimit = 8;

    private readonly CultureInfo _culture;
    private readonly LootScanPresentationText _text;
    private int _pageIndex;
    private LootScanVerdict? _filter;
    private LootScanDecisionViewModel? _selected;
    private readonly ILootScanWorkspaceControls? _controls;
    private readonly Func<string, Task>? _openWiki;
    private readonly ObservableCollection<LootScanDecisionViewModel> _decisions;
    private LootScanResult _result;
    private bool _isProgressive;
    private bool _progressStopped;
    private CarriedGridIdentity? _shownCarriedGrid;

    public LootScanViewModel(
        LootScanResult result,
        Action<LootScanDecision>? openEvidence = null,
        CultureInfo? culture = null,
        LootScanPresentationText? text = null,
        ILootScanWorkspaceControls? controls = null,
        GridCellAddress? select = null,
        bool isProgressive = false,
        Func<string, Task>? openWiki = null)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
        _isProgressive = isProgressive;
        _culture = culture ?? CultureInfo.CurrentCulture;
        _text = text ?? LootScanPresentationText.Default;
        _controls = controls;
        _openWiki = openWiki;
        PhaseChoices =
        [
            new(null, _text.PhaseCounted),
            new(RecommendationRaidPhase.Early, _text.PhaseEarly),
            new(RecommendationRaidPhase.Middle, _text.PhaseMiddle),
            new(RecommendationRaidPhase.Late, _text.PhaseLate),
            new(RecommendationRaidPhase.Extracting, _text.PhaseExtracting),
        ];
        RiskChoices =
        [
            new(RecommendationRaidRisk.Low, _text.RiskLow),
            new(RecommendationRaidRisk.Elevated, _text.RiskElevated),
            new(RecommendationRaidRisk.High, _text.RiskHigh),
            new(RecommendationRaidRisk.Critical, _text.RiskCritical),
        ];
        // Decided calls keep the planner's order. What could only be valued follows, dearest
        // square first, and what could not be read at all comes last: during a raid the top of
        // the list has to be the part worth acting on.
        _decisions = new ObservableCollection<LootScanDecisionViewModel>(result.Decisions
            .Select(decision => new LootScanDecisionViewModel(
                decision, result.EvaluatedUtc, openEvidence, _culture, _text, controls, openWiki: openWiki)
            {
                SelectAction = Select,
            })
            .Select((decision, index) => (Decision: decision, Index: index))
            .OrderBy(entry => entry.Decision.IsReview)
            .ThenByDescending(entry => entry.Decision.IsAdvisedTake)
            .ThenByDescending(entry => entry.Decision.IsReview ? entry.Decision.CatalogValuePerSquareRoubles ?? -1 : 0)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Decision)
            .ToArray());
        Issues = result.Issues.Select(issue => issue.Explanation).Distinct(StringComparer.Ordinal).ToArray();
        PreviousPageCommand = new DelegateCommand(PreviousPage);
        NextPageCommand = new DelegateCommand(NextPage);
        ScanAgainCommand = new DelegateCommand(() => ScanAgainRequested?.Invoke(this, EventArgs.Empty));
        ToggleStayCommand = new DelegateCommand(() => StayToggled?.Invoke(this, !IsStaying));
        Filters =
        [
            new LootScanFilterViewModel(null, _text.FilterAll, Decisions.Count, SetFilter) { IsSelected = true },
            new LootScanFilterViewModel(LootScanVerdict.Take, _text.FilterTake, TakeCount, SetFilter),
            new LootScanFilterViewModel(LootScanVerdict.Swap, _text.FilterSwap, SwapCount, SetFilter),
            new LootScanFilterViewModel(LootScanVerdict.Leave, _text.FilterLeave, LeaveCount, SetFilter),
            new LootScanFilterViewModel(LootScanVerdict.Review, _text.FilterReview, ReviewCount, SetFilter),
        ];
        LootGrid = BuildLootGrid();
        CarriedGrid = BuildCarriedGrid();

        // A scan decided again after the player pinned something keeps them on that item. Failing
        // that the concept opens on its most consequential call: a swap first, then a take.
        Select((select is { } anchor ? Decisions.FirstOrDefault(item => item.SourceAnchor == anchor) : null) ??
               Decisions.FirstOrDefault(item => item.IsSwap) ??
               Decisions.FirstOrDefault(item => item.IsTake) ??
               Decisions.FirstOrDefault());
    }

    public LootScanResult Result => _result;

    /// <summary>"Last scans" (#274): this raid's saved scans, beside the live one.</summary>
    /// <summary>#287: the retention chip and Read as… for the frame this result came from.</summary>
    public TarkovCompanion.App.ViewModels.V2.Shell.ScanSourceViewModel? Source
    {
        get => _source;
        set
        {
            if (!ReferenceEquals(_source, value))
            {
                _source = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSource));
            }
        }
    }

    public bool HasSource => _source is not null;

    private TarkovCompanion.App.ViewModels.V2.Shell.ScanSourceViewModel? _source;

    public LootScanHistoryViewModel? History
    {
        get => _history;
        set
        {
            if (!ReferenceEquals(_history, value))
            {
                _history = value;
                OnPropertyChanged();
            }
        }
    }

    private LootScanHistoryViewModel? _history;

    public CaptureCorrelationId CorrelationId => Result.CorrelationId;

    public bool IsProgressive => _isProgressive;

    /// <summary>An empty, live page that accepts matched cells until its final decision arrives.</summary>
    public static LootScanViewModel CreateProgress(
        LootScanRecognitionStarted started,
        ILootScanWorkspaceControls? controls = null,
        CultureInfo? culture = null,
        LootScanPresentationText? text = null,
        Func<string, Task>? openWiki = null)
    {
        ArgumentNullException.ThrowIfNull(started);
        var focus = started.Context.InitiatingDevice ?? "desktop";
        var result = new LootScanResult(
            $"progress-{started.CorrelationId}",
            started.SessionId,
            started.CorrelationId,
            started.Context,
            started.ArtifactId,
            started.DecodeRevision,
            started.ContentSha256,
            started.ContentSha256,
            focus,
            started.StartedUtc.ToUniversalTime(),
            new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current, "loot.recognition.running"),
            [],
            [],
            []);
        return new(result, culture: culture, text: text, controls: controls, isProgressive: true, openWiki: openWiki);
    }

    /// <summary>Whether the raid phase and risk can be set from here.</summary>
    public bool HasControls => _controls is not null;

    public IReadOnlyList<LootScanChoice<RecommendationRaidPhase?>> PhaseChoices { get; }

    public IReadOnlyList<LootScanChoice<RecommendationRaidRisk>> RiskChoices { get; }

    public LootScanChoice<RecommendationRaidPhase?> SelectedPhase
    {
        get => PhaseChoices.First(choice => choice.Value == _controls?.Phase);
        set
        {
            if (value is not null && _controls is not null && value.Value != _controls.Phase)
            {
                _ = _controls.SetPhaseAsync(value.Value);
            }
        }
    }

    public LootScanChoice<RecommendationRaidRisk> SelectedRisk
    {
        get => RiskChoices.First(choice => choice.Value == (_controls?.Risk ?? RecommendationRaidRisk.Low));
        set
        {
            if (value is not null && _controls is not null && value.Value != _controls.Risk)
            {
                _ = _controls.SetRiskAsync(value.Value);
            }
        }
    }

    public string PhaseLabel => _text.PhaseLabel;

    public string RiskLabel => _text.RiskLabel;

    public IReadOnlyList<LootScanDecisionViewModel> Decisions => _decisions;

    public IReadOnlyList<string> Issues { get; private set; }

    /// <summary>
    /// The cards drawn for the current page. Rendering every recognized grid item at once made a
    /// maximum-size scan allocate hundreds of expanders and buttons before the first result could
    /// be read. Paging is deliberately fixed-size so the review cost stays bounded on desktop and
    /// on the paired tablet surface.
    /// </summary>
    public IReadOnlyList<LootScanDecisionViewModel> VisibleDecisions => FilteredDecisions
        .Skip(_pageIndex * DecisionsPerPage)
        .Take(DecisionsPerPage)
        .ToArray();

    public IReadOnlyList<string> VisibleIssues => Issues.Take(VisibleIssueLimit).ToArray();

    /// <summary>Requests the view to announce and reveal the new page after explicit navigation.</summary>
    public event EventHandler? PageNavigated;

    public ICommand PreviousPageCommand { get; }

    public ICommand NextPageCommand { get; }

    public string Heading => _text.Heading;

    public string StatusLabel => IsProgressive
        ? (_progressStopped ? _text.StatusStopped : _text.StatusRecognising)
        : Result.Status.Freshness switch
    {
        FreshnessState.Stale => _text.StatusStale,
        FreshnessState.Unknown => _text.StatusUnknown,
        _ => Result.Status.Completeness switch
        {
            ResultCompleteness.Complete => _text.StatusComplete,
            ResultCompleteness.Partial => _text.StatusPartial,
            _ => _text.StatusUnavailable,
        },
    };

    public string StatusDetail => IsProgressive
        ? (_progressStopped ? _text.StatusDetailStopped : _text.StatusDetailRecognising)
        : Result.Status.Freshness switch
    {
        FreshnessState.Stale => _text.StatusDetailStale,
        FreshnessState.Unknown => _text.StatusDetailUnknown,
        _ => Result.Status.Completeness switch
        {
            ResultCompleteness.Complete => _text.StatusDetailComplete,
            ResultCompleteness.Partial => _text.StatusDetailPartial,
            _ => _text.StatusDetailUnavailable,
        },
    };

    public string ContextLabel
    {
        get
        {
            // The map and nothing else. The profile rides in the context as its stable id, and
            // a guid beside the map name told the player nothing; the shell's own header already
            // names the profile.
            var map = Result.Context.ActiveMap;
            return string.IsNullOrWhiteSpace(map)
                ? _text.CurrentCaptureContext
                : _culture.TextInfo.ToTitleCase(map.Replace('-', ' '));
        }
    }

    public string CaptureLabel => Format(
        _text.CaptureTemplate,
        ("correlation", Result.CorrelationId.ToString()),
        ("revision", Result.DecodeRevision.ToString(_culture)));

    public string TimingLabel
    {
        get
        {
            var elapsed = Result.Timings.Sum(stage => stage.ElapsedMilliseconds);
            return elapsed < 1000
                ? Format(_text.MillisecondsTemplate, ("value", elapsed.ToString(_culture)))
                : Format(_text.SecondsTemplate, ("value", (elapsed / 1000d).ToString("0.0", _culture)));
        }
    }

    public string FocusLabel => Format(_text.FocusTemplate, ("device", Result.FocusDeviceId));

    public int TakeCount => Decisions.Count(item => item.IsTake);

    public int SwapCount => Decisions.Count(item => item.IsSwap);

    public int LeaveCount => Decisions.Count(item => item.IsLeave);

    public int ReviewCount => Decisions.Count(item => item.IsReview);

    public string TakeSummary => Format(_text.TakeCountTemplate, ("count", TakeCount.ToString(_culture)));

    public string SwapSummary => Format(_text.SwapCountTemplate, ("count", SwapCount.ToString(_culture)));

    public string LeaveSummary => Format(_text.LeaveCountTemplate, ("count", LeaveCount.ToString(_culture)));

    public string ReviewSummary => Format(_text.ReviewCountTemplate, ("count", ReviewCount.ToString(_culture)));

    public bool HasDecisions => Decisions.Count > 0;

    public bool HasNoVisibleLoot =>
        !IsProgressive &&
        Decisions.Count == 0 &&
        Result.Status.Completeness != ResultCompleteness.Unavailable &&
        Result.Issues.All(issue => issue.Kind is not (
            LootScanIssueKind.CaptureChanged or
            LootScanIssueKind.LootCoveragePartial));

    public bool HasUnavailableResult => !IsProgressive && !HasDecisions && !HasNoVisibleLoot;

    public bool HasIssues => Issues.Count > 0;

    public bool HasHiddenIssues => Issues.Count > VisibleIssueLimit;

    public string HiddenIssuesLabel => Format(
        _text.HiddenIssuesTemplate,
        ("count", (Issues.Count - VisibleIssueLimit).ToString(_culture)));

    public int PageCount => Math.Max(1, (FilteredDecisions.Count + DecisionsPerPage - 1) / DecisionsPerPage);

    public bool HasMultiplePages => PageCount > 1;

    public bool HasPreviousPage => _pageIndex > 0;

    public bool HasNextPage => _pageIndex + 1 < PageCount;

    public string PageSummary => Format(
        _text.PageTemplate,
        ("page", (_pageIndex + 1).ToString(_culture)),
        ("pages", PageCount.ToString(_culture)),
        ("items", FilteredDecisions.Count.ToString(_culture)));

    public bool IsComplete =>
        !IsProgressive &&
        Result.Status.Completeness == ResultCompleteness.Complete &&
        Result.Status.Freshness == FreshnessState.Current;

    public bool IsPartial =>
        IsProgressive ||
        Result.Status.Completeness == ResultCompleteness.Partial ||
        Result.Status.Freshness != FreshnessState.Current;

    /// <summary>Asks whoever hosts this result to arm another loot capture (the shell's capture dialog).</summary>
    public event EventHandler? ScanAgainRequested;

    public ICommand ScanAgainCommand { get; }

    /// <summary>
    /// #572: the shell pushes what <see cref="Application.Services.CaptureSessions.LootAutoReturnPolicy"/>
    /// decided here once a second, so the page can show its own small countdown without owning any
    /// navigation or clock logic itself.
    /// </summary>
    private bool _showsAutoReturnControls;
    private bool _showsReturnCountdown;
    private bool _isStaying;
    private TimeSpan? _returnRemaining;

    /// <summary>Raised when the player toggles "Stay"; true pins the page for the rest of the raid.</summary>
    public event EventHandler<bool>? StayToggled;

    public ICommand ToggleStayCommand { get; }

    /// <summary>
    /// Whether the app opened this page by itself, mid-raid: worth offering "Stay" at all, whether
    /// or not a countdown happens to be running right now (pinned and switched-off both hide the
    /// countdown text but must not hide the toggle that explains why it is hidden).
    /// </summary>
    public bool ShowsAutoReturnControls
    {
        get => _showsAutoReturnControls;
        private set => SetProperty(ref _showsAutoReturnControls, value);
    }

    /// <summary>Whether the numeric countdown itself is on screen: eligible, unpinned, not switched off.</summary>
    public bool ShowsReturnCountdown
    {
        get => _showsReturnCountdown;
        private set
        {
            if (SetProperty(ref _showsReturnCountdown, value))
            {
                OnPropertyChanged(nameof(ReturnCountdownLabel));
            }
        }
    }

    public bool IsStaying
    {
        get => _isStaying;
        private set
        {
            if (SetProperty(ref _isStaying, value))
            {
                OnPropertyChanged(nameof(StayCommandLabel));
            }
        }
    }

    public string StayCommandLabel => IsStaying ? _text.StayingLabel : _text.StayLabel;

    public string ReturnCountdownLabel
    {
        get
        {
            var seconds = _returnRemaining is { } remaining ? (int)Math.Ceiling(remaining.TotalSeconds) : 0;
            return seconds > 0
                ? Format(_text.ReturnCountdownTemplate, ("seconds", seconds.ToString(_culture)))
                : _text.ReturnCountdownLessThanOne;
        }
    }

    /// <summary>Called by the shell, about once a second, with what the auto-return policy decided.</summary>
    public void UpdateAutoReturn(bool isEligible, bool showsCountdown, bool isPinned, TimeSpan? remaining)
    {
        _returnRemaining = remaining;
        ShowsAutoReturnControls = isEligible;
        ShowsReturnCountdown = showsCountdown;
        IsStaying = isPinned;
        OnPropertyChanged(nameof(ReturnCountdownLabel));
    }

    /// <summary>Verdict chips above the decision list; "All" is first and selected by default.</summary>
    public IReadOnlyList<LootScanFilterViewModel> Filters { get; }

    public LootScanVerdict? Filter => _filter;

    public LootScanGridViewModel? LootGrid { get; private set; }

    public LootScanGridViewModel? CarriedGrid { get; private set; }

    public bool HasLootGrid => LootGrid is not null;

    public bool HasNoLootGrid => LootGrid is null;

    public bool HasCarriedGrid => CarriedGrid is not null;

    public bool HasNoCarriedGrid => CarriedGrid is null;

    public string CarriedGridTitle => _shownCarriedGrid is { } identity
        ? CarriedGridName(identity, _text, _culture)
        : _text.CarriedSpace;

    public string CarriedSpaceSummary
    {
        get
        {
            var grids = Result.CarriedGrids.Count > 0
                ? Result.CarriedGrids
                : Result.CarriedGrid is { } legacy
                    ? [new CarriedGridRecognition(CarriedGridIdentity.PrimaryBackpack, legacy)]
                    : [];
            return string.Join(" · ", grids
                .GroupBy(grid => grid.Identity.Kind)
                .Select(group =>
                {
                    var capacity = group.Sum(grid =>
                        (grid.Recognition.Geometry.Rows.Value ?? 0) * (grid.Recognition.Geometry.Columns.Value ?? 0));
                    var occupied = group.Sum(grid => grid.Recognition.Cells.Sum(cell =>
                    {
                        var width = cell.Item.Value?.WidthCells.Value ??
                            CellsAcross(cell.Item.Bounds?.Width, grid.Recognition.Geometry.CellWidthPixels.Value);
                        var height = cell.Item.Value?.HeightCells.Value ??
                            CellsAcross(cell.Item.Bounds?.Height, grid.Recognition.Geometry.CellHeightPixels.Value);
                        return width * height;
                    }));
                    var free = Math.Max(0, capacity - occupied);
                    var name = CarriedGridName(new(group.Key, 0), _text, _culture);
                    return free == 0
                        ? Format(_text.CarriedGridFullTemplate, ("container", name))
                        : Format(_text.CarriedGridFreeTemplate, ("container", name), ("count", free.ToString(_culture)));
                }));
        }
    }

    public string LootItemsLabel => Format(
        Decisions.Count == 1 ? _text.OneItemTemplate : _text.ItemsTemplate,
        ("count", Decisions.Count.ToString(_culture)));

    /// <summary>"Take 3 · Swap 1 · Leave 1", naming only the verdicts that occur.</summary>
    public string DecisionSummary
    {
        get
        {
            var parts = new[]
                {
                    (_text.FilterTake, TakeCount),
                    (_text.FilterSwap, SwapCount),
                    (_text.FilterLeave, LeaveCount),
                    (_text.FilterReview, ReviewCount),
                }
                .Where(part => part.Item2 > 0)
                .Select(part => $"{part.Item1} {part.Item2.ToString(_culture)}");
            var summary = string.Join(" · ", parts);
            if (IsProgressive && Decisions.Count > 0)
            {
                return Format(_text.PendingCountTemplate, ("count", Decisions.Count.ToString(_culture)));
            }

            return summary.Length == 0 ? _text.NothingToDecide : summary;
        }
    }

    public LootScanDecisionViewModel? SelectedDecision
    {
        get => _selected;
        private set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelectedDecision));
            }
        }
    }

    public bool HasSelectedDecision => SelectedDecision is not null;

    public string TimingSummary => IsProgressive
        ? _text.ProgressTiming
        : Format(_text.AnalysedInTemplate, ("duration", TimingLabel));

    /// <summary>Adds one named cell once. Events from a stopped or different scan are ignored.</summary>
    public void AddPending(LootScanItemMatched matched)
    {
        ArgumentNullException.ThrowIfNull(matched);
        if (!IsProgressive || _progressStopped || matched.CorrelationId != CorrelationId ||
            matched.SessionId != Result.CaptureSessionId ||
            !string.Equals(matched.ArtifactId, Result.ArtifactId, StringComparison.Ordinal) ||
            matched.DecodeRevision != Result.DecodeRevision ||
            matched.Cell.Item.Value is null ||
            _decisions.Any(item => item.SourceAnchor == matched.Cell.Anchor))
        {
            return;
        }

        var pending = new LootScanDecision(
            matched.Cell.Anchor,
            matched.Cell.Item,
            LootScanVerdict.Review,
            [new LootScanReason("recognition.pending", _text.PendingReason)]);
        var row = new LootScanDecisionViewModel(
            pending,
            Result.EvaluatedUtc,
            openEvidence: null,
            _culture,
            _text,
            _controls,
            isPending: true,
            openWiki: _openWiki)
        {
            SelectAction = Select,
        };
        _decisions.Add(row);
        LootGrid = BuildLootGrid();
        if (SelectedDecision is null)
        {
            Select(row);
        }

        RefreshDecisions();
    }

    /// <summary>
    /// Reuses rows by grid anchor, updates them in place, then moves them into final planner order.
    /// </summary>
    public void Reconcile(LootScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.CorrelationId != CorrelationId || result.CaptureSessionId != Result.CaptureSessionId ||
            !string.Equals(result.ArtifactId, Result.ArtifactId, StringComparison.Ordinal) ||
            result.DecodeRevision != Result.DecodeRevision)
        {
            return;
        }

        var selectedAnchor = SelectedDecision?.SourceAnchor;
        var byAnchor = _decisions.ToDictionary(item => item.SourceAnchor);
        var ordered = result.Decisions
            .Select((decision, index) =>
            {
                if (byAnchor.TryGetValue(decision.SourceAnchor, out var existing))
                {
                    existing.Update(decision, result.EvaluatedUtc);
                    return (Decision: existing, Index: index);
                }

                return (Decision: new LootScanDecisionViewModel(
                    decision, result.EvaluatedUtc, null, _culture, _text, _controls, openWiki: _openWiki)
                {
                    SelectAction = Select,
                }, Index: index);
            })
            .OrderBy(entry => entry.Decision.IsReview)
            .ThenByDescending(entry => entry.Decision.IsAdvisedTake)
            .ThenByDescending(entry => entry.Decision.IsReview ? entry.Decision.CatalogValuePerSquareRoubles ?? -1 : 0)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Decision)
            .ToArray();

        for (var index = _decisions.Count - 1; index >= 0; index--)
        {
            if (!ordered.Contains(_decisions[index]))
            {
                _decisions.RemoveAt(index);
            }
        }

        for (var index = 0; index < ordered.Length; index++)
        {
            var current = _decisions.IndexOf(ordered[index]);
            if (current < 0)
            {
                _decisions.Insert(index, ordered[index]);
            }
            else if (current != index)
            {
                _decisions.Move(current, index);
            }
        }

        _result = result;
        _isProgressive = false;
        _progressStopped = false;
        Issues = result.Issues.Select(issue => issue.Explanation).Distinct(StringComparer.Ordinal).ToArray();
        LootGrid = BuildLootGrid();
        CarriedGrid = BuildCarriedGrid();
        Select((selectedAnchor is { } anchor ? _decisions.FirstOrDefault(item => item.SourceAnchor == anchor) : null) ??
               _decisions.FirstOrDefault(item => item.IsSwap) ??
               _decisions.FirstOrDefault(item => item.IsTake) ??
               _decisions.FirstOrDefault());
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(IsProgressive));
        RefreshDecisions();
    }

    public void StopProgress()
    {
        if (!IsProgressive || _progressStopped)
        {
            return;
        }

        _progressStopped = true;
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(TimingSummary));
    }

    private void RefreshDecisions()
    {
        _pageIndex = Math.Min(_pageIndex, Math.Max(0, PageCount - 1));
        foreach (var filter in Filters)
        {
            filter.Count = filter.Verdict switch
            {
                LootScanVerdict.Take => TakeCount,
                LootScanVerdict.Swap => SwapCount,
                LootScanVerdict.Leave => LeaveCount,
                LootScanVerdict.Review => ReviewCount,
                _ => Decisions.Count,
            };
        }

        foreach (var property in new[]
        {
            nameof(StatusLabel), nameof(StatusDetail), nameof(TimingSummary), nameof(VisibleDecisions),
            nameof(HasDecisions), nameof(HasNoVisibleLoot), nameof(HasUnavailableResult), nameof(LootItemsLabel),
            nameof(TakeCount), nameof(SwapCount), nameof(LeaveCount), nameof(ReviewCount), nameof(TakeSummary),
            nameof(SwapSummary), nameof(LeaveSummary), nameof(ReviewSummary), nameof(DecisionSummary),
            nameof(PageCount), nameof(HasMultiplePages), nameof(HasPreviousPage), nameof(HasNextPage),
            nameof(PageSummary), nameof(IsComplete), nameof(IsPartial), nameof(Issues), nameof(VisibleIssues),
            nameof(HasIssues), nameof(HasHiddenIssues), nameof(HiddenIssuesLabel), nameof(LootGrid),
            nameof(CarriedGrid), nameof(HasLootGrid), nameof(HasNoLootGrid), nameof(HasCarriedGrid),
            nameof(HasNoCarriedGrid), nameof(CarriedGridTitle), nameof(CarriedSpaceSummary),
        })
        {
            OnPropertyChanged(property);
        }
    }

    private IReadOnlyList<LootScanDecisionViewModel> FilteredDecisions => _filter is { } verdict
        ? Decisions.Where(item => item.Verdict == verdict).ToArray()
        : Decisions;

    public void Select(LootScanDecisionViewModel? decision)
    {
        if (decision is not null && !Decisions.Contains(decision))
        {
            return;
        }

        foreach (var item in Decisions)
        {
            item.IsSelected = ReferenceEquals(item, decision);
        }

        SelectedDecision = decision;
        CarriedGrid = BuildCarriedGrid();
        OnPropertyChanged(nameof(CarriedGrid));
        OnPropertyChanged(nameof(HasCarriedGrid));
        OnPropertyChanged(nameof(HasNoCarriedGrid));
        OnPropertyChanged(nameof(CarriedGridTitle));

        foreach (var tile in (LootGrid?.Tiles ?? []).Concat(CarriedGrid?.Tiles ?? []))
        {
            tile.IsSelected = tile.Decision is not null && ReferenceEquals(tile.Decision, decision);
        }
    }

    private void SetFilter(LootScanVerdict? verdict)
    {
        _filter = verdict;
        foreach (var chip in Filters)
        {
            chip.IsSelected = chip.Verdict == verdict;
        }

        _pageIndex = 0;
        OnPropertyChanged(nameof(Filter));
        OnPropertyChanged(nameof(VisibleDecisions));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(HasMultiplePages));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(PageSummary));
    }

    private LootScanGridViewModel? BuildLootGrid()
    {
        var byAnchor = Decisions.ToDictionary(item => item.SourceAnchor);
        var tiles = new List<LootScanGridTileViewModel>();
        var grid = Result.VisibleLootGrid;
        if (grid is not null)
        {
            foreach (var cell in grid.Cells)
            {
                byAnchor.TryGetValue(cell.Anchor, out var decision);
                tiles.Add(LootTile(cell.Anchor, cell.Item.Value, decision));
            }
        }
        else
        {
            tiles.AddRange(Decisions.Select(decision => LootTile(decision.SourceAnchor, decision.Item, decision)));
        }

        return tiles.Count == 0 && grid is null
            ? null
            : new LootScanGridViewModel(grid?.Geometry.Rows.Value, grid?.Geometry.Columns.Value, tiles, occupied: null, _text, _culture);
    }

    /// <remarks>
    /// A cell whose item was refused still has a measured footprint, and drawing it 1x1 left a
    /// hole where the rest of it was. The size comes from the named item, else from a candidate
    /// (every candidate was shortlisted at the measured size), else from the cell's own bounds.
    /// </remarks>
    private LootScanGridTileViewModel LootTile(GridCellAddress anchor, RecognizedItem? item, LootScanDecisionViewModel? decision)
    {
        var field = decision?.ItemField;
        var candidate = field?.Candidates.FirstOrDefault()?.Value;
        var geometry = Result.VisibleLootGrid?.Geometry;
        var width = item?.WidthCells.Value ?? candidate?.WidthCells.Value ??
            CellsAcross(field?.Bounds?.Width, geometry?.CellWidthPixels.Value);
        var height = item?.HeightCells.Value ?? candidate?.HeightCells.Value ??
            CellsAcross(field?.Bounds?.Height, geometry?.CellHeightPixels.Value);
        return new(anchor, width, height,
            decision?.Name ?? item?.DisplayName.Value ?? _text.UnknownItem,
            decision?.ShortValueLabel ?? string.Empty,
            LootScanTileKind.Loot, decision);
    }

    private static int CellsAcross(int? pixels, int? cellPixels) => pixels is > 0 && cellPixels is > 0
        ? Math.Max(1, (int)Math.Round(pixels.Value / (double)cellPixels.Value, MidpointRounding.AwayFromZero))
        : 1;

    private LootScanGridViewModel? BuildCarriedGrid()
    {
        var grids = Result.CarriedGrids.Count > 0
            ? Result.CarriedGrids
            : Result.CarriedGrid is { } legacy
                ? [new CarriedGridRecognition(CarriedGridIdentity.PrimaryBackpack, legacy)]
                : [];
        var wanted = SelectedDecision?.Placement?.CarriedGrid ??
            SelectedDecision?.Drops.FirstOrDefault()?.CarriedGrid ??
            grids.FirstOrDefault(candidate => candidate.Identity == CarriedGridIdentity.PrimaryBackpack)?.Identity ??
            grids.FirstOrDefault()?.Identity;
        var selectedGrid = wanted is { } identity
            ? grids.FirstOrDefault(candidate => candidate.Identity == identity)
            : null;
        var grid = selectedGrid?.Recognition;
        _shownCarriedGrid = selectedGrid?.Identity;
        var dropOwners = new Dictionary<GridCellAddress, LootScanDecisionViewModel>();
        foreach (var decision in Decisions)
        {
            foreach (var drop in decision.Drops.Where(drop => drop.CarriedGrid == _shownCarriedGrid))
            {
                dropOwners.TryAdd(drop.Anchor, decision);
            }
        }

        var tiles = new List<LootScanGridTileViewModel>();
        var occupied = 0;
        if (grid is not null)
        {
            foreach (var cell in grid.Cells)
            {
                var item = cell.Item.Value;
                // An item nobody could name still covers the squares it was seen to cover.
                var width = item?.WidthCells.Value ??
                    CellsAcross(cell.Item.Bounds?.Width, grid.Geometry.CellWidthPixels.Value);
                var height = item?.HeightCells.Value ??
                    CellsAcross(cell.Item.Bounds?.Height, grid.Geometry.CellHeightPixels.Value);
                occupied += width * height;
                var isDrop = dropOwners.TryGetValue(cell.Anchor, out var owner);
                tiles.Add(new(cell.Anchor, width, height,
                    item?.DisplayName.Value ?? _text.UnknownItem,
                    item?.Quantity.Value is > 1 and var quantity ? quantity.ToString(_culture) : string.Empty,
                    isDrop ? LootScanTileKind.Drop : LootScanTileKind.Carried,
                    owner));
            }
        }
        else
        {
            foreach (var decision in Decisions)
            {
                foreach (var drop in decision.Drops.Where(drop => drop.CarriedGrid == _shownCarriedGrid))
                {
                    tiles.Add(new(drop.Anchor, drop.Item.Value?.WidthCells.Value ?? 1, drop.Item.Value?.HeightCells.Value ?? 1,
                        drop.Item.Value?.DisplayName.Value ?? _text.UnknownItem, string.Empty, LootScanTileKind.Drop, decision));
                }
            }
        }

        // Where each take or swap would land, drawn over what it lands on.
        foreach (var decision in Decisions.Where(item =>
                     item.Placement?.CarriedGrid == _shownCarriedGrid && (item.IsTake || item.IsSwap)))
        {
            var placement = decision.Placement!;
            tiles.Add(new(placement.Anchor, placement.WidthCells, placement.HeightCells,
                decision.Name, string.Empty, LootScanTileKind.Incoming, decision));
        }

        return tiles.Count == 0 && grid is null
            ? null
            : new LootScanGridViewModel(grid?.Geometry.Rows.Value, grid?.Geometry.Columns.Value, tiles,
                grid is null ? null : occupied, _text, _culture);
    }

    private void PreviousPage()
    {
        if (!HasPreviousPage)
        {
            return;
        }

        _pageIndex--;
        PageChanged();
    }

    private void NextPage()
    {
        if (!HasNextPage)
        {
            return;
        }

        _pageIndex++;
        PageChanged();
    }

    private void PageChanged()
    {
        OnPropertyChanged(nameof(VisibleDecisions));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(PageSummary));
        PageNavigated?.Invoke(this, EventArgs.Empty);
    }

    private static string Format(string template, params (string Key, string Value)[] values) =>
        V2PresentationFormatting.Message(
            template,
            values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));

    internal static string CarriedGridName(
        CarriedGridIdentity identity,
        LootScanPresentationText text,
        CultureInfo culture)
    {
        var name = identity.Kind switch
        {
            CarriedGridKind.Backpack => text.Backpack,
            CarriedGridKind.TacticalRig => text.TacticalRig,
            CarriedGridKind.Pockets => text.Pockets,
            _ => text.CarriedSpace,
        };
        return identity.Index == 0
            ? name
            : Format(text.CarriedGridIndexTemplate, ("container", name), ("index", (identity.Index + 1).ToString(culture)));
    }
}

public sealed class LootScanDecisionViewModel : BindableViewModel
{
    private LootScanDecision _decision;
    private readonly CultureInfo _culture;
    private readonly LootScanPresentationText _text;
    private readonly bool _canOpenEvidence;
    private bool _isPending;

    public LootScanDecisionViewModel(
        LootScanDecision decision,
        DateTimeOffset evaluatedUtc,
        Action<LootScanDecision>? openEvidence,
        CultureInfo? culture = null,
        LootScanPresentationText? text = null,
        ILootScanWorkspaceControls? controls = null,
        bool isPending = false,
        Func<string, Task>? openWiki = null)
    {
        _decision = decision ?? throw new ArgumentNullException(nameof(decision));
        _culture = culture ?? CultureInfo.CurrentCulture;
        _text = text ?? LootScanPresentationText.Default;
        _controls = controls;
        _openWiki = openWiki;
        _isPending = isPending;
        _canOpenEvidence = openEvidence is not null;
        TogglePinCommand = new DelegateCommand(() => Change(id => _controls!.SetPinnedAsync(id, !IsPinned)));
        ToggleWishlistCommand = new DelegateCommand(() => Change(id => _controls!.SetWishlistedAsync(id, !IsWishlisted)));
        ToggleAlwaysTakeCommand = new DelegateCommand(() => Change(id => _controls!.SetRuleAsync(
            id,
            IsAlwaysTake ? LootScanItemRule.None : LootScanItemRule.AlwaysTake)));
        ToggleAlwaysLeaveCommand = new DelegateCommand(() => Change(id => _controls!.SetRuleAsync(
            id,
            IsAlwaysLeave ? LootScanItemRule.None : LootScanItemRule.AlwaysLeave)));
        EvaluatedUtc = evaluatedUtc;
        OpenEvidenceCommand = new DelegateCommand(() => openEvidence?.Invoke(_decision));
        OpenWikiCommand = new AsyncDelegateCommand(() =>
            ItemId is { } itemId && _openWiki is not null ? _openWiki(itemId) : Task.CompletedTask);
        SelectCommand = new DelegateCommand(() => SelectAction?.Invoke(this));
    }

    private readonly ILootScanWorkspaceControls? _controls;
    private readonly Func<string, Task>? _openWiki;

    private string? ItemId => _decision.Item.Value?.CanonicalId.Value;

    /// <summary>The call this row shows, for the saved copy of the scan (#274).</summary>
    internal LootScanDecision Decision => _decision;

    /// <summary>A named item can be pinned, wished for or given a rule. A cell nobody named cannot.</summary>
    public bool CanSetItemChoices => !IsPending && _controls is not null && ItemId is not null;

    public bool CanOpenWiki => !IsPending && _openWiki is not null && ItemId is not null;

    public ICommand OpenWikiCommand { get; }

    public bool IsPending => _isPending;

    internal void Update(LootScanDecision decision, DateTimeOffset evaluatedUtc)
    {
        _decision = decision ?? throw new ArgumentNullException(nameof(decision));
        EvaluatedUtc = evaluatedUtc;
        _isPending = false;
        OnPropertyChanged(string.Empty);
    }

    public bool IsPinned => ItemId is { } id && _controls?.IsPinned(id) == true;

    public bool IsWishlisted => ItemId is { } id && _controls?.IsWishlisted(id) == true;

    public bool IsAlwaysTake => ItemId is { } id && _controls?.RuleFor(id) == LootScanItemRule.AlwaysTake;

    public bool IsAlwaysLeave => ItemId is { } id && _controls?.RuleFor(id) == LootScanItemRule.AlwaysLeave;

    public string PinLabel => IsPinned ? _text.Unpin : _text.Pin;

    public string WishlistLabel => IsWishlisted ? _text.Unwish : _text.Wish;

    public string AlwaysTakeLabel => _text.AlwaysTake;

    public string AlwaysLeaveLabel => _text.AlwaysLeave;

    public ICommand TogglePinCommand { get; }

    public ICommand ToggleWishlistCommand { get; }

    public ICommand ToggleAlwaysTakeCommand { get; }

    public ICommand ToggleAlwaysLeaveCommand { get; }

    private void Change(Func<string, Task> change)
    {
        if (_controls is not null && ItemId is { } id)
        {
            _ = change(id);
        }
    }

    /// <summary>Set by the owning result so a row or grid tile can make this the selected decision.</summary>
    internal Action<LootScanDecisionViewModel>? SelectAction { get; init; }

    public ICommand SelectCommand { get; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public LootScanVerdict Verdict => _decision.Verdict;

    public GridCellAddress SourceAnchor => _decision.SourceAnchor;

    public RecognizedItem? Item => _decision.Item.Value;

    public LootScanPlacement? Placement => _decision.Placement;

    public IReadOnlyList<LootScanDropItem> Drops => _decision.Drops;

    /// <summary>
    /// One plain line under the item name: the strongest profile reason ("Current quest",
    /// "Hideout") when the recommendation names one, otherwise the planner's own first reason.
    /// </summary>
    public string HeadlineReason
    {
        get
        {
            if (IsPending)
            {
                return _text.PendingReason;
            }

            // A refusal says it is one. This used to fall through to "Value only", which read as
            // a reason to take an item nobody had identified.
            // What the Events page recorded outranks everything else the row could say: the
            // Safety category is shared with "protected", and an allergic item headed
            // "Protected item" told the player the opposite of what they had written down.
            if (EventHeadline is { } eventHeadline)
            {
                return eventHeadline;
            }

            if (_decision.Verdict == LootScanVerdict.Review)
            {
                return IsAdvisedTake ? AdviceHeadline : RefusalHeadline;
            }

            // The planner's own reasons are full sentences meant for the evidence disclosure; a
            // row gets the short form of what actually decided it.
            var code = _decision.Reasons.FirstOrDefault()?.Code;
            var planner = code switch
            {
                "capacity.visible-fit" => _text.ReasonFits,
                "capacity.bounded-swap" => _text.ReasonSwapFits,
                "capacity.no-supported-fit" => _text.ReasonNoRoom,
                "swap.cost-exceeds-value" => _text.ReasonSwapCosts,
                _ => null,
            };
            // Strongest first. This read weakest first, and nobody saw it because no scan had
            // ever produced a profile reason: a pinned quest item would have been headed "Pinned".
            var category = _decision.Recommendation?.Decision.Value?.Reasons
                .OrderByDescending(reason => reason.Priority)
                .Select(reason => (RecommendationReasonCategory?)reason.Category)
                .FirstOrDefault(value => value is not (RecommendationReasonCategory.Economics or RecommendationReasonCategory.EvidenceQuality));
            if (category is null && planner is not null)
            {
                return planner;
            }

            return Named(category switch
            {
                RecommendationReasonCategory.ExplicitOverride => _text.ReasonExplicit,
                RecommendationReasonCategory.Safety => _text.ReasonProtected,
                RecommendationReasonCategory.CurrentFoundInRaidQuest => _text.ReasonCurrentQuestFir,
                RecommendationReasonCategory.CurrentQuest => _text.ReasonCurrentQuest,
                RecommendationReasonCategory.FutureQuest => _text.ReasonFutureQuest,
                RecommendationReasonCategory.Hideout => _text.ReasonHideout,
                RecommendationReasonCategory.CraftOrBarter => _text.ReasonCraft,
                RecommendationReasonCategory.SpecialistUtility => _text.ReasonUtility,
                RecommendationReasonCategory.PinOrWishlist => _text.ReasonPinned,
                RecommendationReasonCategory.ScarcityOrObtainability => _text.ReasonScarce,
                _ => planner ?? _text.ReasonValueOnly,
            });
        }
    }

    /// <summary>
    /// "Current quest" becomes "Current quest · 10 for Acquaintance" where a need decided it.
    /// </summary>
    /// <remarks>
    /// The list row is the only thing most scans are read from, and "Current quest" did not say
    /// which one or how many. The need's name and count exist only inside the engine's sentence,
    /// so they are read out of it; a sentence this does not recognise leaves the label as it was.
    /// </remarks>
    private string Named(string label)
    {
        if (StrongestAdvice is not { Code: var code, Explanation: var sentence } ||
            !code.StartsWith("need.", StringComparison.Ordinal))
        {
            return label;
        }

        var match = NeedSentence.Match(sentence);
        return match.Success
            ? Message(_text.ReasonNeedTemplate, ("reason", label), ("count", match.Groups[1].Value), ("need", match.Groups[2].Value))
            : label;
    }

    private static readonly System.Text.RegularExpressions.Regex NeedSentence = new(
        @"^Keep (\d+) more(?: found-in-raid)? for (.+?) \((?:current|\d+ step\(s\) ahead)\)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    internal EvidencedValue<RecognizedItem> ItemField => _decision.Item;

    /// <summary>The Events page's result for this item, where the engine weighed one.</summary>
    private string? EventHeadline
    {
        get
        {
            var codes = (_decision.Recommendation?.Decision.Value?.Reasons ?? []).Select(reason => reason.Code).ToArray();
            return codes.Contains("event.allergic", StringComparer.Ordinal) ? _text.ReasonEventAllergic
                : codes.Contains("event.untested", StringComparer.Ordinal) && !IsAdvisedTake && _decision.Verdict == LootScanVerdict.Review ? _text.ReasonEventUntested
                : null;
        }
    }

    /// <summary>The engine's strongest reason that is about the item and not about the evidence.</summary>
    private V2RecommendationReason? StrongestAdvice =>
        _decision.Recommendation?.Decision.Value?.Reasons
            .OrderByDescending(reason => reason.Priority)
            .FirstOrDefault(reason => reason.Category != RecommendationReasonCategory.EvidenceQuality);

    /// <summary>
    /// The engine says take it and the planner could not finish the call.
    /// </summary>
    /// <remarks>
    /// Two things stop a take becoming a verdict while the advice itself stands: the backpack
    /// was not read, so no fit can be claimed, or one piece of evidence is missing, such as a
    /// stash scan to subtract holdings from. The contract keeps these as review, because a take
    /// verdict claims a fit. The player is looking at their own backpack and can judge the fit
    /// themselves, so what they are owed is the advice and what it is missing, not a refusal
    /// that reads the same as "nothing is known".
    /// </remarks>
    public bool IsAdvisedTake =>
        _decision.Verdict == LootScanVerdict.Review &&
        _decision.Item.Value is not null &&
        _decision.Recommendation?.Decision.Value?.Action is
            V2RecommendationAction.Take or V2RecommendationAction.Keep or V2RecommendationAction.Swap;

    /// <summary>Why the engine says take it: the strongest reason that is not about evidence.</summary>
    private string AdviceHeadline =>
        _decision.Recommendation?.Decision.Value?.Reasons
            .OrderByDescending(reason => reason.Priority)
            .Select(reason => reason.Category)
            .Where(category => category != RecommendationReasonCategory.EvidenceQuality)
            .Select(category => (string?)(category switch
            {
                RecommendationReasonCategory.ExplicitOverride => _text.ReasonExplicit,
                RecommendationReasonCategory.Safety => _text.ReasonProtected,
                RecommendationReasonCategory.CurrentFoundInRaidQuest => _text.ReasonCurrentQuestFir,
                RecommendationReasonCategory.CurrentQuest => _text.ReasonCurrentQuest,
                RecommendationReasonCategory.FutureQuest => _text.ReasonFutureQuest,
                RecommendationReasonCategory.Hideout => _text.ReasonHideout,
                RecommendationReasonCategory.CraftOrBarter => _text.ReasonCraft,
                RecommendationReasonCategory.SpecialistUtility => _text.ReasonUtility,
                RecommendationReasonCategory.PinOrWishlist => _text.ReasonPinned,
                RecommendationReasonCategory.ScarcityOrObtainability => _text.ReasonScarce,
                _ => _text.ReasonWorthItsSquares,
            }))
            .Select(label => label is null ? null : Named(label))
            .FirstOrDefault() ?? _text.ReasonWorthItsSquares;

    /// <summary>
    /// What the engine could not settle, in the player's words, from its own evidence reasons.
    /// </summary>
    /// <remarks>
    /// This was one fixed sentence: "the flea fee, your needs and your free space aren't known".
    /// It was true of every scan when it was written and would be false of most now, so it is
    /// built from what this item's evaluation actually reported missing.
    /// </remarks>
    private IReadOnlyList<string> Gaps
    {
        get
        {
            var gaps = new List<string>();
            foreach (var code in (_decision.Recommendation?.Decision.Value?.Reasons ?? [])
                         .Where(reason => reason.Category == RecommendationReasonCategory.EvidenceQuality)
                         .Select(reason => reason.Code))
            {
                var gap = code switch
                {
                    "raid-context.missing" or "raid-context.phase-unknown" or "raid-context.phase-untrusted" => _text.GapRaidPhase,
                    "raid-context.risk-unknown" or "raid-context.risk-untrusted" => _text.GapRaidRisk,
                    "economics.flea-net-untrusted" => _text.GapFleaNet,
                    "economics.trader-untrusted" or "economics.price-missing" => _text.GapPrice,
                    "scarcity.unknown" or "scarcity.untrusted" => _text.GapScarcity,
                    "profile.incomplete" => _text.GapProfile,
                    "profile.override-untrusted" => _text.GapItemRule,
                    "inventory.missing" or "inventory.partial" or "inventory.stale" or "inventory.untrusted" or "inventory.incompatible" => _text.GapHoldings,
                    "candidate.fir-untrusted" => _text.GapFoundInRaid,
                    _ => null,
                };
                if (gap is not null && !gaps.Contains(gap, StringComparer.Ordinal))
                {
                    gaps.Add(gap);
                }
            }

            if (_decision.Reasons.Any(reason => reason.Code == "carried.capacity-incomplete"))
            {
                gaps.Add(_text.GapBackpack);
            }

            return gaps;
        }
    }

    private string GapSentence(string lead)
    {
        var gaps = Gaps;
        return gaps.Count switch
        {
            0 => lead,
            1 => $"{lead} {Message(_text.GapOneTemplate, ("first", gaps[0]))}",
            _ => $"{lead} {Message(
                _text.GapManyTemplate,
                ("list", string.Join(", ", gaps.Take(gaps.Count - 1))),
                ("last", gaps[^1]))}",
        };
    }

    /// <summary>Why this cell has no take, swap or leave, in a few words.</summary>
    private string RefusalHeadline
    {
        get
        {
            if (_decision.Item.Value is null)
            {
                var names = _decision.Item.Candidates.Select(candidate => candidate.DisplayName).Distinct(StringComparer.Ordinal).ToArray();
                return names.Length switch
                {
                    0 => _text.RefusedNoMatch,
                    1 => Message(_text.RefusedOneLookalikeTemplate, ("first", names[0])),
                    2 => Message(_text.RefusedTwoLookalikesTemplate, ("first", names[0]), ("second", names[1])),
                    _ => Message(
                        _text.RefusedManyLookalikesTemplate,
                        ("first", names[0]),
                        ("second", names[1]),
                        ("count", (names.Length - 2).ToString(_culture))),
                };
            }

            if (_decision.Item.Value.Quantity.Value is null || _decision.Item.Value.Condition.Value is null)
            {
                return _text.RefusedAttributesUnread;
            }

            return CatalogValueRoubles is null ? _text.RefusedNoPrice : _text.RefusedValuedOnly;
        }
    }

    /// <summary>
    /// What the catalog says the item sells for, when the engine could not settle a net value:
    /// the better of the 24-hour flea average (before the fee) and the best trader.
    /// </summary>
    internal long? CatalogValueRoubles
    {
        get
        {
            var inputs = _decision.Economics?.Inputs;
            var flea = inputs?.FleaGrossRoubles.Value;
            var trader = inputs?.TraderRoubles.Value;
            return flea is null && trader is null ? null : Math.Max(flea ?? 0, trader ?? 0);
        }
    }

    /// <summary>The value shown is the catalog's figure, not a net the engine settled.</summary>
    public bool HasCatalogValueOnly =>
        _decision.Economics?.BestNetValueRoubles is null && CatalogValueRoubles is not null;

    private bool CatalogValueIsFlea =>
        (_decision.Economics?.Inputs.FleaGrossRoubles.Value ?? 0) >= (_decision.Economics?.Inputs.TraderRoubles.Value ?? 0) &&
        _decision.Economics?.Inputs.FleaGrossRoubles.Value is not null;

    internal long? CatalogValuePerSquareRoubles =>
        CatalogValueRoubles is { } value &&
        _decision.Item.Value is { WidthCells.Value: { } width, HeightCells.Value: { } height } &&
        width * height > 0
            ? value / (width * height)
            : null;

    /// <summary>"₽68k": the whole item's value, short enough for a grid tile.</summary>
    public string ShortValueLabel => (_decision.Economics?.BestNetValueRoubles ?? CatalogValueRoubles) is { } value
        ? CompactRoubles(value, _culture)
        : string.Empty;

    /// <summary>"₽68k / sq", or empty when the value per square is unknown.</summary>
    public string ShortValuePerSquareLabel => (_decision.Economics?.ValuePerSquareRoubles ?? CatalogValuePerSquareRoubles) is { } value
        ? Message(_text.ShortPerSquareTemplate, ("value", CompactRoubles(value, _culture)))
        : string.Empty;

    public bool HasShortValuePerSquare => ShortValuePerSquareLabel.Length > 0;

    /// <summary>What the selected swap gives up, as one line per carried item.</summary>
    public string DropSummary => _decision.Drops.Count == 0
        ? string.Empty
        : Message(
            _text.DropSummaryTemplate,
            ("items", string.Join(", ", _decision.Drops.Select(drop => ItemName(drop.Item, drop.Anchor)))),
            ("cost", CompactRoubles(_decision.ReplacementCostRoubles.GetValueOrDefault(), _culture)));

    public string ReplacementCostLabel => _decision.ReplacementCostRoubles is { } cost
        ? Message(_text.GivesUpTemplate, ("value", CompactRoubles(cost, _culture)))
        : string.Empty;

    internal static string CompactRoubles(long value, CultureInfo culture) => Math.Abs(value) switch
    {
        >= 1_000_000 => "₽" + (value / 1_000_000d).ToString("0.#", culture) + "M",
        >= 10_000 => "₽" + (value / 1_000d).ToString("0", culture) + "k",
        >= 1_000 => "₽" + (value / 1_000d).ToString("0.#", culture) + "k",
        _ => "₽" + value.ToString(culture),
    };

    public DateTimeOffset EvaluatedUtc { get; private set; }

    public ICommand OpenEvidenceCommand { get; }

    public bool CanOpenEvidence => !IsPending && _canOpenEvidence;

    public string AutomationId => $"v2-loot-scan-{_decision.SourceAnchor.Row}-{_decision.SourceAnchor.Column}";

    public string Name
    {
        get
        {
            var item = _decision.Item.Value;
            return item?.DisplayName.Value ?? item?.CanonicalId.Value ??
                Message(
                    _text.ItemAtTemplate,
                    ("row", (_decision.SourceAnchor.Row + 1).ToString(_culture)),
                    ("column", (_decision.SourceAnchor.Column + 1).ToString(_culture)));
        }
    }

    public string VerdictLabel => IsPending
        ? _text.VerdictPending
        : IsAdvisedTake ? _text.VerdictAdvisedTake : _decision.Verdict.ToString().ToUpperInvariant();

    public string AutomationSummary => Message(
        _text.AutomationSummaryTemplate,
        ("verdict", VerdictLabel),
        ("item", Name),
        ("reason", WhyLabel));

    public bool IsTake => !IsPending && _decision.Verdict == LootScanVerdict.Take;

    public bool IsSwap => !IsPending && _decision.Verdict == LootScanVerdict.Swap;

    public bool IsLeave => !IsPending && _decision.Verdict == LootScanVerdict.Leave;

    public bool IsReview => !IsPending && _decision.Verdict == LootScanVerdict.Review;

    /// <remarks>
    /// A decided call keeps the planner's sentences. A refusal gets one plain sentence instead:
    /// the engine's own are written for the evidence disclosure and name contract fields.
    /// </remarks>
    public string WhyLabel
    {
        get
        {
            if (IsPending)
            {
                return _text.PendingReason;
            }

            if (_decision.Verdict is LootScanVerdict.Take or LootScanVerdict.Swap)
            {
                // The planner's sentence is about the fit. Why the item is wanted at all - the
                // quest by name, the hideout level, the pin - is the engine's, and a TAKE that
                // said only "it fits" left the player to guess which of those it was.
                var fit = string.Join(" ", _decision.Reasons.Select(reason => reason.Explanation));
                return StrongestAdvice is { } wanted ? $"{wanted.Explanation} {fit}" : fit;
            }

            if (_decision.Verdict != LootScanVerdict.Review)
            {
                return string.Join(" ", _decision.Reasons.Select(reason => reason.Explanation));
            }

            if (_decision.Item.Value is null)
            {
                return _decision.Item.Candidates.Count == 0 ? _text.WhyNoMatch : _text.WhyLookalikes;
            }

            if (_decision.Item.Value.Quantity.Value is null || _decision.Item.Value.Condition.Value is null)
            {
                return _text.WhyAttributesUnread;
            }

            // What the player wrote on the Events page is the whole answer for this row.
            if ((_decision.Recommendation?.Decision.Value?.Reasons ?? [])
                .FirstOrDefault(reason => reason.Code == "event.allergic") is { } allergy)
            {
                return allergy.Explanation;
            }

            if (IsAdvisedTake)
            {
                var strongest = _decision.Recommendation!.Decision.Value!.Reasons
                    .OrderByDescending(reason => reason.Priority)
                    .FirstOrDefault(reason => reason.Category != RecommendationReasonCategory.EvidenceQuality);
                return GapSentence(strongest?.Explanation ?? _text.WhyWorthItsSquares);
            }

            return _decision.Reasons.FirstOrDefault()?.Code switch
            {
                "recommendation.incomplete" or "economics.incomplete" or "recommendation.missing" =>
                    CatalogValueRoubles is null ? _text.WhyNoPrice : GapSentence(_text.WhyValuedOnly),
                _ => string.Join(" ", _decision.Reasons.Select(reason => reason.Explanation)),
            };
        }
    }

    public string SizeLabel
    {
        get
        {
            var item = _decision.Item.Value;
            return item?.WidthCells.Value is { } width && item.HeightCells.Value is { } height
                ? Message(
                    _text.SizeTemplate,
                    ("width", width.ToString(_culture)),
                    ("height", height.ToString(_culture)),
                    ("squares", (width * height).ToString(_culture)))
                : _text.FootprintNeedsReview;
        }
    }

    public string AttributesLabel
    {
        get
        {
            var item = _decision.Item.Value;
            if (item is null)
            {
                return _text.IdentityUnresolved;
            }

            var parts = new List<string>();
            if (item.Quantity.Value is { } quantity)
            {
                parts.Add(Message(_text.StackTemplate, ("quantity", quantity.ToString(_culture))));
            }

            if (item.FoundInRaid.Value is { } foundInRaid)
            {
                parts.Add(foundInRaid ? _text.FoundInRaid : _text.NotFoundInRaid);
            }

            if (item.Condition.Value is { } condition && condition.Kind != ItemConditionKind.NotApplicable)
            {
                parts.Add(condition.Current is { } current && condition.Maximum is { } maximum
                    ? Message(
                        _text.ConditionValuesTemplate,
                        ("current", current.ToString("0.#", _culture)),
                        ("maximum", maximum.ToString("0.#", _culture)))
                    : Message(
                        _text.ConditionKindTemplate,
                        ("kind", condition.Kind.ToString().ToLowerInvariant())));
            }

            return parts.Count == 0 ? _text.NoAttributeDetail : string.Join(" • ", parts);
        }
    }

    public string ValueLabel => _decision.Economics?.BestNetValueRoubles is { } value
        ? Message(_text.NetValueTemplate, ("value", Roubles(value)))
        : CatalogValueRoubles is { } catalog
            ? Roubles(catalog)
            : _text.ValueNeedsReview;

    public string ValuePerSquareLabel => _decision.Economics?.ValuePerSquareRoubles is { } value
        ? Message(
            _text.ValuePerSquareTemplate,
            ("value", Roubles(value)),
            ("band", _decision.Economics.ValueBand?.ToString().ToLowerInvariant() ?? _text.UnknownValueBand))
        : CatalogValuePerSquareRoubles is { } catalog
            ? Message(_text.CatalogValuePerSquareTemplate, ("value", Roubles(catalog)))
            : _text.ValuePerSquareUnavailable;

    public string PriceBasisLabel => _decision.Economics?.SelectedPriceBasis switch
    {
        "flea-net" => _text.FleaNetBasis,
        "trader" => _text.TraderBasis,
        _ when CatalogValueRoubles is not null => CatalogValueIsFlea ? _text.FleaAverageBasis : _text.TraderBasis,
        _ => _text.PriceSourceNeedsReview,
    };

    public string PlacementLabel
    {
        get
        {
            if (_decision.Placement is not { } placement)
            {
                return string.Empty;
            }

            var template = placement.RotateFromObserved
                ? _text.PlacementRotatedTemplate
                : _text.PlacementTemplate;
            return Message(
                template,
                ("container", LootScanViewModel.CarriedGridName(placement.CarriedGrid, _text, _culture).ToLower(_culture)),
                ("row", (placement.Anchor.Row + 1).ToString(_culture)),
                ("column", (placement.Anchor.Column + 1).ToString(_culture)));
        }
    }

    public bool HasPlacement => _decision.Placement is not null;

    public string SwapLabel => _decision.Verdict == LootScanVerdict.Swap
        ? Message(
            _text.SwapSummaryTemplate,
            ("count", _decision.Drops.Count.ToString(_culture)),
            ("cost", Roubles(_decision.ReplacementCostRoubles.GetValueOrDefault())))
        : string.Empty;

    public bool HasSwap => _decision.Verdict == LootScanVerdict.Swap;

    public IReadOnlyList<string> DropLabels => _decision.Drops
        .Select(drop => Message(
            _text.DropTemplate,
            ("item", ItemName(drop.Item, drop.Anchor)),
            ("container", LootScanViewModel.CarriedGridName(drop.CarriedGrid, _text, _culture).ToLower(_culture)),
            ("row", (drop.Anchor.Row + 1).ToString(_culture)),
            ("column", (drop.Anchor.Column + 1).ToString(_culture)),
            ("cost", Roubles(drop.ReplacementValueRoubles)),
            ("evidence", DescribeProvenance(drop.ValueProvenance))))
        .ToArray();

    public IReadOnlyList<string> RecommendationReasonLabels =>
        _decision.Recommendation?.Decision.Value?.Reasons
            .Take(LootScanPlannerLimits.MaximumRecommendationReasons)
            .Select(reason => Message(
                _text.RecommendationReasonTemplate,
                ("category", reason.Category.ToString()),
                ("reason", reason.Explanation),
                ("code", reason.Code),
                ("evidence", DescribeProvenance(reason.Provenance))))
            .ToArray() ?? [];

    public bool HasRecommendationReasons => RecommendationReasonLabels.Count > 0;

    public string OpportunityCostLabel
    {
        get
        {
            var recommendation = _decision.Recommendation?.Decision.Value;
            if (recommendation is null || recommendation.OpportunityCostRoubles.Value is not { } value)
            {
                return _text.OpportunityCostUnavailable;
            }

            var opportunityCost = recommendation.OpportunityCostRoubles;
            return recommendation.OpportunityCostLineage is { } lineage
                ? Message(
                    _text.OpportunityCostLineageTemplate,
                    ("value", Roubles(value)),
                    ("evidence", DescribeProvenance(opportunityCost.Provenance)),
                    ("priceEvidence", DescribeProvenance(lineage.Price)),
                    ("footprintEvidence", DescribeProvenance(lineage.Footprint)))
                : Message(
                    _text.OpportunityCostTemplate,
                    ("value", Roubles(value)),
                    ("evidence", DescribeProvenance(opportunityCost.Provenance)));
        }
    }

    public string EconomicEvidenceLabel
    {
        get
        {
            if (_decision.Economics is not { } economics)
            {
                return _text.EconomicEvidenceUnavailable;
            }

            var price = economics.SelectedPriceBasis == "trader"
                ? economics.Inputs.TraderRoubles
                : economics.Inputs.FleaNetRoubles.Value is not null
                    ? economics.Inputs.FleaNetRoubles
                    : economics.Inputs.TraderRoubles;
            return economics.CalculationProvenance is { } calculation
                ? Message(
                    _text.EconomicEvidenceWithInputsTemplate,
                    ("basis", PriceBasisLabel),
                    ("evidence", DescribeProvenance(calculation)),
                    ("priceEvidence", DescribeProvenance(price.Provenance)),
                    ("footprintEvidence", DescribeProvenance(economics.Inputs.OccupiedSquares.Provenance)))
                : Message(
                    _text.EconomicInputsEvidenceTemplate,
                    ("status", economics.Status.Code ?? _text.UnknownEconomicStatus),
                    ("priceEvidence", DescribeProvenance(price.Provenance)),
                    ("footprintEvidence", DescribeProvenance(economics.Inputs.OccupiedSquares.Provenance)));
        }
    }

    public string EvidenceLabel
    {
        get
        {
            return DescribeProvenance(_decision.Item.Provenance);
        }
    }

    public string CandidateLabel => AlternateIdentityCount() switch
    {
        0 => _text.IdentityMatched,
        1 => _text.OneAlternateIdentity,
        var count => Message(_text.AlternateIdentitiesTemplate, ("count", count.ToString(_culture))),
    };

    public string EvidenceDisclosureName => Message(
        _text.EvidenceDisclosureTemplate,
        ("item", Name));

    public string EvidenceActionLabel => !CanOpenEvidence
        ? _text.EvidenceUnavailable
        : IsReview
            ? _text.ReviewOrCorrect
            : _text.OpenEvidence;

    private string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return _text.JustNow;
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Message(
                _text.MinutesOldTemplate,
                ("value", Math.Floor(age.TotalMinutes).ToString(_culture)));
        }

        return age < TimeSpan.FromDays(1)
            ? Message(
                _text.HoursOldTemplate,
                ("value", Math.Floor(age.TotalHours).ToString(_culture)))
            : Message(
                _text.DaysOldTemplate,
                ("value", Math.Floor(age.TotalDays).ToString(_culture)));
    }

    private string DescribeProvenance(EvidenceProvenance provenance)
    {
        var age = provenance.EvidenceThroughUtc > EvaluatedUtc
            ? _text.TimestampAhead
            : DescribeAge(EvaluatedUtc - provenance.EvidenceThroughUtc);
        var confidence = provenance.Confidence.Score is { } score
            ? score.ToString("P0", _culture)
            : _text.Unscored;
        return Message(
            _text.ProvenanceTemplate,
            ("source", provenance.SourceClass.ToString()),
            ("age", age),
            ("confidence", confidence));
    }

    private int AlternateIdentityCount()
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var current = _decision.Item.Value?.CanonicalId.Value;
        foreach (var candidate in _decision.Item.Candidates)
        {
            AddIdentity(candidate.Value.CanonicalId.Value);
            foreach (var nested in candidate.Value.CanonicalId.Candidates)
            {
                AddIdentity(nested.Value);
            }
        }

        if (_decision.Item.Value is { } item)
        {
            foreach (var candidate in item.CanonicalId.Candidates)
            {
                AddIdentity(candidate.Value);
            }
        }

        return identities.Count;

        void AddIdentity(string? identity)
        {
            if (!string.IsNullOrWhiteSpace(identity) && !string.Equals(identity, current, StringComparison.Ordinal))
            {
                identities.Add(identity);
            }
        }
    }

    private string ItemName(EvidencedValue<RecognizedItem> item, GridCellAddress anchor) =>
        item.Value?.DisplayName.Value ?? item.Value?.CanonicalId.Value ??
        Message(
            _text.ItemAtTemplate,
            ("row", (anchor.Row + 1).ToString(_culture)),
            ("column", (anchor.Column + 1).ToString(_culture)));

    private string Roubles(long value) =>
        V2PresentationFormatting.Currency(value, "RUB", 0, _culture);

    private static string Message(string template, params (string Key, string Value)[] values) =>
        V2PresentationFormatting.Message(
            template,
            values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
}

/// <summary>Whole-message resources used by the Loot Scan presentation adapter.</summary>
public sealed record LootScanPresentationText
{
    public string Heading { get; init; } = "Loot Scan";
    public string StatusStale { get; init; } = "Evidence expired — review needed";
    public string StatusUnknown { get; init; } = "Evidence freshness unknown";
    public string StatusComplete { get; init; } = "Ready to review";
    public string StatusPartial { get; init; } = "Review needed";
    public string StatusUnavailable { get; init; } = "No usable result";
    public string StatusRecognising { get; init; } = "Recognising loot";
    public string StatusStopped { get; init; } = "Scan stopped";
    public string StatusDetailStale { get; init; } = "Retake or refresh the scan before relying on its recommendations.";
    public string StatusDetailUnknown { get; init; } = "The scan cannot prove when all supporting evidence was current.";
    public string StatusDetailComplete { get; init; } = "Every recommendation is tied to the reviewed screenshot and visible carried space.";
    public string StatusDetailPartial { get; init; } = "Uncertain cells stay visible and are never promoted into a take or swap.";
    public string StatusDetailUnavailable { get; init; } = "Retake the screenshot with both the loot and carried grid visible.";
    public string StatusDetailRecognising { get; init; } = "Matched items appear while the rest of the grid is still being read.";
    public string StatusDetailStopped { get; init; } = "This scan was cancelled or replaced before its decision finished.";
    public string CurrentCaptureContext { get; init; } = "Current capture context";
    public string CaptureTemplate { get; init; } = "Capture {correlation} • decode {revision}";
    public string MillisecondsTemplate { get; init; } = "{value} ms";
    public string SecondsTemplate { get; init; } = "{value} s";
    public string FocusTemplate { get; init; } = "Result returns to {device}";
    public string TakeCountTemplate { get; init; } = "{count} take";
    public string SwapCountTemplate { get; init; } = "{count} swap";
    public string LeaveCountTemplate { get; init; } = "{count} leave";
    public string ReviewCountTemplate { get; init; } = "{count} review";
    public string HiddenIssuesTemplate { get; init; } = "+{count} more limitations. Open evidence for item-level detail.";
    public string PageTemplate { get; init; } = "Page {page} of {pages} • {items} items";
    public string AutomationSummaryTemplate { get; init; } = "{verdict}: {item}. {reason}";
    public string SizeTemplate { get; init; } = "{width}×{height} • {squares} squares";
    public string FootprintNeedsReview { get; init; } = "Footprint needs review";
    public string IdentityUnresolved { get; init; } = "Identity unresolved";
    public string StackTemplate { get; init; } = "stack {quantity}";
    public string FoundInRaid { get; init; } = "found in raid";
    public string NotFoundInRaid { get; init; } = "not found in raid";
    public string ConditionValuesTemplate { get; init; } = "condition {current}/{maximum}";
    public string ConditionKindTemplate { get; init; } = "condition {kind}";
    public string NoAttributeDetail { get; init; } = "No stack or condition detail";
    public string NetValueTemplate { get; init; } = "{value} net";
    public string ValueNeedsReview { get; init; } = "Value needs review";
    public string ValuePerSquareTemplate { get; init; } = "{value} per square • {band}";
    public string ValuePerSquareUnavailable { get; init; } = "Value per square unavailable";
    public string UnknownValueBand { get; init; } = "unknown band";
    public string FleaNetBasis { get; init; } = "Flea net after fee";
    public string TraderBasis { get; init; } = "Best trader value";
    public string PriceSourceNeedsReview { get; init; } = "Price source needs review";
    public string PlacementTemplate { get; init; } = "Place in {container}, row {row}, column {column}";
    public string PlacementRotatedTemplate { get; init; } = "Place in {container}, row {row}, column {column} • rotate";
    public string SwapSummaryTemplate { get; init; } = "Replace {count} item(s) • {cost} given up";
    public string DropTemplate { get; init; } = "Drop {item} from {container} at row {row}, column {column} ({cost}) • {evidence}";
    public string RecommendationReasonTemplate { get; init; } = "{category}: {reason} [{code}] • {evidence}";
    public string OpportunityCostTemplate { get; init; } = "Opportunity cost {value} • {evidence}";
    public string OpportunityCostLineageTemplate { get; init; } = "Opportunity cost {value} • calculation {evidence} • price {priceEvidence} • footprint {footprintEvidence}";
    public string OpportunityCostUnavailable { get; init; } = "Opportunity cost unavailable";
    public string EconomicEvidenceWithInputsTemplate { get; init; } = "{basis} • calculation {evidence} • price {priceEvidence} • footprint {footprintEvidence}";
    public string EconomicInputsEvidenceTemplate { get; init; } = "Economic evidence needs review [{status}] • price {priceEvidence} • footprint {footprintEvidence}";
    public string UnknownEconomicStatus { get; init; } = "unspecified";
    public string EconomicEvidenceUnavailable { get; init; } = "Economic calculation evidence unavailable";
    public string EvidenceDisclosureTemplate { get; init; } = "Evidence and alternatives for {item}";
    public string IdentityMatched { get; init; } = "Identity matched";
    public string OneAlternateIdentity { get; init; } = "1 alternate identity";
    public string AlternateIdentitiesTemplate { get; init; } = "{count} alternate identities";
    public string EvidenceUnavailable { get; init; } = "Evidence unavailable";
    public string ReviewOrCorrect { get; init; } = "Review / correct";
    public string OpenEvidence { get; init; } = "Open evidence";
    public string ProvenanceTemplate { get; init; } = "{source} • {age} • {confidence} confidence";
    public string TimestampAhead { get; init; } = "timestamp ahead";
    public string Unscored { get; init; } = "unscored";
    public string JustNow { get; init; } = "just now";
    public string MinutesOldTemplate { get; init; } = "{value} min old";
    public string HoursOldTemplate { get; init; } = "{value} h old";
    public string DaysOldTemplate { get; init; } = "{value} d old";
    public string ItemAtTemplate { get; init; } = "item at row {row}, column {column}";
    public string FilterAll { get; init; } = "All";
    public string FilterTake { get; init; } = "Take";
    public string FilterSwap { get; init; } = "Swap";
    public string FilterLeave { get; init; } = "Leave";
    public string FilterReview { get; init; } = "Review";
    public string NothingToDecide { get; init; } = "Nothing to decide";
    public string PendingCountTemplate { get; init; } = "{count} pending";
    public string OneItemTemplate { get; init; } = "{count} item";
    public string ItemsTemplate { get; init; } = "{count} items";
    public string AnalysedInTemplate { get; init; } = "Analysed in {duration}";
    public string ProgressTiming { get; init; } = "Deciding as items arrive";
    public string ShortPerSquareTemplate { get; init; } = "{value} / sq";
    public string GivesUpTemplate { get; init; } = "gives up {value}";
    public string DropSummaryTemplate { get; init; } = "Drop {items} · {cost} given up";
    public string GridSizeTemplate { get; init; } = "{columns} × {rows} squares";
    public string FreeSquaresTemplate { get; init; } = "{count} free squares";
    public string OneFreeSquare { get; init; } = "1 free square";
    public string UnknownItem { get; init; } = "Unknown item";
    public string CarriedSpace { get; init; } = "Carried space";
    public string Backpack { get; init; } = "Backpack";
    public string TacticalRig { get; init; } = "Rig";
    public string Pockets { get; init; } = "Pockets";
    public string CarriedGridIndexTemplate { get; init; } = "{container} {index}";
    public string CarriedGridFullTemplate { get; init; } = "{container} full";
    public string CarriedGridFreeTemplate { get; init; } = "{container} {count} free";
    public string ReasonExplicit { get; init; } = "Your own rule";
    public string ReasonProtected { get; init; } = "Protected item";
    public string ReasonCurrentQuestFir { get; init; } = "Current quest · found in raid";
    public string ReasonCurrentQuest { get; init; } = "Current quest";
    public string ReasonFutureQuest { get; init; } = "Future quest";
    public string ReasonHideout { get; init; } = "Hideout";
    public string ReasonCraft { get; init; } = "Craft or barter";
    public string ReasonUtility { get; init; } = "Useful gear";
    public string ReasonPinned { get; init; } = "Pinned";
    public string ReasonScarce { get; init; } = "Hard to find";
    public string ReasonValueOnly { get; init; } = "Value only";
    public string ReasonNeedTemplate { get; init; } = "{reason} · {count} for {need}";
    public string ReasonEventAllergic { get; init; } = "Allergic · do not eat";
    public string ReasonEventUntested { get; init; } = "Untested event item";
    public string RefusedNoMatch { get; init; } = "Not recognised";
    public string RefusedOneLookalikeTemplate { get; init; } = "Maybe {first}";
    public string RefusedTwoLookalikesTemplate { get; init; } = "{first} or {second}";
    public string RefusedManyLookalikesTemplate { get; init; } = "{first}, {second} or {count} more";
    public string RefusedAttributesUnread { get; init; } = "Count or condition not read";
    public string RefusedNoPrice { get; init; } = "No price";
    public string RefusedValuedOnly { get; init; } = "Valued, not decided";
    public string WhyNoMatch { get; init; } = "No icon in the catalog matches this cell closely enough to name it.";
    public string WhyLookalikes { get; init; } = "These icons are too alike to tell apart from the picture, so none is chosen.";
    public string WhyAttributesUnread { get; init; } = "Its stack count or remaining uses can't be read from the screenshot, so it isn't valued.";
    public string WhyNoPrice { get; init; } = "The catalog has no flea or trader price for it.";
    public string WhyValuedOnly { get; init; } = "Priced from the catalog. No take or leave call yet.";
    public string WhyWorthItsSquares { get; init; } = "Worth the squares it takes.";
    public string ReasonWorthItsSquares { get; init; } = "Worth its squares";
    public string VerdictAdvisedTake { get; init; } = "TAKE?";
    public string VerdictPending { get; init; } = "PENDING";
    public string PendingReason { get; init; } = "Matched — decision pending";
    public string Pin { get; init; } = "Pin";
    public string Unpin { get; init; } = "Pinned";
    public string Wish { get; init; } = "Wishlist";
    public string Unwish { get; init; } = "On wishlist";
    public string AlwaysTake { get; init; } = "Always take";
    public string AlwaysLeave { get; init; } = "Always leave";
    public string PhaseLabel { get; init; } = "Raid";
    public string RiskLabel { get; init; } = "Risk";
    public string PhaseCounted { get; init; } = "From the clock";
    public string PhaseEarly { get; init; } = "Early";
    public string PhaseMiddle { get; init; } = "Middle";
    public string PhaseLate { get; init; } = "Late";
    public string PhaseExtracting { get; init; } = "Heading out";
    public string RiskLow { get; init; } = "Normal";
    public string RiskElevated { get; init; } = "Careful";
    public string RiskHigh { get; init; } = "High";
    public string RiskCritical { get; init; } = "Critical";
    public string GapOneTemplate { get; init; } = "Not known: {first}.";
    public string GapManyTemplate { get; init; } = "Not known: {list} and {last}.";
    public string GapRaidPhase { get; init; } = "the raid phase (pick it above)";
    public string GapRaidRisk { get; init; } = "how much risk you'll carry it through";
    public string GapFleaNet { get; init; } = "what the flea returns after its fee";
    public string GapPrice { get; init; } = "a current price";
    public string GapScarcity { get; init; } = "how readily another turns up";
    public string GapProfile { get; init; } = "your quest progress";
    public string GapItemRule { get; init; } = "what your rule for this item means";
    public string GapHoldings { get; init; } = "what you already hold (no stash scan yet)";
    public string GapFoundInRaid { get; init; } = "whether it is found in raid";
    public string GapBackpack { get; init; } = "whether it fits (your backpack wasn't read)";
    public string FleaAverageBasis { get; init; } = "24-hour flea average, before the fee";
    public string CatalogValuePerSquareTemplate { get; init; } = "{value} per square";
    public string ReasonFits { get; init; } = "Fits the free space";
    public string ReasonSwapFits { get; init; } = "Fits after a swap";
    public string ReasonNoRoom { get; init; } = "No room for it";
    public string ReasonSwapCosts { get; init; } = "A swap would cost more than it gains";

    /// <summary>#572: the small countdown that says the page is about to return to the map.</summary>
    public string ReturnCountdownTemplate { get; init; } = "Map in {seconds}s";
    public string ReturnCountdownLessThanOne { get; init; } = "Map in <1s";
    public string StayLabel { get; init; } = "Stay";
    public string StayingLabel { get; init; } = "Staying";

    public static LootScanPresentationText Default { get; } = new();
}

/// <summary>A verdict chip over the decision list.</summary>
public sealed class LootScanFilterViewModel : BindableViewModel
{
    private bool _isSelected;

    public LootScanFilterViewModel(LootScanVerdict? verdict, string label, int count, Action<LootScanVerdict?> select)
    {
        Verdict = verdict;
        Label = label;
        Count = count;
        SelectCommand = new DelegateCommand(() => select(verdict));
    }

    public LootScanVerdict? Verdict { get; }

    public string Label { get; }

    private int _count;

    public int Count
    {
        get => _count;
        internal set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(CountLabel));
            }
        }
    }

    public string CountLabel => Count.ToString(CultureInfo.CurrentCulture);

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public enum LootScanTileKind
{
    /// <summary>An item in the container, coloured by its verdict.</summary>
    Loot = 1,

    /// <summary>Something already carried that no decision touches.</summary>
    Carried,

    /// <summary>A carried item a swap would drop.</summary>
    Drop,

    /// <summary>Where a take or swap would put the new item.</summary>
    Incoming,
}

/// <summary>
/// One reviewed grid (the container, or the carried backpack) laid out on a fixed square size.
/// The view scales the whole grid down to fit; positions stay in grid units times
/// <see cref="CellSize"/> so tiles and the cell pattern line up at any scale.
/// </summary>
public sealed class LootScanGridViewModel
{
    public const double CellSize = 80;

    public LootScanGridViewModel(
        int? rows,
        int? columns,
        IReadOnlyList<LootScanGridTileViewModel> tiles,
        int? occupied,
        LootScanPresentationText text,
        CultureInfo culture)
    {
        Tiles = tiles;
        var extentRows = tiles.Select(tile => tile.Row + tile.HeightCells).DefaultIfEmpty(1).Max();
        var extentColumns = tiles.Select(tile => tile.Column + tile.WidthCells).DefaultIfEmpty(1).Max();
        Rows = Math.Max(rows ?? extentRows, extentRows);
        Columns = Math.Max(columns ?? extentColumns, extentColumns);
        SizeKnown = rows is not null && columns is not null;
        SizeLabel = SizeKnown
            ? V2PresentationFormatting.Message(text.GridSizeTemplate, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["columns"] = Columns.ToString(culture),
                ["rows"] = Rows.ToString(culture),
            })
            : string.Empty;
        if (SizeKnown && occupied is { } used)
        {
            FreeSquares = Math.Max(0, (Rows * Columns) - used);
            FreeSquaresLabel = FreeSquares == 1
                ? text.OneFreeSquare
                : V2PresentationFormatting.Message(text.FreeSquaresTemplate, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["count"] = FreeSquares.Value.ToString(culture),
                });
        }
    }

    public int Rows { get; }

    public int Columns { get; }

    public bool SizeKnown { get; }

    public string SizeLabel { get; }

    public int? FreeSquares { get; }

    public string FreeSquaresLabel { get; } = string.Empty;

    public bool HasFreeSquares => FreeSquaresLabel.Length > 0;

    public double PixelWidth => Columns * CellSize;

    public double PixelHeight => Rows * CellSize;

    /// <summary>How far the view may scale a small grid up before its squares stop reading as squares.</summary>
    public double MaxPixelWidth => PixelWidth * 1.4;

    public IReadOnlyList<LootScanGridTileViewModel> Tiles { get; }
}

public sealed class LootScanGridTileViewModel : BindableViewModel
{
    private const double Gap = 2;
    private bool _isSelected;

    public LootScanGridTileViewModel(
        GridCellAddress anchor,
        int widthCells,
        int heightCells,
        string name,
        string detail,
        LootScanTileKind kind,
        LootScanDecisionViewModel? decision)
    {
        Row = anchor.Row;
        Column = anchor.Column;
        WidthCells = Math.Max(1, widthCells);
        HeightCells = Math.Max(1, heightCells);
        Name = name;
        Detail = detail;
        Kind = kind;
        Decision = decision;
        SelectCommand = new DelegateCommand(() => decision?.SelectCommand.Execute(null));
    }

    public int Row { get; }

    public int Column { get; }

    public int WidthCells { get; }

    public int HeightCells { get; }

    public double Left => (Column * LootScanGridViewModel.CellSize) + Gap;

    public double Top => (Row * LootScanGridViewModel.CellSize) + Gap;

    public double Width => (WidthCells * LootScanGridViewModel.CellSize) - (2 * Gap);

    public double Height => (HeightCells * LootScanGridViewModel.CellSize) - (2 * Gap);

    public string Name { get; }

    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    public LootScanTileKind Kind { get; }

    public LootScanDecisionViewModel? Decision { get; }

    public bool CanSelect => Decision is not null;

    public ICommand SelectCommand { get; }

    public bool IsTake => Kind == LootScanTileKind.Loot && Decision?.IsTake == true;

    public bool IsSwap => Kind == LootScanTileKind.Loot && Decision?.IsSwap == true;

    public bool IsLeave => Kind == LootScanTileKind.Loot && Decision?.IsLeave == true;

    public bool IsReview => Kind == LootScanTileKind.Loot && (Decision is null || Decision.IsReview);

    public bool IsCarried => Kind == LootScanTileKind.Carried;

    public bool IsDrop => Kind == LootScanTileKind.Drop;

    public bool IsIncoming => Kind == LootScanTileKind.Incoming;

    /// <summary>
    /// Where a take or swap would land is drawn over what is already carried, so only the
    /// selected call's placement, and the carried items it gives up, are marked; drawing every
    /// call's placement at once stacked four labels on the same squares.
    /// </summary>
    public bool ShowsTile => Kind != LootScanTileKind.Incoming || IsSelected;

    public bool IsGivenUp => IsDrop && IsSelected;

    public string AutomationName => HasDetail ? $"{Name}, {Detail}" : Name;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(ShowsTile));
                OnPropertyChanged(nameof(IsGivenUp));
            }
        }
    }
}
