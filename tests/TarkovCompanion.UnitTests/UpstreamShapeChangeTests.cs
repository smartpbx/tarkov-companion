using System.Text.Json;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What a renamed field upstream costs.
/// </summary>
/// <remarks>
/// #119 asked for a versioned schema published by the relay so a shape change could be fixed
/// once on the server. Costed, the win was narrower than it sounds — the client keeps the
/// upstream parser for the fallback regardless, so a player without a relay gains nothing — and
/// the price was a transforming mirror that can serve plausible wrong data to every client at
/// once, where a byte-for-byte one either has the bytes or does not.
///
/// This is the cheap half of the same goal. Every one of these payloads has lost a field that
/// the application does not actually need, and each one used to throw before a line of the
/// code that copes with it could run: `required` is a deserialiser contract, and it was
/// standing in front of fallbacks that already existed.
///
/// The line is where it always was. A record is still refused without the id it is keyed by.
/// </remarks>
public sealed class UpstreamShapeChangeTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void An_item_without_a_short_name_still_loads()
    {
        // The exact case: persistence already falls back to the full name when this is blank,
        // and the marker threw before that code could run.
        var item = Parse<TarkovDevItem>("""
            {"id":"item-1","name":"Bronze pocket watch","width":1,"height":1}
            """);

        Assert.Equal("item-1", item.Id);
        Assert.Equal(string.Empty, item.ShortName);
    }

    [Fact]
    public void A_map_without_a_name_still_loads()
    {
        Assert.Equal("map-1", Parse<TarkovDevMap>("""{"id":"map-1"}""").Id);
    }

    [Fact]
    public void An_exit_without_a_name_still_loads()
    {
        Assert.Equal("exit-1", Parse<TarkovDevMapExtract>("""{"id":"exit-1"}""").Id);
    }

    [Fact]
    public void A_task_without_a_name_still_loads()
    {
        Assert.Equal("task-1", Parse<TarkovDevTask>("""{"id":"task-1"}""").Id);
    }

    [Fact]
    public void A_category_without_a_name_still_loads()
    {
        Assert.Equal("cat-1", Parse<TarkovDevItemCategory>("""{"id":"cat-1"}""").Id);
    }

    /// <summary>
    /// The id is still required, and that is the point rather than an oversight.
    /// </summary>
    /// <remarks>
    /// A record with no id cannot be stored, joined or updated, so accepting one would trade a
    /// loud failure for a silent hole. Hardening is about the fields the application can do
    /// without, and this is not one.
    /// </remarks>
    [Fact]
    public void An_item_without_an_id_is_still_refused()
    {
        Assert.Throws<JsonException>(() => Parse<TarkovDevItem>("""
            {"name":"Bronze pocket watch","shortName":"Watch","width":1,"height":1}
            """));
    }

    [Fact]
    public void A_map_without_an_id_is_still_refused()
    {
        Assert.Throws<JsonException>(() => Parse<TarkovDevMap>("""{"name":"Customs"}"""));
    }

    private static T Parse<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("null");
}
