using System.Security.Cryptography;
using System.Text;

namespace TarkovCompanion.Infrastructure.Diagnostics;

/// <summary>Explicit, local controls. Telemetry is disabled unless a caller opts in.</summary>
public sealed record DiagnosticRuntimeControls(
    DiagnosticLogVerbosity Verbosity,
    bool InternalTelemetryEnabled)
{
    public static DiagnosticRuntimeControls LocalOnly { get; } = new(DiagnosticLogVerbosity.Information, false);

    public static DiagnosticRuntimeControls FromEnvironment(Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);

        var verbosity = readEnvironment("TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL")?.Trim().ToUpperInvariant() switch
        {
            "TRACE" => DiagnosticLogVerbosity.Trace,
            "DEBUG" => DiagnosticLogVerbosity.Debug,
            "WARNING" => DiagnosticLogVerbosity.Warning,
            "ERROR" => DiagnosticLogVerbosity.Error,
            _ => DiagnosticLogVerbosity.Information,
        };

        // This setting is intentionally not inferred from a URL, build type, or developer mode.
        // Only an exact affirmative consent enables the internal, self-hosted collector.
        var telemetry = string.Equals(
            readEnvironment("TARKOV_COMPANION_INTERNAL_TELEMETRY"),
            "enabled",
            StringComparison.OrdinalIgnoreCase);
        return new(verbosity, telemetry);
    }
}

public enum DiagnosticLogVerbosity
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
}

/// <summary>
/// Validates the diagnostic-channel token without ever serialising, logging, or retaining it.
/// </summary>
/// <remarks>
/// <c>TARKOV_COMPANION_DIAGNOSTIC_TOKEN</c> may contain a current token and expiring previous
/// tokens: <c>current,previous@2026-09-16T00:00:00Z</c>. This permits a short overlap during
/// rotation; a token with no expiry is the current token. The App channel consumes this helper
/// through the composition owner, because its command transport is outside this issue's paths.
/// </remarks>
public sealed class DiagnosticTokenSet
{
    private const int MinimumTokenLength = 32;
    private readonly TokenEntry[] _entries;

    private DiagnosticTokenSet(TokenEntry[] entries) => _entries = entries;

    public static bool TryParse(string? configuredTokens, out DiagnosticTokenSet? tokenSet)
    {
        tokenSet = null;
        if (string.IsNullOrWhiteSpace(configuredTokens))
        {
            return false;
        }

        var entries = new List<TokenEntry>();
        foreach (var part in configuredTokens.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.LastIndexOf('@');
            var token = separator < 0 ? part : part[..separator];
            DateTimeOffset? expiresUtc = null;
            if (separator >= 0 &&
                (!DateTimeOffset.TryParse(
                    part[(separator + 1)..],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed) || parsed.Offset != TimeSpan.Zero))
            {
                return false;
            }
            else if (separator >= 0)
            {
                expiresUtc = parsed;
            }

            if (!IsTokenShapeSafe(token) || entries.Any(entry => FixedTimeEquals(entry.Token, token)))
            {
                return false;
            }

            entries.Add(new(token, expiresUtc));
        }

        if (entries.Count == 0 || entries.Count > 3 || entries.Skip(1).Any(entry => entry.ExpiresUtc is null))
        {
            return false;
        }

        tokenSet = new(entries.ToArray());
        return true;
    }

    public bool IsValid(string? presentedToken, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrEmpty(presentedToken) || nowUtc.Offset != TimeSpan.Zero)
        {
            return false;
        }

        // Evaluate every configured entry so an early match does not disclose its position in a rotation.
        var valid = false;
        foreach (var entry in _entries)
        {
            var current = entry.ExpiresUtc is null || nowUtc <= entry.ExpiresUtc.Value;
            valid |= current & FixedTimeEquals(entry.Token, presentedToken);
        }

        return valid;
    }

    private static bool IsTokenShapeSafe(string token) =>
        token.Length >= MinimumTokenLength && token.Length <= 256 && token.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private sealed record TokenEntry(string Token, DateTimeOffset? ExpiresUtc);
}
