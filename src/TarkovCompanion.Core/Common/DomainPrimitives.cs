namespace TarkovCompanion.Core.Common;

public enum GameMode
{
    Regular,
    Pve,
    PvpSeason,
}

public readonly record struct Confidence
{
    public Confidence(double value)
    {
        if (double.IsNaN(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Confidence must be between 0 and 1.");
        }

        Value = value;
    }

    public double Value { get; }

    public static Confidence Certain => new(1);

    public static Confidence Unknown => new(0);
}

public sealed record DataProvenance(
    string Source,
    DateTimeOffset ObservedUtc,
    DateTimeOffset? SourceUpdatedUtc = null,
    string? Reference = null,
    Confidence? Confidence = null)
{
    public TimeSpan Age(DateTimeOffset nowUtc) => nowUtc - (SourceUpdatedUtc ?? ObservedUtc);
}

public sealed record OperationResult(bool IsSuccess, string? ErrorCode = null, string? Message = null)
{
    public static OperationResult Success() => new(true);

    public static OperationResult Failure(string errorCode, string message) => new(false, errorCode, message);
}
