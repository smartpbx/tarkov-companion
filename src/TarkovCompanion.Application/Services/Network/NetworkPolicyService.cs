using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Core.Network;

namespace TarkovCompanion.Application.Services.Network;

/// <summary>
/// [#292] The one answer to "may this go out?": Setup › Data &amp; Privacy's switches, plus
/// TARKOV_COMPANION_OFFLINE, which holds Local only on.
/// </summary>
/// <remarks>
/// Before this, the offline variable was the only switch and each client read it its own way, once,
/// at composition: TarkovTracker decided at startup, the relay never asked at all. Every client now
/// asks here before it connects, so turning Local only off resumes them without a restart.
///
/// A file that cannot be read keeps the defaults rather than stopping the app, and says so in the log.
/// A save that fails keeps the new choice for this run: the player asked for less traffic, and a disk
/// error is no reason to send more.
/// </remarks>
public sealed class NetworkPolicyService : INetworkPolicy
{
    private readonly INetworkControlsStore _store;
    private readonly Func<bool> _forced;
    private readonly ILogger _logger;
    private NetworkControls _controls;

    public NetworkPolicyService(INetworkControlsStore store, Func<bool>? localOnlyForced = null, ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _forced = localOnlyForced ?? (static () => false);
        _logger = logger ?? NullLogger.Instance;
        try
        {
            _controls = _store.Read();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning(exception, "Network controls unreadable; using the defaults");
            _controls = NetworkControls.Default;
        }
    }

    public NetworkControls Controls => Volatile.Read(ref _controls);

    public bool LocalOnlyForced => _forced();

    public event EventHandler? Changed;

    public NetworkVerdict Check(NetworkService service) => Controls.Check(service, LocalOnlyForced);

    /// <summary>Saves the new controls and tells every client. A no-op when nothing changed.</summary>
    public void Set(NetworkControls controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        if (Interlocked.Exchange(ref _controls, controls) == controls)
        {
            return;
        }

        try
        {
            _store.Save(controls);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not save the network controls; they hold until the app closes");
        }

        _logger.LogInformation(
            "Network controls: local only {LocalOnly}, squad {Squad}, updates {Updates}, reports {Reports}, TarkovTracker {Tracker}",
            controls.LocalOnly,
            controls.SquadSharing,
            controls.UpdateChecks,
            controls.ProblemReports,
            controls.TarkovTracker);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A request the network policy stopped before it left the PC.</summary>
/// <remarks>An <see cref="HttpRequestException"/>, so a client that already survives a dead network survives this.</remarks>
public sealed class NetworkBlockedException(NetworkService? service, NetworkVerdict verdict)
    : HttpRequestException(verdict == NetworkVerdict.LocalOnly
        ? "Local only is on, so nothing was sent."
        : $"{service} is switched off in Data & Privacy, so nothing was sent.")
{
    public NetworkService? Service { get; } = service;

    public NetworkVerdict Verdict { get; } = verdict;
}

/// <summary>
/// The backstop under every <see cref="HttpClient"/> this app makes: it asks the policy before each
/// request and refuses without opening a connection.
/// </summary>
/// <remarks>
/// With no service named it stops traffic only under Local only; that is the shared client, used by
/// the catalog, the maps and the relay alike. The per-service switches are asked by each client
/// itself, at the point it would connect, so it can say "off" instead of failing.
/// </remarks>
public sealed class NetworkPolicyHandler : DelegatingHandler
{
    private readonly INetworkPolicy _policy;
    private readonly NetworkService? _service;

    public NetworkPolicyHandler(INetworkPolicy policy, NetworkService? service, HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _service = service;
    }

    /// <summary>The inner handler wrapped when there is a policy; unchanged when there is not (tests, tools).</summary>
    public static HttpMessageHandler Wrap(INetworkPolicy? policy, NetworkService? service, HttpMessageHandler? inner = null) =>
        policy is null ? inner ?? new HttpClientHandler() : new NetworkPolicyHandler(policy, service, inner);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var verdict = _service is { } service
            ? _policy.Check(service)
            : _policy.Check(NetworkService.GameData);
        return verdict == NetworkVerdict.Allowed
            ? base.SendAsync(request, cancellationToken)
            : Task.FromException<HttpResponseMessage>(new NetworkBlockedException(_service, verdict));
    }
}
