namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Stands in where there is no recycle bin, and tidies nothing.
/// </summary>
/// <remarks>
/// Reporting unavailable rather than falling back to deleting outright. The whole feature
/// rests on the promise that a screenshot can be got back, and a platform that cannot keep
/// that promise should do nothing instead of keeping half of it.
/// </remarks>
public sealed class UnavailableRecycleBin : IRecycleBin
{
    public bool IsAvailable => false;

    public bool Recycle(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return false;
    }
}
