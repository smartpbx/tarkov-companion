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
/// rotation; a token with no expiry is the current token. The App channel does not consume this
/// helper yet; its eventual composition is outside this issue's paths.
/// </remarks>
public sealed class DiagnosticTokenSet
{
    private const int MinimumTokenLength = 32;
    private const int MaximumTokenLength = 256;
    public static readonly TimeSpan MaximumPreviousTokenOverlap = TimeSpan.FromHours(24);
    private readonly TokenEntry[] _entries;

    private DiagnosticTokenSet(TokenEntry[] entries) => _entries = entries;

    public static bool TryParse(string? configuredTokens, DateTimeOffset nowUtc, out DiagnosticTokenSet? tokenSet)
    {
        tokenSet = null;
        if (string.IsNullOrWhiteSpace(configuredTokens) || nowUtc.Offset != TimeSpan.Zero)
        {
            return false;
        }

        var entries = new List<TokenEntry>();
        foreach (var part in configuredTokens.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.LastIndexOf('@');
            var token = separator < 0 ? part : part[..separator];
            DateTimeOffset? expiresUtc = null;
            if (separator >= 0)
            {
                var expiryText = part[(separator + 1)..];
                if (!DateTimeOffset.TryParseExact(
                        expiryText,
                        ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var parsed) ||
                    !expiryText.EndsWith('Z') ||
                    parsed <= nowUtc ||
                    parsed > nowUtc + MaximumPreviousTokenOverlap)
                {
                    return false;
                }

                expiresUtc = parsed;
            }

            if (!IsTokenShapeSafe(token) || entries.Any(entry => FixedTimeEquals(entry.Token, token)))
            {
                return false;
            }

            entries.Add(new(token, expiresUtc));
        }

        if (entries.Count == 0 || entries.Count > 3 || entries[0].ExpiresUtc is not null ||
            entries.Skip(1).Any(entry => entry.ExpiresUtc is null))
        {
            return false;
        }

        tokenSet = new(entries.ToArray());
        return true;
    }

    public bool IsValid(string? presentedToken, DateTimeOffset nowUtc)
    {
        if (presentedToken is null || !IsTokenShapeSafe(presentedToken) || nowUtc.Offset != TimeSpan.Zero)
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
        token.Length >= MinimumTokenLength && token.Length <= MaximumTokenLength && token.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool FixedTimeEquals(string left, string right) =>
        FixedTimeEqualsPadded(left, right);

    private static bool FixedTimeEqualsPadded(string left, string right)
    {
        // Token characters are ASCII and bounded before this point. Comparing fixed-size buffers
        // avoids returning early merely because a presented token has a different byte length.
        Span<byte> leftBytes = stackalloc byte[MaximumTokenLength];
        Span<byte> rightBytes = stackalloc byte[MaximumTokenLength];
        Encoding.UTF8.GetBytes(left, leftBytes);
        Encoding.UTF8.GetBytes(right, rightBytes);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed record TokenEntry(string Token, DateTimeOffset? ExpiresUtc);
}
