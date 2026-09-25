using TarkovCompanion.Application.Services.Network;
using TarkovCompanion.Core.Network;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.App.Services.Network;

/// <summary>
/// [#292] The network policy this run uses, for the status lines that are not built by the container.
/// </summary>
/// <remarks>
/// Set once by <see cref="NetworkPolicyComposition.Create"/>, the way <c>AppFeatureFlags</c> is. Until
/// then (a test that builds one view model by hand) everything is allowed, which is the default.
/// </remarks>
public static class AppNetworkPolicy
{
    private static INetworkPolicy _current = new NetworkPolicyService(new DefaultsOnly());

    public static INetworkPolicy Current
    {
        get => Volatile.Read(ref _current);
        internal set => Volatile.Write(ref _current, value ?? throw new ArgumentNullException(nameof(value)));
    }

    private sealed class DefaultsOnly : INetworkControlsStore
    {
        public NetworkControls Read() => NetworkControls.Default;

        public void Save(NetworkControls controls)
        {
        }
    }
}

public static class NetworkPolicyComposition
{
    /// <summary>
    /// Config/network.json, with <paramref name="localOnlyForced"/> (TARKOV_COMPANION_OFFLINE, or a
    /// test's Offline setting) holding Local only on.
    /// </summary>
    public static NetworkPolicyService Create(AppDataPaths paths, Func<bool> localOnlyForced)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var policy = new NetworkPolicyService(
            new JsonFileNetworkControlsStore(Path.Combine(paths.Config, JsonFileNetworkControlsStore.FileName)),
            localOnlyForced,
            new FileLoggerProvider().CreateLogger("Network"));
        AppNetworkPolicy.Current = policy;
        return policy;
    }
}
