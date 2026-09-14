using System.Text.Json.Serialization;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>The paired-device protocol version, intentionally separate from group protocol v1.</summary>
public readonly record struct CompanionProtocolVersion
{
    public const int MaxMajor = 99;
    public const int MaxMinor = 999;

    [JsonConstructor]
    public CompanionProtocolVersion(int major, int minor)
    {
        if (major is < 1 or > MaxMajor)
        {
            throw new ArgumentOutOfRangeException(nameof(major));
        }

        if (minor is < 0 or > MaxMinor)
        {
            throw new ArgumentOutOfRangeException(nameof(minor));
        }

        Major = major;
        Minor = minor;
    }

    public int Major { get; }

    public int Minor { get; }

    public static CompanionProtocolVersion Current { get; } = new(2, 0);

    [JsonIgnore]
    public bool IsDefined => Major is >= 1 and <= MaxMajor && Minor is >= 0 and <= MaxMinor;

    public bool CanRead(CompanionProtocolVersion message) =>
        IsDefined && message.IsDefined && Major == message.Major && message.Minor <= Minor;

    public override string ToString() => $"{Major}.{Minor}";
}

public sealed record ProtocolVersionRange
{
    public ProtocolVersionRange(CompanionProtocolVersion minimum, CompanionProtocolVersion maximum)
    {
        if (!minimum.IsDefined || !maximum.IsDefined ||
            minimum.Major != maximum.Major || minimum.Minor > maximum.Minor)
        {
            throw new ArgumentException("A compatibility window is one ordered major-version range.");
        }

        Minimum = minimum;
        Maximum = maximum;
    }

    public CompanionProtocolVersion Minimum { get; }

    public CompanionProtocolVersion Maximum { get; }

    public static ProtocolVersionRange Current { get; } = new(
        CompanionProtocolVersion.Current,
        CompanionProtocolVersion.Current);
}

public enum CompatibilityDisposition
{
    Compatible = 1,
    UpgradeClient,
    UpgradeDesktop,
    NoSharedMajor,
}

public enum CompatibilityRecoveryAction
{
    UpdateTablet = 1,
    UpdateDesktop,
    RePairAfterUpdate,
}

public sealed record ProtocolDeprecationNotice(
    CompanionProtocolVersion DeprecatedVersion,
    DateTimeOffset SunsetUtc,
    CompanionProtocolVersion MinimumReplacement,
    CompatibilityRecoveryAction RecoveryAction,
    string Explanation)
{
    public DateTimeOffset SunsetUtc { get; } = ProtocolGuard.Utc(SunsetUtc, nameof(SunsetUtc));

    public CompatibilityRecoveryAction RecoveryAction { get; } =
        ProtocolGuard.Defined(RecoveryAction, nameof(RecoveryAction));

    public string Explanation { get; } = ProtocolGuard.Required(Explanation, nameof(Explanation));
}

public sealed record ClientHello(
    ProtocolVersionRange SupportedVersions,
    string ClientInstanceId,
    IReadOnlyList<string> OptionalFeatures)
{
    public ProtocolVersionRange SupportedVersions { get; } =
        ProtocolGuard.NotNull(SupportedVersions, nameof(SupportedVersions));

    public string ClientInstanceId { get; } = ProtocolGuard.Required(
        ClientInstanceId,
        nameof(ClientInstanceId),
        ProtocolBounds.MaxShortStringBytes);

    public IReadOnlyList<string> OptionalFeatures { get; } = ProtocolGuard.List(
        ProtocolGuard.List(OptionalFeatures, nameof(OptionalFeatures), 64)
            .Select(feature => ProtocolGuard.Required(feature, nameof(OptionalFeatures), ProtocolBounds.MaxShortStringBytes)),
        nameof(OptionalFeatures),
        64);
}

public sealed record ServerHello
{
    public ServerHello(
        CompatibilityDisposition disposition,
        CompanionProtocolVersion? negotiatedVersion,
        ProtocolVersionRange desktopVersions,
        ProtocolDeprecationNotice? deprecation,
        CompatibilityRecoveryAction? recoveryAction)
    {
        Disposition = ProtocolGuard.Defined(disposition, nameof(disposition));
        DesktopVersions = ProtocolGuard.NotNull(desktopVersions, nameof(desktopVersions));
        Deprecation = deprecation;
        RecoveryAction = recoveryAction is { } action
            ? ProtocolGuard.Defined(action, nameof(recoveryAction))
            : null;

        if ((disposition == CompatibilityDisposition.Compatible) != (negotiatedVersion is not null))
        {
            throw new ArgumentException("Only a compatible handshake carries a negotiated version.", nameof(negotiatedVersion));
        }

        if (disposition != CompatibilityDisposition.Compatible && recoveryAction is null)
        {
            throw new ArgumentException("An incompatible handshake must give a recovery action.", nameof(recoveryAction));
        }

        NegotiatedVersion = negotiatedVersion;
    }

    public CompatibilityDisposition Disposition { get; }

    public CompanionProtocolVersion? NegotiatedVersion { get; }

    public ProtocolVersionRange DesktopVersions { get; }

    public ProtocolDeprecationNotice? Deprecation { get; }

    public CompatibilityRecoveryAction? RecoveryAction { get; }
}

public static class ProtocolCompatibility
{
    public static ServerHello Negotiate(
        ClientHello client,
        ProtocolVersionRange desktop,
        ProtocolDeprecationNotice? deprecation = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(desktop);

        if (client.SupportedVersions.Minimum.Major != desktop.Minimum.Major)
        {
            var clientBehind = client.SupportedVersions.Maximum.Major < desktop.Minimum.Major;
            return new ServerHello(
                CompatibilityDisposition.NoSharedMajor,
                null,
                desktop,
                deprecation,
                clientBehind
                    ? CompatibilityRecoveryAction.UpdateTablet
                    : CompatibilityRecoveryAction.UpdateDesktop);
        }

        var lower = Math.Max(client.SupportedVersions.Minimum.Minor, desktop.Minimum.Minor);
        var upper = Math.Min(client.SupportedVersions.Maximum.Minor, desktop.Maximum.Minor);
        if (lower > upper)
        {
            var clientBehind = client.SupportedVersions.Maximum.Minor < desktop.Minimum.Minor;
            return new ServerHello(
                clientBehind ? CompatibilityDisposition.UpgradeClient : CompatibilityDisposition.UpgradeDesktop,
                null,
                desktop,
                deprecation,
                clientBehind
                    ? CompatibilityRecoveryAction.UpdateTablet
                    : CompatibilityRecoveryAction.UpdateDesktop);
        }

        return new ServerHello(
            CompatibilityDisposition.Compatible,
            new CompanionProtocolVersion(desktop.Maximum.Major, upper),
            desktop,
            deprecation,
            null);
    }
}
