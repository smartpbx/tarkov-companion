using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Platform.Windows.Discovery;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.Profiles;

public sealed class EftInstallDiscoveryTests
{
    [Fact]
    public async Task Missing_install_is_an_actionable_state_and_a_confirming_probe_does_not_churn_revision()
    {
        var clock = new ProfileClock(Now);
        var probe = new MutablePathProbe(
            new(["install"], ["logs"], ["screenshots"]),
            ["logs", "screenshots"]);
        var locator = new WindowsEftPathLocator(probe, timeProvider: clock);
        var published = new List<EftInstallDiscoverySnapshot>();
        locator.StateChanged += change => published.Add(change.Snapshot);

        var missing = await locator.RefreshAsync(CancellationToken.None);
        clock.Set(Now.AddMinutes(1));
        var confirmed = await locator.RefreshAsync(CancellationToken.None);

        Assert.Equal(EftInstallDiscoveryStatus.Missing, missing.Status);
        Assert.False(missing.IsContextValid);
        Assert.Equal("eft-install-missing", missing.Code);
        Assert.Contains("Install it or choose valid folders", missing.Detail, StringComparison.Ordinal);
        Assert.Null(missing.Paths.InstallRoot);
        Assert.Equal("logs", missing.Paths.LogRoot);
        Assert.Equal("screenshots", missing.Paths.ScreenshotRoot);
        Assert.Equal(missing.Revision, confirmed.Revision);
        Assert.Equal(Now.AddMinutes(1), confirmed.CheckedUtc);
        Assert.Single(published);
    }

    [Fact]
    public async Task Install_disappearance_invalidates_the_context_until_the_path_recovers()
    {
        var probe = new MutablePathProbe(
            new(["install"], ["logs"], ["screenshots"]),
            ["install", "logs", "screenshots"]);
        var locator = new WindowsEftPathLocator(probe, timeProvider: new ProfileClock(Now));
        var states = new List<EftInstallDiscoveryStatus>();
        locator.StateChanged += change => states.Add(change.Snapshot.Status);

        var ready = await locator.RefreshAsync(CancellationToken.None);
        probe.Existing.Remove("install");
        var invalidated = await locator.RefreshAsync(CancellationToken.None);
        var stillInvalidated = await locator.RefreshAsync(CancellationToken.None);
        probe.Existing.Add("install");
        var recovered = await locator.RefreshAsync(CancellationToken.None);

        Assert.True(ready.IsContextValid);
        Assert.Equal("install", ready.Paths.InstallRoot);
        Assert.Equal(EftInstallDiscoveryStatus.Invalidated, invalidated.Status);
        Assert.Equal("eft-install-disappeared", invalidated.Code);
        Assert.Contains("Reconnect its drive", invalidated.Detail, StringComparison.Ordinal);
        Assert.Equal(invalidated.Revision, stillInvalidated.Revision);
        Assert.Equal(EftInstallDiscoveryStatus.Ready, recovered.Status);
        Assert.Equal("eft-discovery-recovered", recovered.Code);
        Assert.True(recovered.IsContextValid);
        Assert.Equal(ready.Revision + 2, recovered.Revision);
        Assert.Equal(
            [
                EftInstallDiscoveryStatus.Ready,
                EftInstallDiscoveryStatus.Invalidated,
                EftInstallDiscoveryStatus.Ready,
            ],
            states);
    }

    [Fact]
    public async Task Invalid_discovery_configuration_is_visible_while_safe_automatic_paths_remain_usable()
    {
        var probe = new MutablePathProbe(
            new(["install"], ["logs"], ["screenshots"]),
            ["install", "logs", "screenshots"]);
        var locator = new WindowsEftPathLocator(
            probe,
            new ThrowingOverrideStore(new InvalidDataException("invalid discovery json")),
            new ProfileClock(Now));

        var state = await locator.RefreshAsync(CancellationToken.None);
        var paths = await locator.FindAsync(CancellationToken.None);

        Assert.Equal(EftInstallDiscoveryStatus.InvalidConfiguration, state.Status);
        Assert.False(state.IsContextValid);
        Assert.Equal("eft-discovery-configuration-invalid", state.Code);
        Assert.Contains("invalid discovery json", state.Detail, StringComparison.Ordinal);
        Assert.Contains("Re-save them in Settings", state.Detail, StringComparison.Ordinal);
        Assert.Equal("install", state.Paths.InstallRoot);
        Assert.Equal(state.Paths, paths);
    }

    [Fact]
    public async Task Probe_failure_is_bounded_and_recovers_without_becoming_an_empty_success()
    {
        var probe = new MutablePathProbe(
            new(["install"], ["logs"], ["screenshots"]),
            ["install", "logs", "screenshots"])
        {
            Failure = new IOException(new string('x', 10_000)),
        };
        var locator = new WindowsEftPathLocator(probe, timeProvider: new ProfileClock(Now));

        var unavailable = await locator.RefreshAsync(CancellationToken.None);
        var findError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            locator.FindAsync(CancellationToken.None));
        probe.Failure = null;
        var recovered = await locator.RefreshAsync(CancellationToken.None);

        Assert.Equal(EftInstallDiscoveryStatus.Unavailable, unavailable.Status);
        Assert.False(unavailable.IsContextValid);
        Assert.Equal("eft-discovery-unavailable", unavailable.Code);
        Assert.InRange(unavailable.Detail.Length, 1, 2048);
        Assert.Null(unavailable.Paths.InstallRoot);
        Assert.Contains("eft-discovery-unavailable", findError.Message, StringComparison.Ordinal);
        Assert.Equal(EftInstallDiscoveryStatus.Ready, recovered.Status);
        Assert.Equal("eft-discovery-recovered", recovered.Code);
        Assert.Equal(unavailable.Revision + 1, recovered.Revision);
    }

    [Fact]
    public async Task Discovery_subscriber_failure_is_isolated_after_state_commit()
    {
        var probe = new MutablePathProbe(new(["install"], [], []), ["install"]);
        var logger = new CapturingLogger<WindowsEftPathLocator>();
        var locator = new WindowsEftPathLocator(
            probe,
            timeProvider: new ProfileClock(Now),
            logger: logger);
        var delivered = 0;
        locator.StateChanged += _ => throw new InvalidOperationException("subscriber failed");
        locator.StateChanged += _ => delivered++;

        var state = await locator.RefreshAsync(CancellationToken.None);

        Assert.Equal(EftInstallDiscoveryStatus.Ready, state.Status);
        Assert.Same(state, locator.Current);
        Assert.Equal(1, delivered);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
        Assert.IsType<InvalidOperationException>(warning.Error);
    }

    private sealed class MutablePathProbe(
        EftPathCandidates candidates,
        IEnumerable<string> existing) : IEftPathProbe
    {
        public HashSet<string> Existing { get; } = new(existing, StringComparer.OrdinalIgnoreCase);

        public Exception? Failure { get; set; }

        public EftPathCandidates GetCandidates()
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            return candidates;
        }

        public bool DirectoryExists(string path) => Existing.Contains(path);
    }

    private sealed class ThrowingOverrideStore(Exception exception) : IEftPathOverrideStore
    {
        public Task<EftPathOverrides> GetAsync(CancellationToken cancellationToken) =>
            Task.FromException<EftPathOverrides>(exception);

        public Task SaveAsync(EftPathOverrides overrides, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
