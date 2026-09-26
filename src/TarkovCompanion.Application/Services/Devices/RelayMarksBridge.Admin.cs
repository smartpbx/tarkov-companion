namespace TarkovCompanion.Application.Services.Devices;

public sealed partial class RelayMarksBridge
{
    /// <summary>
    /// [#920] Sends one request as this desktop's owner session: the relay owner's admin panel
    /// rides on the session this bridge already keeps, so no admin key is typed or stored for it.
    /// Null when there is no relay or no owner session yet.
    /// </summary>
    public async Task<HttpResponseMessage?> SendAsOwnerAsync(
        HttpMethod method,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        HttpClient? relay;
        OwnerCredential? owner;
        lock (_gate)
        {
            relay = _relay;
            owner = _owner;
        }

        if (relay is null || owner is null)
        {
            return null;
        }

        var request = new HttpRequestMessage(method, relativePath.TrimStart('/'));
        AddBearer(request, owner);
        try
        {
            return await relay.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
    }
}
