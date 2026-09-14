using System.Reflection;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

public sealed class V2ArchitectureContractTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Avalonia",
        "Microsoft.Data.Sqlite",
        "System.Data.SQLite",
        "System.Management",
        "Tesseract",
    ];

    private static readonly string[] ForbiddenTypeNamespacePrefixes =
    [
        "Avalonia",
        "Microsoft.Data.Sqlite",
        "Microsoft.Win32",
        "System.Diagnostics",
        "System.Net",
        "System.Runtime.InteropServices",
    ];

    private static readonly string[] ForbiddenProtocolTerms =
    [
        "GameMemory",
        "ProcessMemory",
        "GameplayInput",
        "MouseInput",
        "KeyboardInput",
        "ControllerInput",
        "InputInjection",
        "PacketCapture",
        "PacketInspection",
        "Packet",
        "GameOverlay",
        "Overlay",
        "Hook",
        "Inject",
        "ProcessHandle",
        "LiveEnemy",
        "EnemyTracking",
        "EnemyEsp",
        "EspObservation",
        "EspResult",
        "EnemyPosition",
        "PlayerPosition",
        "Radar",
    ];

    [Fact]
    public void CoreDoesNotReferencePlatformOrIoFrameworks()
    {
        var references = typeof(EvidenceSourceClass).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            ForbiddenAssemblyPrefixes.Any(prefix =>
                reference.Name?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true));
    }

    [Fact]
    public void V2PublicSurfaceDoesNotExposePlatformOrNetworkTypes()
    {
        foreach (var type in V2Types())
        {
            foreach (var exposedType in ExposedTypes(type))
            {
                Assert.DoesNotContain(ForbiddenTypeNamespacePrefixes, prefix =>
                    exposedType.Namespace?.StartsWith(prefix, StringComparison.Ordinal) == true);
            }
        }
    }

    [Fact]
    public void V2ProtocolHasNoForbiddenCapabilityVocabulary()
    {
        var names = V2Types()
            .SelectMany(type =>
                new[] { type.Name }
                    .Concat(type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                        .Select(member => member.Name))
                    .Concat(type.IsEnum ? Enum.GetNames(type) : []))
            .ToArray();

        foreach (var forbidden in ForbiddenProtocolTerms)
        {
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(
            Enum.GetNames<EvidenceSourceClass>(),
            name => name.Contains("Live", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenericEnvelopesAreClosedToAllowlistedPayloads()
    {
        var envelopes = new (Type Envelope, Type Marker, IReadOnlySet<Type> Allowed)[]
        {
            (typeof(RecognitionResultEnvelope<>), typeof(IRecognitionPayload), V2WirePayloads.Recognition),
            (typeof(ContextualRecognitionResult<>), typeof(IRecognitionPayload), V2WirePayloads.Recognition),
            (typeof(HistoricalIntelligence<>), typeof(IIntelligencePayload), V2WirePayloads.Intelligence),
            (typeof(ModelledIntelligence<>), typeof(IIntelligencePayload), V2WirePayloads.Intelligence),
            (typeof(RevisionedState<>), typeof(IWorkspaceStatePayload), V2WirePayloads.WorkspaceState),
        };

        foreach (var (envelope, marker, allowed) in envelopes)
        {
            Assert.Contains(marker, envelope.GetGenericArguments().Single().GetGenericParameterConstraints());

            var implementers = typeof(V2ContractVersion).Assembly.GetTypes()
                .Where(type => type.IsClass && !type.IsAbstract && marker.IsAssignableFrom(type))
                .ToHashSet();
            Assert.True(implementers.SetEquals(allowed), $"{marker.Name} implementers and allowlist differ.");
            Assert.All(allowed, type => Assert.True(type.IsSealed, $"{type.Name} must be sealed."));
        }

        // Every public generic type in the contract is either one of those envelopes or an evidence primitive.
        var openGenerics = V2Types().Where(type => type.IsGenericTypeDefinition).ToHashSet();
        openGenerics.ExceptWith(envelopes.Select(item => item.Envelope));
        openGenerics.ExceptWith([typeof(EvidencedValue<>), typeof(EvidenceCandidate<>), typeof(EvidenceCorrection<>)]);
        Assert.Empty(openGenerics);
    }

    [Fact]
    public void AllowlistsCannotBeCastBackOrMutated()
    {
        foreach (var allowed in new[] { V2WirePayloads.Recognition, V2WirePayloads.Intelligence, V2WirePayloads.WorkspaceState })
        {
            object hostile = allowed;
            Assert.False(hostile is HashSet<Type>);
            Assert.False(hostile is SortedSet<Type>);
            Assert.Throws<NotSupportedException>(() => ((ISet<Type>)hostile).Add(typeof(HostilePayload)));
            Assert.Throws<NotSupportedException>(() => ((ICollection<Type>)hostile).Clear());
            Assert.DoesNotContain(typeof(HostilePayload), (IReadOnlySet<Type>)allowed);
        }

        var property = typeof(V2WirePayloads).GetProperty(nameof(V2WirePayloads.Recognition))!;
        Assert.Null(property.SetMethod);
        Assert.Throws<ArgumentException>(() => new RecognitionResultEnvelope<HostilePayload>(
            V2ContractTestData.Header(RecognizedContext.Item),
            V2ContractTestData.Complete("hostile", new HostilePayload())));
    }

    [Fact]
    public void EvidencedPayloadValuesCanBeAbsentWithoutLookingRead()
    {
        // EvidencedValue<int> or <SomeEnum> cannot be Unknown at all, because presence is the test.
        var evidencedTypes = V2Types()
            .SelectMany(ExposedTypes)
            .SelectMany(Flatten)
            .Where(type =>
                type.IsGenericType &&
                !type.ContainsGenericParameters &&
                type.GetGenericTypeDefinition() == typeof(EvidencedValue<>))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(evidencedTypes);
        foreach (var argument in evidencedTypes.Select(type => type.GetGenericArguments()[0]))
        {
            var absentable = !argument.IsValueType || Nullable.GetUnderlyingType(argument) is not null;
            Assert.True(absentable, $"EvidencedValue<{argument.Name}> cannot represent an absent value.");
        }
    }

    [Fact]
    public void EnumZeroIsUndefinedOrUnknown()
    {
        foreach (var type in V2Types().Where(type => type.IsEnum))
        {
            var zero = Enum.GetName(type, 0);
            Assert.True(zero is null or "Unknown", $"{type.Name}.{zero} occupies zero, so a missing value would read as it.");
        }
    }

    [Fact]
    public void ContractPropertiesHaveNoPublicSetters()
    {
        var settable = V2Types()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.SetMethod is { IsPublic: true })
                .Select(property => $"{type.Name}.{property.Name}"))
            .ToArray();

        // Any of these could be init-assigned in a `with` expression, bypassing validation.
        Assert.Empty(settable);
    }

    [Fact]
    public void ValueStructsDeserializeThroughTheirValidatingConstructor()
    {
        var structs = V2Types().Where(type => type.IsValueType && !type.IsEnum).ToArray();

        Assert.NotEmpty(structs);
        Assert.All(structs, type => Assert.Contains(
            type.GetConstructors(),
            constructor => constructor.GetParameters().Length > 0 &&
                constructor.IsDefined(typeof(JsonConstructorAttribute))));
    }

    [Fact]
    public void GameFacingCapabilityIsAReadOnlyInterface()
    {
        var source = typeof(IUserInitiatedVisibleCaptureSource);
        var methods = source.GetMethods();

        Assert.True(source.IsInterface);
        Assert.Single(methods);
        Assert.Equal(nameof(IUserInitiatedVisibleCaptureSource.CaptureAsync), methods[0].Name);
    }

    private static IReadOnlyList<Type> V2Types() => typeof(V2ContractVersion).Assembly
        .GetTypes()
        .Where(type =>
            type.IsPublic &&
            (type.Namespace == "TarkovCompanion.Core.Abstractions.V2" ||
             type.Namespace == "TarkovCompanion.Core.Domain.Evidence"))
        .ToArray();

    private static IEnumerable<Type> ExposedTypes(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            foreach (var exposed in Flatten(property.PropertyType))
            {
                yield return exposed;
            }
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            foreach (var exposed in Flatten(method.ReturnType))
            {
                yield return exposed;
            }

            foreach (var parameter in method.GetParameters())
            {
                foreach (var exposed in Flatten(parameter.ParameterType))
                {
                    yield return exposed;
                }
            }
        }
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        if (type.IsArray && type.GetElementType() is { } element)
        {
            foreach (var nested in Flatten(element))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Flatten(argument))
            {
                yield return nested;
            }
        }
    }

    private sealed record HostilePayload : IRecognitionPayload;
}
