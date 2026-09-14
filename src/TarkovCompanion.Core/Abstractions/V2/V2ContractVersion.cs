namespace TarkovCompanion.Core.Abstractions.V2;

/// <summary>The semantic version of the v2 contract, independent of the application version.</summary>
public readonly record struct V2ContractVersion
{
    public V2ContractVersion(int major, int minor)
    {
        if (major < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(major));
        }

        if (minor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minor));
        }

        Major = major;
        Minor = minor;
    }

    public int Major { get; }

    public int Minor { get; }

    public static V2ContractVersion Current { get; } = new(2, 0);

    public bool CanRead(V2ContractVersion other) => Major == other.Major;

    public override string ToString() => $"{Major}.{Minor}";
}
