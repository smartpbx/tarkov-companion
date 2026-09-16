using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovCompanion.Core.Domain.Strategy.Data;

/// <summary>Fail-closed serializer for imported traffic data and model lifecycle documents.</summary>
/// <remarks>
/// The general v2 reader permits optional same-major additions. Traffic imports instead reject
/// unknown members: otherwise a sender could attach an identity, exact coordinate, or current-raid
/// flag and receive an apparently successful import after that prohibited field was discarded.
/// </remarks>
public static class TrafficDataJson
{
    public const int MaxDepth = 32;

    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = MaxDepth,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
