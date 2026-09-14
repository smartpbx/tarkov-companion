using System.Security.Cryptography;
using System.Text;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// Whether a request is the operator's, rather than a member's.
/// </summary>
/// <remarks>
/// A separate secret from the group key, and that separation is the point. Every member of every
/// group holds a group key; what an operator does — reading every group's reports, deciding which
/// rooms may exist — is not something any of them should be able to do by holding theirs.
///
/// The check was written out twice inline on the report endpoints, which is two places for it to
/// drift apart. It is one place now, compared in fixed time, and absent configuration refuses
/// rather than allows.
/// </remarks>
public static class RelayAdmin
{
    public const string Variable = "TARKOV_RELAY_ADMIN_KEY";

    public const string Header = "X-Admin-Key";

    /// <summary>Whether an operator secret was configured at all.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Secret);

    public static bool IsAuthorised(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Secret is not { Length: > 0 } secret ||
            !request.Headers.TryGetValue(Header, out var provided) ||
            provided.Count != 1)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided[0] ?? string.Empty),
            Encoding.UTF8.GetBytes(secret));
    }

    private static string? Secret => Environment.GetEnvironmentVariable(Variable);
}
