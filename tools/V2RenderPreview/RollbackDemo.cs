using TarkovCompanion.App.Services.Updates;
using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#292] Setup › Updates with an installed build that can go back, for a machine that has no
/// installation. <c>--rollback-demo</c> shows the confirmation; <c>--rollback-demo-pinned</c> the pin.
/// </summary>
/// <remarks>
/// That the gateway really finds, fetches and hands over the older build is RollbackTests' job,
/// against the updater library itself; this only puts its answers on the page to be looked at.
/// </remarks>
internal sealed class RollbackDemo(bool pinned) : IUpdateRollback
{
    private static readonly DateTimeOffset Applied = new(2026, 9, 23, 18, 42, 0, TimeSpan.Zero);

    public static void Install(string[] args)
    {
        var confirm = args.Contains("--rollback-demo");
        var pinned = args.Contains("--rollback-demo-pinned");
        if (!confirm && !pinned)
        {
            return;
        }

        SetupRollbackViewModel.RenderDemo = new RollbackDemo(pinned);
        SetupRollbackViewModel.RenderConfirming = confirm;
    }

    public bool IsInstalled => true;

    public UpdatePin? Pin => pinned ? new("2.0.1560", "2.0.1574", Applied) : null;

    public Task<RollbackOffer> FindPreviousAsync(CancellationToken cancellationToken) => Task.FromResult(new RollbackOffer(
        "2.0.1574",
        new("2.0.1560", "TarkovCompanionDesktop-2.0.1560-full.nupkg", new string('A', 64), 102_000_000, RollbackSource.KeptCopy),
        "2.0.1574",
        null));

    public Task<UpdateProgress> DownloadPreviousAsync(RollbackOffer offer, Action<int>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(new UpdateProgress("Not in a render"));

    public void ApplyAndRestart()
    {
    }

    public void ResumeUpdates()
    {
    }

    public IReadOnlyList<UpdateProvenanceRow> Provenance() => UpdateProvenanceText.Rows(
        pinned ? "2.0.1560" : "2.0.1574",
        UpdateChannel.Rough.Name,
        UpdateChannel.Rough.Feed,
        new(pinned ? "2.0.1560" : "2.0.1574", UpdateChannel.Rough.Name, "tarkov.mannerow.net",
            "6121612949CEE2043B350CA50230CF20217C1820F27E49615FF255DDCD918525", Applied, pinned),
        null);
}
