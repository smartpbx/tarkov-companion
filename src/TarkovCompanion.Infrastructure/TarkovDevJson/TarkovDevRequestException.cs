using System.Net;

namespace TarkovCompanion.Infrastructure.TarkovDevJson;

public sealed class TarkovDevRequestException : Exception
{
    public TarkovDevRequestException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

public sealed class TarkovDevResponseBudgetException(long maximumBytes)
    : Exception($"The catalog response exceeded its {maximumBytes:N0}-byte budget.")
{
    public long MaximumBytes { get; } = maximumBytes;
}

public sealed class TarkovDevOfflineException()
    : Exception("Catalog networking is disabled while the companion is offline.");
