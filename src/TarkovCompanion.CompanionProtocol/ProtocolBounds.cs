using System.Collections.ObjectModel;
using System.Text;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>The resource and lifetime limits shared by every paired-device transport.</summary>
/// <remarks>
/// These are protocol limits rather than implementation suggestions. Keeping them here prevents
/// a direct-LAN adapter and the relay adapter from accepting different messages.
/// </remarks>
public static class ProtocolBounds
{
    public const int MaxPayloadBytes = 64 * 1024;
    public const int MaxStringBytes = 1024;
    public const int MaxShortStringBytes = 128;
    public const int MaxCollectionItems = 256;
    public const int MaxJsonDepth = 16;
    public const int MaxDevices = 32;
    public const int MaxMarks = 256;
    public const int MaxRecentCommands = 256;
    public const int MaxOfflineQueueItems = 64;
    public const int MaxDeliveryItemsPerAggregate = 64;
    public const int MaxPairingAttemptsPerWindow = 5;

    public const double MaxWorldCoordinateMagnitude = 1_000_000;

    public static TimeSpan PairingLifetime { get; } = TimeSpan.FromMinutes(5);
    public static TimeSpan MaximumPairingLifetime { get; } = TimeSpan.FromMinutes(10);
    public static TimeSpan PairingRateWindow { get; } = TimeSpan.FromMinutes(5);
    public static TimeSpan CommandLifetime { get; } = TimeSpan.FromMinutes(5);
    public static TimeSpan OfflineQueueLifetime { get; } = TimeSpan.FromMinutes(15);
    public static TimeSpan CaptureIntentLifetime { get; } = TimeSpan.FromMinutes(2);
    public static TimeSpan ControlLeaseLifetime { get; } = TimeSpan.FromMinutes(2);
    public static TimeSpan MaximumControlLeaseLifetime { get; } = TimeSpan.FromMinutes(5);
    public static TimeSpan PingLifetime { get; } = TimeSpan.FromSeconds(45);
    public static TimeSpan MaintenanceScanInterval { get; } = TimeSpan.FromHours(1);
    public static TimeSpan DeviceInactivityExpiry { get; } = TimeSpan.FromHours(2);
}

internal static class ProtocolGuard
{
    public static string Required(string? value, string parameterName, int maxBytes = ProtocolBounds.MaxStringBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        if (Encoding.UTF8.GetByteCount(trimmed) > maxBytes)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"UTF-8 text exceeds {maxBytes} bytes.");
        }

        return trimmed;
    }

    public static string? Optional(string? value, string parameterName, int maxBytes = ProtocolBounds.MaxStringBytes) =>
        string.IsNullOrWhiteSpace(value) ? null : Required(value, parameterName, maxBytes);

    public static ReadOnlyCollection<T> List<T>(
        IEnumerable<T>? values,
        string parameterName,
        int maximum = ProtocolBounds.MaxCollectionItems)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var copy = values.ToArray();
        if (copy.Length > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"A collection may contain at most {maximum} items.");
        }

        if (copy.Any(value => value is null))
        {
            throw new ArgumentException("Protocol collections cannot contain null entries.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A timestamp with an explicit UTC zero offset is required.", parameterName);
        }

        if (value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentException("Protocol timestamps have millisecond precision.", parameterName);
        }

        return value;
    }

    public static DateTimeOffset? UtcOptional(DateTimeOffset? value, string parameterName) =>
        value is null ? null : Utc(value.Value, parameterName);

    public static TEnum Defined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum =>
        Enum.IsDefined(value) && Convert.ToInt64(value) != 0
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, value, $"Undefined {typeof(TEnum).Name} value.");

    public static T NotNull<T>(T? value, string parameterName)
        where T : class => value ?? throw new ArgumentNullException(parameterName);

    public static Guid Id(Guid value, string parameterName) =>
        value != Guid.Empty ? value : throw new ArgumentException("An identifier is required.", parameterName);

    public static long NonNegative(long value, string parameterName) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(parameterName);

    public static long Positive(long value, string parameterName) =>
        value > 0 ? value : throw new ArgumentOutOfRangeException(parameterName);

    public static string Base64Url(
        string? value,
        string parameterName,
        int maximumBytes = 1024,
        int? exactDecodedBytes = null)
    {
        var text = Required(value, parameterName, maximumBytes);
        if (text.Any(character =>
                !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')))
        {
            throw new ArgumentException("Expected unpadded base64url text.", parameterName);
        }

        var remainder = text.Length % 4;
        if (remainder == 1)
        {
            throw new ArgumentException("Expected well-formed unpadded base64url text.", parameterName);
        }

        byte[] decoded;
        try
        {
            var padding = remainder == 0 ? string.Empty : new string('=', 4 - remainder);
            decoded = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + padding);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Expected well-formed unpadded base64url text.", parameterName, exception);
        }

        if (exactDecodedBytes is { } exact && decoded.Length != exact)
        {
            throw new ArgumentException($"Expected exactly {exact} decoded bytes.", parameterName);
        }

        return text;
    }
}
