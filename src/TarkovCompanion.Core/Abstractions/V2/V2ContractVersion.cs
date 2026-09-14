using System.Text.Json.Serialization;

namespace TarkovCompanion.Core.Abstractions.V2;

/// <summary>The semantic version of the v2 contract, independent of the application version.</summary>
/// <remarks>
/// A reader accepts its own major version up to its own minor version. Peers negotiate the lower
/// minor of a shared major and send only that, so a newer minor never reaches an older reader;
/// anything else is <see cref="AcknowledgementDisposition.UnsupportedVersion"/>.
/// </remarks>
public readonly record struct V2ContractVersion
{
    public const int MaxMajor = 99;

    public const int MaxMinor = 999;

    // System.Text.Json builds a struct through its implicit parameterless constructor unless told
    // otherwise, which silently round-tripped ids to Guid.Empty and addresses to (0, 0).
    [JsonConstructor]
    public V2ContractVersion(int major, int minor)
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

    public static V2ContractVersion Current { get; } = new(2, 0);

    public bool IsDefined => Major is >= 1 and <= MaxMajor && Minor is >= 0 and <= MaxMinor;

    public bool CanRead(V2ContractVersion message) =>
        IsDefined && message.IsDefined && Major == message.Major && message.Minor <= Minor;

    /// <summary>The version both peers can read, or null when their majors differ.</summary>
    public static V2ContractVersion? Negotiate(V2ContractVersion local, V2ContractVersion remote) =>
        local.IsDefined && remote.IsDefined && local.Major == remote.Major
            ? new V2ContractVersion(local.Major, Math.Min(local.Minor, remote.Minor))
            : null;

    public override string ToString() => $"{Major}.{Minor}";
}
