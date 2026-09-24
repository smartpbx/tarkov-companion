using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Core.Features;

namespace TarkovCompanion.Application.Services.ReleaseExperience;

/// <summary>The player's own switches, as the override file holds them.</summary>
public interface IFeatureFlagOverrideStore
{
    /// <summary>Every entry in the file. A value that is not true or false reads as null.</summary>
    IReadOnlyDictionary<string, bool?> Read();

    void Save(IReadOnlyDictionary<string, bool> overrides);
}

public enum FeatureFlagSource
{
    RingDefault,
    Override,
}

/// <summary>One flag as Setup shows it.</summary>
/// <param name="IsOn">What the player has chosen (or the ring default), applied or not.</param>
/// <param name="IsOnNow">What this run is actually doing.</param>
public sealed record FeatureFlagState(FeatureFlagDefinition Flag, bool IsOn, bool IsOnNow, FeatureFlagSource Source)
{
    /// <summary>The choice differs from what is running and only a restart applies it.</summary>
    public bool WaitsForRestart => IsOn != IsOnNow;
}

/// <summary>
/// [#314] Ring default, then the player's override: resolved once as the application starts.
/// </summary>
/// <remarks>
/// A flag that <see cref="FeatureFlagDefinition.NeedsRestart"/> keeps answering what it answered at
/// startup, so a feature that read it once and one that reads it on every frame never disagree.
/// Setup shows the choice beside what is running and says a restart applies it.
///
/// Unknown keys in the file are a newer build's flags, or retired ones: ignored, logged once, and
/// written back untouched, so going back a build and forward again does not lose a choice.
/// </remarks>
public sealed class FeatureFlagService : IFeatureFlags
{
    private readonly IFeatureFlagOverrideStore _store;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<FeatureFlagDefinition> _registry;
    private readonly Dictionary<string, FeatureFlagDefinition> _byKey;
    private readonly Dictionary<string, bool> _atStartup = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _overrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _foreign = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public FeatureFlagService(
        ReleaseRing ring,
        IFeatureFlagOverrideStore store,
        ILogger? logger = null,
        IReadOnlyList<FeatureFlagDefinition>? registry = null)
    {
        Ring = ring;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? NullLogger.Instance;
        _registry = registry ?? Flag.All;
        _byKey = _registry.ToDictionary(flag => flag.Key, StringComparer.Ordinal);
        Load();
        foreach (var flag in _registry)
        {
            _atStartup[flag.Key] = Chosen(flag);
        }
    }

    public ReleaseRing Ring { get; }

    public event EventHandler? Changed;

    public bool IsOn(FeatureFlagDefinition flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        lock (_gate)
        {
            if (!_byKey.ContainsKey(flag.Key))
            {
                return flag.DefaultFor(Ring);
            }

            return flag.NeedsRestart ? _atStartup[flag.Key] : Chosen(flag);
        }
    }

    public IReadOnlyList<FeatureFlagState> States
    {
        get
        {
            lock (_gate)
            {
                return _registry.Select(flag => new FeatureFlagState(
                    flag,
                    Chosen(flag),
                    flag.NeedsRestart ? _atStartup[flag.Key] : Chosen(flag),
                    _overrides.ContainsKey(flag.Key) ? FeatureFlagSource.Override : FeatureFlagSource.RingDefault)).ToArray();
            }
        }
    }

    /// <summary>Records the player's choice. Choosing the ring default is the same as Reset.</summary>
    public void Set(FeatureFlagDefinition flag, bool isOn)
    {
        ArgumentNullException.ThrowIfNull(flag);
        lock (_gate)
        {
            Require(flag);
            if (isOn == flag.DefaultFor(Ring))
            {
                _overrides.Remove(flag.Key);
            }
            else
            {
                _overrides[flag.Key] = isOn;
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Back to the ring default: the override is removed from the file.</summary>
    public void Reset(FeatureFlagDefinition flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        lock (_gate)
        {
            Require(flag);
            if (!_overrides.Remove(flag.Key))
            {
                return;
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool Chosen(FeatureFlagDefinition flag) =>
        _overrides.TryGetValue(flag.Key, out var chosen) ? chosen : flag.DefaultFor(Ring);

    private void Require(FeatureFlagDefinition flag)
    {
        if (!_byKey.ContainsKey(flag.Key))
        {
            throw new ArgumentException($"'{flag.Key}' is not a registered flag.", nameof(flag));
        }
    }

    private void Load()
    {
        IReadOnlyDictionary<string, bool?> entries;
        try
        {
            entries = _store.Read();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning(exception, "Feature flag overrides could not be read; using the {Ring} defaults", Ring);
            return;
        }

        foreach (var (key, value) in entries)
        {
            if (!_byKey.ContainsKey(key))
            {
                if (value is { } foreignValue)
                {
                    _foreign[key] = foreignValue;
                }

                if (_reported.Add(key))
                {
                    _logger.LogWarning("Feature flag override {Key} is not a flag this build knows; ignored", key);
                }

                continue;
            }

            if (value is not { } isOn)
            {
                if (_reported.Add(key))
                {
                    _logger.LogWarning("Feature flag override {Key} is not true or false; ignored", key);
                }

                continue;
            }

            _overrides[key] = isOn;
        }
    }

    private void Save()
    {
        var all = new Dictionary<string, bool>(_foreign, StringComparer.Ordinal);
        foreach (var (key, value) in _overrides)
        {
            all[key] = value;
        }

        try
        {
            _store.Save(all);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Feature flag overrides could not be saved");
        }
    }
}
