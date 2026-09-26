using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.App.ViewModels.V2.Now;

/// <summary>
/// [#712 2-4] The player's own pace and exits, read once per raid: YOU's exit pick and every walk
/// minute on the panel use them (NowPanelState.ChoosePersonalExit, NowPanelState.Walk).
/// </summary>
public sealed partial class NowPanelViewModel
{
    private NowPersonal _personal = NowPersonal.None;
    private string? _personalKey;
    private int _personalRead;

    /// <summary>
    /// Reads the player's pace and their exit use on a map (and side, when known). Null leaves the
    /// panel on the fixed pace and the plain nearest exit.
    /// </summary>
    public Func<string, SituationSide, CancellationToken, Task<NowPersonal>>? LoadPersonal { get; set; }

    public NowPersonal Personal => _personal;

    /// <summary>The exits the panel picks from, as the map last handed them.</summary>
    public IReadOnlyList<NowExit> Exits => _exits;

    /// <summary>Reads the player's history again for this raid (a raid recorded meanwhile, the render preview).</summary>
    public void ReloadPersonal()
    {
        _personalKey = null;
        Refresh();
    }

    /// <summary>Hands the panel the player's history directly (tests, and the render preview).</summary>
    public void SetPersonal(NowPersonal? personal)
    {
        _personal = personal ?? NowPersonal.None;
        Refresh();
    }

    /// <summary>Starts a read when a new raid, map or side comes in; the tick calls this, so it is cheap otherwise.</summary>
    private void RequestPersonal()
    {
        if (LoadPersonal is not { } load ||
            _situation.Phase.Value != SituationPhase.InRaid ||
            _situation.Map?.Value is not { Length: > 0 } mapId)
        {
            return;
        }

        var side = _situation.Side?.Value ?? SituationSide.Unknown;
        var key = $"{mapId}|{side}|{_situation.Clock?.StartedUtc:O}";
        if (key == _personalKey)
        {
            return;
        }

        _personalKey = key;
        var read = ++_personalRead;
        _ = ReadPersonalAsync(load, mapId, side, read);
    }

    private async Task ReadPersonalAsync(Func<string, SituationSide, CancellationToken, Task<NowPersonal>> load, string mapId, SituationSide side, int read)
    {
        try
        {
            var personal = await load(mapId, side, CancellationToken.None).ConfigureAwait(false);
            _post(() =>
            {
                // A newer raid's read wins over a slow older one.
                if (read == _personalRead && !_disposed)
                {
                    _personal = personal;
                    Refresh();
                }
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The history is a refinement: without it the panel keeps the careful pace and the plain nearest exit.
            System.Diagnostics.Trace.TraceWarning($"Now panel: reading your pace and exits failed: {exception.Message}");
        }
    }
}
