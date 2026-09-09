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
