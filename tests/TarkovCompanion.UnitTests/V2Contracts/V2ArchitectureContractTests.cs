using System.Reflection;
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
        "PacketCapture",
        "PacketInspection",
        "GameOverlay",
        "LiveEnemy",
        "EnemyTracking",
        "EnemyEsp",
        "EspObservation",
        "EspResult",
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
                        .Select(member => member.Name)))
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
}
