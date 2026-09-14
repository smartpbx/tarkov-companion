using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovCompanion.Core.Abstractions.V2;

/// <summary>The one serializer configuration for v2 contracts; transports must not use defaults.</summary>
/// <remarks>
/// Defaults fail open three ways: an enum may arrive as any integer, a missing constructor
/// argument becomes its default (an empty id, a zero enum), and null reaches non-nullable
/// references. Each is closed here; constructors then enforce the remaining invariants.
/// Unknown object fields are skipped so a same-major reader tolerates optional additions.
/// </remarks>
public static class V2ContractJson
{
    public const int MaxDepth = 64;

    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = MaxDepth,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
