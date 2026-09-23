using System.Globalization;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.App.ViewModels.V2.Intel;

public sealed record AcquisitionChainStepViewModel(
    string Label,
    string SourceLabel,
    string CostLabel,
    string OverheadLabel,
    double Indent)
{
    public bool HasOverhead => OverheadLabel.Length > 0;
}

/// <summary>Presentation shared by Intel item detail and Crafts &amp; barters' chain view.</summary>
public sealed class AcquisitionChainViewModel(IAcquisitionChainPlanningService planner) : BindableViewModel
{
    private CancellationTokenSource? _loadCts;
    private AcquisitionChainPlan? _plan;
    private bool _loading;
    private string _selectedItemId = string.Empty;

    internal Task LoadTask { get; private set; } = Task.CompletedTask;
    public bool HasSelection => _selectedItemId.Length > 0;
    public bool IsLoading => _loading;
    public bool HasRoute => _plan?.Cheapest is not null;
    public bool ShowsEmpty => HasSelection && !_loading && !HasRoute;
    public string Heading => V2ShellText.Get("V2.Shell.Intel.Chain.Heading");
    public string LoadingLabel => V2ShellText.Get("V2.Shell.Intel.Chain.Loading");
    public string EmptyLabel => V2ShellText.Get("V2.Shell.Intel.Chain.Empty");
    public string TotalLabel => _plan?.Cheapest is { } step
        ? V2ShellText.Format("V2.Shell.Intel.Chain.Total", CultureInfo.CurrentCulture, Roubles(step.TotalRoubles))
        : string.Empty;
    public string LimitsLabel
    {
        get
        {
            if (_plan is null)
            {
                return string.Empty;
            }

            var limits = new List<string>(3);
            if (_plan.CycleSkipped)
            {
                limits.Add(V2ShellText.Get("V2.Shell.Intel.Chain.Cycle"));
            }

            if (_plan.DepthLimitReached)
            {
                limits.Add(V2ShellText.Get("V2.Shell.Intel.Chain.Depth"));
            }

            if (_plan.SearchLimitReached)
            {
                limits.Add(V2ShellText.Get("V2.Shell.Intel.Chain.SearchLimit"));
            }

            return string.Join(" · ", limits);
        }
    }
    public bool HasLimits => LimitsLabel.Length > 0;
    public string UpdatedLabel => _plan?.OldestPriceUtc is { } updated
        ? V2ShellText.Format("V2.Shell.Intel.Chain.Updated", CultureInfo.CurrentCulture, LocalTime.Moment(updated))
        : string.Empty;
    public bool HasUpdated => UpdatedLabel.Length > 0;
    public IReadOnlyList<AcquisitionChainStepViewModel> Steps => _plan?.Cheapest is { } step
        ? Flatten(step, 0).ToArray()
        : [];

    public Task ShowAsync(string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (string.Equals(_selectedItemId, itemId, StringComparison.Ordinal) && (_loading || _plan is not null))
        {
            return LoadTask;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        _selectedItemId = itemId;
        _plan = null;
        _loading = true;
        RaiseChanged();
        LoadTask = LoadAsync(itemId, _loadCts.Token);
        return LoadTask;
    }

    public void Clear()
    {
        _loadCts?.Cancel();
        _selectedItemId = string.Empty;
        _plan = null;
        _loading = false;
        RaiseChanged();
    }

    private async Task LoadAsync(string itemId, CancellationToken cancellationToken)
    {
        try
        {
            _plan = await OffInterfaceThread.Run(() => planner.PlanAsync(itemId, 1, cancellationToken)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            _plan = new(itemId, itemId, 1, null, false, false, false, null);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _loading = false;
                RaiseChanged();
            }
        }
    }

    private static IEnumerable<AcquisitionChainStepViewModel> Flatten(AcquisitionChainStep step, int depth)
    {
        var verb = step.Method switch
        {
            AcquisitionChainMethod.Craft => V2ShellText.Get("V2.Shell.Intel.Chain.Craft"),
            AcquisitionChainMethod.Barter => V2ShellText.Get("V2.Shell.Intel.Chain.Barter"),
            _ => V2ShellText.Get("V2.Shell.Intel.Chain.Buy"),
        };
        var source = step.Duration is { } duration
            ? $"{step.SourceName} · {Duration(duration)}"
            : step.SourceName;
        var overhead = new List<string>(3);
        if (step.FuelCostRoubles > 0)
        {
            overhead.Add(V2ShellText.Format("V2.Shell.Intel.Chain.Fuel", CultureInfo.CurrentCulture, Roubles(step.FuelCostRoubles)));
        }

        if (step.StationTimeCostRoubles > 0)
        {
            overhead.Add(V2ShellText.Format("V2.Shell.Intel.Chain.Time", CultureInfo.CurrentCulture, Roubles(step.StationTimeCostRoubles)));
        }

        if (step.InputOpportunityCostRoubles > 0)
        {
            overhead.Add(V2ShellText.Format("V2.Shell.Intel.Chain.Opportunity", CultureInfo.CurrentCulture, Roubles(step.InputOpportunityCostRoubles)));
        }

        yield return new(
            $"{verb} {step.Quantity.ToString(CultureInfo.CurrentCulture)}× {step.ItemName}",
            source,
            Roubles(step.TotalRoubles),
            string.Join(" · ", overhead),
            depth * 22d);
        foreach (var child in step.Inputs)
        {
            foreach (var row in Flatten(child, depth + 1))
            {
                yield return row;
            }
        }
    }

    private void RaiseChanged()
    {
        foreach (var property in new[]
        {
            nameof(HasSelection), nameof(IsLoading), nameof(HasRoute), nameof(ShowsEmpty), nameof(TotalLabel),
            nameof(LimitsLabel), nameof(HasLimits), nameof(UpdatedLabel), nameof(HasUpdated), nameof(Steps),
        })
        {
            OnPropertyChanged(property);
        }
    }

    private static string Roubles(long value) =>
        V2ShellText.Format("V2.Shell.Intel.Roubles", CultureInfo.CurrentCulture, value);

    private static string Duration(TimeSpan duration) => duration.TotalHours >= 1
        ? string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalHours}h {duration.Minutes}m")
        : string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes))}m");
}
