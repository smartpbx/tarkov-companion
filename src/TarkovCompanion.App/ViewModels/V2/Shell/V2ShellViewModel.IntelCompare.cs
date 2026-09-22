using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.Application.Services.Intel;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>#287: Intel's side-by-side comparison, fed from the item open in the detail pane.</summary>
public sealed partial class V2ShellViewModel
{
    private IntelCompareViewModel? _intelCompare;

    public IntelCompareViewModel IntelCompare => _intelCompare ??= CreateIntelCompare();

    /// <summary>The item detail, unless the comparison table is showing in its place.</summary>
    public bool ShowsIntelDetail => IntelHasResult && !IntelCompare.IsOpen;

    private IntelCompareViewModel CreateIntelCompare()
    {
        var compare = new IntelCompareViewModel((itemId, cancellationToken) => _intel.GetComparisonFactsAsync(itemId, cancellationToken));
        compare.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(IntelCompareViewModel.IsOpen))
            {
                OnPropertyChanged(nameof(ShowsIntelDetail));
            }
        };
        return compare;
    }

    private void SyncIntelCompare()
    {
        if (_intelResult is { Kind: not V2IntelKind.Unknown } result)
        {
            IntelCompare.SetCurrent(result.ItemId, result.Name);
        }
        else
        {
            IntelCompare.SetCurrent(null, string.Empty);
        }

        OnPropertyChanged(nameof(ShowsIntelDetail));
    }
}
