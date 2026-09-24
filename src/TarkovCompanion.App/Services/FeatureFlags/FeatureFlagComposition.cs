using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services.Updates;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.ReleaseExperience;
using TarkovCompanion.Core.Features;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.App.Services.FeatureFlags;

/// <summary>
/// [#314] The flags this run uses, for a view model that is not built by the container with them.
/// </summary>
/// <remarks>
/// Set once by <see cref="FeatureFlagComposition.Add"/>, before any view model exists, the way
/// <c>UiText.Use</c> sets the language. Until then (a test that builds one view model by hand) it
/// answers the rough ring's defaults, which is what a player with no override file sees.
/// </remarks>
public static class AppFeatureFlags
{
    private static IFeatureFlags _current = new RingDefaults(ReleaseRing.Rough);

    public static IFeatureFlags Current
    {
        get => Volatile.Read(ref _current);
        internal set => Volatile.Write(ref _current, value ?? throw new ArgumentNullException(nameof(value)));
    }

    private sealed class RingDefaults(ReleaseRing ring) : IFeatureFlags
    {
        public ReleaseRing Ring => ring;

        public bool IsOn(FeatureFlagDefinition flag) => flag.DefaultFor(ring);
    }
}

/// <summary>Which ring this run is on (#314): read from how it was started, never from the network.</summary>
public static class ReleaseRingDetector
{
    /// <summary>dev, rough or stable: a developer's way to see another ring's defaults.</summary>
    public const string RingOverrideVariable = "TARKOV_RELEASE_RING";

    public static ReleaseRing Detect(ILogger? logger = null) => Detect(
        Environment.GetEnvironmentVariable(RingOverrideVariable),
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UpdateChannel.FeedOverrideVariable)),
        IsInstalled(AppContext.BaseDirectory),
        logger);

    /// <summary>
    /// The variable if it names a ring; else a custom feed or a build run from a folder is Dev;
    /// else an installed build follows the rough feed, which is the only feed there is until #280.
    /// </summary>
    public static ReleaseRing Detect(string? configured, bool customFeed, bool installed, ILogger? logger = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (ReleaseRings.TryParse(configured, out var named))
            {
                return named;
            }

            logger?.LogWarning("{Variable}={Value} is not a ring (dev, rough, stable); ignored", RingOverrideVariable, configured);
        }

        return customFeed || !installed ? ReleaseRing.Dev : ReleaseRing.Rough;
    }

    /// <summary>
    /// Velopack installs the application in a <c>current</c> folder beside its Update.exe and
    /// leaves an <c>sq.version</c> manifest in it. A build unzipped or run from bin has neither.
    /// </summary>
    private static bool IsInstalled(string baseDirectory)
    {
        try
        {
            var folder = new DirectoryInfo(baseDirectory);
            return File.Exists(Path.Combine(folder.FullName, "sq.version"))
                && folder.Parent is { } parent
                && File.Exists(Path.Combine(parent.FullName, "Update.exe"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>[#314] Resolves the flags as the container is built, and registers them and their Setup panel.</summary>
public static class FeatureFlagComposition
{
    public static void Add(IServiceCollection services, AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);
        // The container's logger does not exist yet; this is the same file logger it will use.
        var logger = new FileLoggerProvider().CreateLogger("FeatureFlags");
        var flags = new FeatureFlagService(
            ReleaseRingDetector.Detect(logger),
            new JsonFileFeatureFlagOverrideStore(Path.Combine(paths.Config, JsonFileFeatureFlagOverrideStore.FileName)),
            logger);
        AppFeatureFlags.Current = flags;
        services.AddSingleton(flags);
        services.AddSingleton<IFeatureFlags>(flags);
        services.AddSingleton(provider => new SetupFeatureFlagsViewModel(provider.GetRequiredService<FeatureFlagService>()));
    }
}
