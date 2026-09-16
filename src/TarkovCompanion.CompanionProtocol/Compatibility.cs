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

public sealed record ProtocolDeprecationNotice
{
    public ProtocolDeprecationNotice(
        CompanionProtocolVersion deprecatedVersion,
        DateTimeOffset sunsetUtc,
        CompanionProtocolVersion minimumReplacement,
        CompatibilityRecoveryAction recoveryAction,
        string explanation)
    {
        DeprecatedVersion = ProtocolGuard.Version(deprecatedVersion, nameof(deprecatedVersion));
        MinimumReplacement = ProtocolGuard.Version(minimumReplacement, nameof(minimumReplacement));
        if (MinimumReplacement.Major != DeprecatedVersion.Major || MinimumReplacement.Minor <= DeprecatedVersion.Minor)
        {
            throw new ArgumentException(
                "A deprecation names a later replacement minor in the same major; a new major is a new protocol.",
                nameof(minimumReplacement));
        }

        SunsetUtc = ProtocolGuard.Utc(sunsetUtc, nameof(sunsetUtc));
        RecoveryAction = ProtocolGuard.Defined(recoveryAction, nameof(recoveryAction));
        Explanation = ProtocolGuard.Required(explanation, nameof(explanation));
    }

    /// <summary>Every minor up to and including this one is deprecated.</summary>
    public CompanionProtocolVersion DeprecatedVersion { get; }

    public DateTimeOffset SunsetUtc { get; }

    public CompanionProtocolVersion MinimumReplacement { get; }

    public CompatibilityRecoveryAction RecoveryAction { get; }

    public string Explanation { get; }
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

        var compatible = disposition == CompatibilityDisposition.Compatible;
        if (compatible != (negotiatedVersion is not null) || compatible == (recoveryAction is not null))
        {
            throw new ArgumentException(
                "A compatible handshake carries only a negotiated version; an incompatible one carries only a recovery action.",
                nameof(negotiatedVersion));
        }

        if (negotiatedVersion is { } negotiated &&
            (!negotiated.IsDefined ||
             negotiated.Major != desktopVersions.Maximum.Major ||
             negotiated.Minor < desktopVersions.Minimum.Minor ||
             negotiated.Minor > desktopVersions.Maximum.Minor))
        {
            throw new ArgumentException("The negotiated version lies inside the desktop compatibility window.", nameof(negotiatedVersion));
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
    /// <summary>
    /// Selects the highest shared minor. After a deprecation sunset every minor below its
    /// replacement is unsupported rather than silently reinterpreted.
    /// </summary>
    public static ServerHello Negotiate(
        ClientHello client,
        ProtocolVersionRange desktop,
        DateTimeOffset nowUtc,
        ProtocolDeprecationNotice? deprecation = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(desktop);
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        if (deprecation is not null && deprecation.DeprecatedVersion.Major != desktop.Maximum.Major)
        {
            throw new ArgumentException("A deprecation applies to the desktop's own major.", nameof(deprecation));
        }

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

        var desktopMinimum = desktop.Minimum.Minor;
        if (deprecation is not null && now >= deprecation.SunsetUtc)
        {
            desktopMinimum = Math.Max(desktopMinimum, deprecation.MinimumReplacement.Minor);
        }

        var lower = Math.Max(client.SupportedVersions.Minimum.Minor, desktopMinimum);
        var upper = Math.Min(client.SupportedVersions.Maximum.Minor, desktop.Maximum.Minor);
        if (lower > upper)
        {
            // A desktop whose whole window is past sunset must update itself; otherwise the side
            // whose newest minor is below the other's usable minimum updates.
            var clientBehind = desktopMinimum <= desktop.Maximum.Minor &&
                               client.SupportedVersions.Maximum.Minor < desktopMinimum;
            return new ServerHello(
                clientBehind ? CompatibilityDisposition.UpgradeClient : CompatibilityDisposition.UpgradeDesktop,
                null,
                desktop,
                deprecation,
                clientBehind
                    ? CompatibilityRecoveryAction.UpdateTablet
                    : CompatibilityRecoveryAction.UpdateDesktop);
        }

        var negotiated = new CompanionProtocolVersion(desktop.Maximum.Major, upper);
        var affected = deprecation is not null && negotiated.Minor <= deprecation.DeprecatedVersion.Minor;
        return new ServerHello(
            CompatibilityDisposition.Compatible,
            negotiated,
            desktop,
            affected ? deprecation : null,
            null);
    }
}
