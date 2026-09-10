using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Infrastructure.Security;

public sealed class UnavailableIntegrationSecretStore : IIntegrationSecretStore
{
    public bool IsAvailable => false;

    public Task SaveAsync(
        IntegrationSecretReference reference,
        string secret,
        CancellationToken cancellationToken) =>
        Task.FromException(new PlatformNotSupportedException(
            "Protected integration secret storage is unavailable on this platform."));

    public Task<string?> LoadAsync(
        IntegrationSecretReference reference,
        CancellationToken cancellationToken) =>
        Task.FromException<string?>(new PlatformNotSupportedException(
            "Protected integration secret storage is unavailable on this platform."));

    public Task<bool> ExistsAsync(
        IntegrationSecretReference reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task DeleteAsync(
        IntegrationSecretReference reference,
        CancellationToken cancellationToken) =>
        Task.FromException(new PlatformNotSupportedException(
            "Protected integration secret storage is unavailable on this platform."));
}
