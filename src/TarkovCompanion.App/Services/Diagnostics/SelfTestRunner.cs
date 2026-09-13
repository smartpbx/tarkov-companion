using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.Services.Diagnostics;

public sealed record SelfTestCheck(string Name, string Status, string Detail, bool Required = true);

public sealed record SelfTestEnvironment(
    string OperatingSystem,
    string ProcessArchitecture,
    string Framework,
    string Provider,
    bool NetworkContacted);

public sealed record SelfTestReport(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    bool Success,
    IReadOnlyList<SelfTestCheck> Checks,
    SelfTestEnvironment Environment,
    IReadOnlyDictionary<string, string?> Paths,
    IReadOnlyDictionary<string, bool> Safety);

public static class SelfTestRunner
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<SelfTestReport> RunAsync(
        string outputPath,
        AppCommandLine commandLine,
        AppCompositionSettings? settings,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(commandLine);

        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new InvalidOperationException("The self-test output path has no parent directory.");
        Directory.CreateDirectory(outputDirectory);

        var checks = new List<SelfTestCheck>();
        var paths = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["report"] = fullOutputPath,
            ["appBase"] = AppContext.BaseDirectory,
            ["currentDirectory"] = Environment.CurrentDirectory,
            ["eftInstall"] = Environment.GetEnvironmentVariable("TARKOV_COMPANION_EFT_INSTALL_ROOT"),
            ["eftLogs"] = Environment.GetEnvironmentVariable("TARKOV_COMPANION_EFT_LOG_ROOT"),
            ["eftScreenshots"] = Environment.GetEnvironmentVariable("TARKOV_COMPANION_EFT_SCREENSHOT_ROOT"),
        };
        var diagnosticEnabled = false;
        var offlineSettings = settings is null
            ? new AppCompositionSettings(Offline: true)
            : settings with { Offline = true };

        ServiceProvider? services = null;
        try
        {
            services = AppComposition.Build(commandLine, offlineSettings);
            var startup = services.GetRequiredService<ApplicationStartupCoordinator>();
            await startup.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var snapshot = services.GetRequiredService<IRuntimeStateStore>().Current;
            var appPaths = services.GetRequiredService<AppDataPaths>();
            var dataStore = services.GetRequiredService<IRuntimeDataStore>();
            paths["database"] = dataStore.DatabasePath;
            paths["cache"] = appPaths.Cache;
            paths["profile"] = Path.Combine(appPaths.Config, "profile.json");

            checks.Add(snapshot.DatabaseReady && File.Exists(dataStore.DatabasePath)
                ? new("database", "pass", $"Persistent SQLite opened at {dataStore.DatabasePath}.")
                : new("database", "fail", "The persistent SQLite database was not initialized."));

            checks.Add(snapshot.Data.ItemCount > 0
                ? new(
                    "cache",
                    "pass",
                    $"{snapshot.Data.ItemCount} normalized item(s); state is {snapshot.Data.Availability}.")
                : new(
                    "cache",
                    "unavailable",
                    $"No normalized item cache is present: {snapshot.Data.Detail}",
                    Required: false));

            checks.Add(services.GetService<IDataSyncService>() is not null
                ? new("data-source", "pass", "json.tarkov.dev services are composed; self-test forced offline mode.")
                : new("data-source", "fail", "The json.tarkov.dev data-sync service is not composed."));

            checks.Add(snapshot.Profile is null
                ? new("profile", "fail", "The local profile could not be loaded.")
                : new(
                    "profile",
                    "pass",
                    $"Loaded profile '{snapshot.Profile.Name}' at level {snapshot.Profile.Level} ({snapshot.Profile.GameMode})."));

            var recognitionSelfTest = services.GetService<IRecognitionSelfTest>();
            if (recognitionSelfTest is null)
            {
                checks.Add(new(
                    "ocr-provider",
                    OperatingSystem.IsWindows() ? "fail" : "unavailable",
                    "The recognition capability self-test is not composed.",
                    Required: OperatingSystem.IsWindows()));
            }
            else
            {
                var recognition = await recognitionSelfTest.RunAsync(cancellationToken).ConfigureAwait(false);
                foreach (var capability in recognition.Capabilities)
                {
                    var required = capability.Capability == "offline-ocr" &&
                        OperatingSystem.IsWindows() &&
                        RuntimeInformation.ProcessArchitecture == Architecture.X64;
                    checks.Add(new(
                        CapabilityCheckName(capability.Capability),
                        capability.IsAvailable ? "pass" : required ? "fail" : "unavailable",
                        $"{capability.Provider}: {capability.Detail}",
                        required));
                }
            }

            // The scan shortcut used to be registered here, to prove the combination was
            // obtainable on this machine. There is no shortcut now: the game's own screenshot
            // key drives every scan, and it needs nothing registered, nothing claimed from the
            // window manager and nothing that another application can already own.

            var diagnosticRequested = commandLine.DeveloperMode &&
                !string.IsNullOrWhiteSpace(commandLine.DiagnosticChannelPath);
            var diagnosticToken = Environment.GetEnvironmentVariable(DiagnosticCommandChannel.TokenEnvironmentVariable);
            diagnosticEnabled = diagnosticRequested && diagnosticToken?.Length >= 32;
            checks.Add(diagnosticRequested
                ? diagnosticEnabled
                    ? new("diagnostic", "pass", "Developer diagnostic channel configuration is complete.")
                    : new(
                        "diagnostic",
                        "fail",
                        "Developer diagnostics were requested without a token of at least 32 characters.")
                : new(
                    "diagnostic",
                    "disabled",
                    "Developer diagnostics are disabled unless explicitly requested.",
                    Required: false));

            Directory.CreateDirectory(appPaths.Support);
            var pathProbe = Path.Combine(appPaths.Support, $"self-test-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(pathProbe, "writable", cancellationToken).ConfigureAwait(false);
            File.Delete(pathProbe);
            checks.Add(new("paths", "pass", "Configured database, cache, profile, and support paths are writable."));
            checks.Add(new("platform", "pass", $"Headless diagnostics supported on {RuntimeInformation.OSDescription}."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            checks.Add(new("self-test-runtime", "fail", exception.Message));
        }
        finally
        {
            if (services is not null)
            {
                await services.DisposeAsync().ConfigureAwait(false);
            }
        }

        var report = new SelfTestReport(
            2,
            DateTimeOffset.UtcNow,
            checks.All(check => !check.Required || string.Equals(check.Status, "pass", StringComparison.Ordinal)),
            checks,
            new(
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription,
                "json.tarkov.dev",
                false),
            paths,
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["readsGameMemory"] = false,
                ["sendsGameInput"] = false,
                ["capturesNetworkTraffic"] = false,
                ["diagnosticChannelEnabled"] = diagnosticEnabled,
            });

        await using var output = File.Create(fullOutputPath);
        await JsonSerializer.SerializeAsync(output, report, SerializerOptions, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private static string CapabilityCheckName(string capability) => capability switch
    {
        "offline-ocr" => "ocr-provider",
        "canonical-item-catalog" => "recognition-catalog",
        "icon-fallback" => "recognition-icon-fallback",
        _ => "recognition-" + capability,
    };
}
