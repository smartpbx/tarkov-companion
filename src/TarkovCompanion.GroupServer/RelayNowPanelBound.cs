using System.Text;
using System.Text.Json;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// [#712 0-11] The bound on the Now panel block a desktop's map surface may carry.
/// </summary>
/// <remarks>
/// The relay holds the surface opaquely and keeps it that way: this only measures how many bytes
/// the one top-level <c>now</c> value takes, and reads nothing inside it. The desktop clips every
/// string and caps every list (<c>TabletNowPanel.Bounded</c>), so a real panel is a few KB even
/// with every character escaped; a larger one is a desktop gone wrong, and a tablet redrawing it
/// once a second is what this refuses. A surface without the key (an older desktop, or the flag
/// off) is not measured at all. Text that does not parse as JSON is left to the existing size
/// bound, as it was before this existed: no page draws it either way.
/// </remarks>
public static class RelayNowPanelBound
{
    public const int MaximumNowBytes = 96 * 1024;

    private static readonly byte[] NowKey = Encoding.UTF8.GetBytes("now");

    public static bool Fits(ReadOnlySpan<byte> surfaceJson)
    {
        try
        {
            var reader = new Utf8JsonReader(surfaceJson, new JsonReaderOptions { MaxDepth = 64 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return true;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isNow = reader.ValueTextEquals(NowKey);
                if (!reader.Read())
                {
                    return true;
                }

                var start = reader.TokenStartIndex;
                reader.Skip();
                if (isNow && reader.BytesConsumed - start > MaximumNowBytes)
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
