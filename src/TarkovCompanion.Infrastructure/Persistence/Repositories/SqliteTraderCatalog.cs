using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads the trader names every sync has been writing since migration 0001.
/// </summary>
/// <remarks>
/// Two columns out of a table of about a dozen rows, which is why there is no caching here and
/// no snapshot type around it. The read is cheap; what was expensive was not having it, because
/// every consumer holding a trader id printed the id.
/// </remarks>
public sealed class SqliteTraderCatalog(SqliteConnectionFactory connectionFactory) : ITraderCatalog
{
    public async Task<IReadOnlyDictionary<string, string>> GetNamesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM traders;";

        // Ordinal, because a trader id is a hexadecimal string from the feed and the only
        // comparison that can be made about it is whether it is the same string.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var name = reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            {
                names[id] = name.Trim();
            }
        }

        return names;
    }
}
