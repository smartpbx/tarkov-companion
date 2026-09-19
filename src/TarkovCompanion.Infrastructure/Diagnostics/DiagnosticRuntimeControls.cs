using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TarkovCompanion.Infrastructure.Diagnostics;

/// <summary>Explicit, local controls. Telemetry stays off unless a later caller confirms consent.</summary>
public sealed record DiagnosticRuntimeControls(
    DiagnosticLogVerbosity Verbosity,
    bool InternalTelemetryRequested)
{
    public static DiagnosticRuntimeControls LocalOnly { get; } = new(DiagnosticLogVerbosity.Information, false);

    /// <summary>The level a logger factory should be given for <see cref="Verbosity"/>.</summary>
    public LogLevel MinimumLogLevel => Verbosity switch
    {
        DiagnosticLogVerbosity.Trace => LogLevel.Trace,
        DiagnosticLogVerbosity.Debug => LogLevel.Debug,
        DiagnosticLogVerbosity.Warning => LogLevel.Warning,
        DiagnosticLogVerbosity.Error => LogLevel.Error,
        _ => LogLevel.Information,
    };

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

        // This is a request, not consent or an enabled collector. Future composition must also
        // require the reviewed in-app consent state before it creates an exporter.
        var telemetryRequested = string.Equals(
            readEnvironment("TARKOV_COMPANION_INTERNAL_TELEMETRY"),
            "enabled",
            StringComparison.OrdinalIgnoreCase);
        return new(verbosity, telemetryRequested);
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
/// Validates diagnostic-channel tokens while retaining only fixed-size cryptographic digests.
/// </summary>
/// <remarks>
/// <c>TARKOV_COMPANION_DIAGNOSTIC_TOKEN</c> may contain a current token and expiring previous
/// tokens: <c>current,previous@2026-09-16T00:00:00Z</c>. This permits a short overlap during
/// rotation. The App channel does not consume this helper yet; its eventual composition is
/// outside this issue's paths.
/// </remarks>
public sealed class DiagnosticTokenSet
{
    private const int MinimumTokenLength = 32;
    private const int MaximumTokenLength = 256;
    private const int MaximumEntries = 3;
    private const int MaximumExpiryLength = 28;
    private const int MaximumConfigurationLength =
        (MaximumEntries * (MaximumTokenLength + 1 + MaximumExpiryLength)) + (MaximumEntries - 1);

    public static readonly TimeSpan MaximumPreviousTokenOverlap = TimeSpan.FromHours(24);

    private readonly TokenEntry[] _entries;

    private DiagnosticTokenSet(TokenEntry[] entries) => _entries = entries;

    public static bool TryParse(string? configuredTokens, DateTimeOffset nowUtc, out DiagnosticTokenSet? tokenSet)
    {
        tokenSet = null;
        if (configuredTokens is null || configuredTokens.Length == 0 ||
            configuredTokens.Length > MaximumConfigurationLength || nowUtc.Offset != TimeSpan.Zero)
        {
            return false;
        }

        var entries = new List<TokenEntry>();
        var remaining = configuredTokens.AsSpan();
        while (true)
        {
            if (entries.Count == MaximumEntries)
            {
                ClearDigests(entries);
                return false;
            }

            var comma = remaining.IndexOf(',');
            var part = (comma < 0 ? remaining : remaining[..comma]).Trim();
            if (part.IsEmpty)
            {
                ClearDigests(entries);
                return false;
            }

            var separator = part.LastIndexOf('@');
            var token = separator < 0 ? part : part[..separator];
            DateTimeOffset? expiresUtc = null;
            if (separator >= 0)
            {
                var expiryText = part[(separator + 1)..];
                if (!DateTimeOffset.TryParseExact(
                        expiryText,
                        ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed) ||
                    parsed <= nowUtc ||
                    parsed - nowUtc > MaximumPreviousTokenOverlap)
                {
                    ClearDigests(entries);
                    return false;
                }

                expiresUtc = parsed;
            }

            if (!IsTokenShapeSafe(token))
            {
                ClearDigests(entries);
                return false;
            }

            var digest = HashToken(token);
            if (entries.Any(entry => CryptographicOperations.FixedTimeEquals(entry.Digest, digest)))
            {
                CryptographicOperations.ZeroMemory(digest);
                ClearDigests(entries);
                return false;
            }

            entries.Add(new(digest, expiresUtc));
            if (comma < 0)
            {
                break;
            }

            remaining = remaining[(comma + 1)..];
        }

        if (entries[0].ExpiresUtc is not null || entries.Skip(1).Any(entry => entry.ExpiresUtc is null))
        {
            ClearDigests(entries);
            return false;
        }

        tokenSet = new(entries.ToArray());
        return true;
    }

    public bool IsValid(string? presentedToken, DateTimeOffset nowUtc)
    {
        if (presentedToken is null || nowUtc.Offset != TimeSpan.Zero)
        {
            return false;
        }

        var token = presentedToken.AsSpan();
        if (!IsTokenShapeSafe(token))
        {
            return false;
        }

        var presentedDigest = HashToken(token);
        try
        {
            // Evaluate every configured digest so a match does not disclose its rotation slot.
            var valid = false;
            foreach (var entry in _entries)
            {
                var current = entry.ExpiresUtc is null || nowUtc <= entry.ExpiresUtc.Value;
                valid |= current & CryptographicOperations.FixedTimeEquals(entry.Digest, presentedDigest);
            }

            return valid;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(presentedDigest);
        }
    }

    public override string ToString() => nameof(DiagnosticTokenSet);

    private static bool IsTokenShapeSafe(ReadOnlySpan<char> token)
    {
        if (token.Length is < MinimumTokenLength or > MaximumTokenLength)
        {
            return false;
        }

        foreach (var character in token)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] HashToken(ReadOnlySpan<char> token)
    {
        // Shape validation makes the UTF-8 representation one byte per character. Rent one
        // bounded buffer, clear it before returning it, and retain only the fixed-size digest.
        var bytes = ArrayPool<byte>.Shared.Rent(MaximumTokenLength);
        try
        {
            var written = Encoding.UTF8.GetBytes(token, bytes);
            return SHA256.HashData(bytes.AsSpan(0, written));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes.AsSpan(0, MaximumTokenLength));
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }

    private static void ClearDigests(IEnumerable<TokenEntry> entries)
    {
        foreach (var entry in entries)
        {
            CryptographicOperations.ZeroMemory(entry.Digest);
        }
    }

    private sealed class TokenEntry(byte[] digest, DateTimeOffset? expiresUtc)
    {
        public byte[] Digest { get; } = digest;

        public DateTimeOffset? ExpiresUtc { get; } = expiresUtc;
    }
}
