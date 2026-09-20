using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Builds the exact privacy-safe text that can be previewed, copied, or sent for support.
/// </summary>
/// <remarks>
/// A redactor used to accept arbitrary runtime details and application-log lines, then try to
/// find the secrets inside them. One missed path form or new log message made the whole report
/// unsafe. This report instead projects runtime state into a closed vocabulary before rendering:
/// fixed categories, booleans, capped counts, and a numeric version plus bounded commit prefix.
/// </remarks>
public static class SupportBundle
{
    /// <summary>How many screenshot names may be classified without ever rendering a name.</summary>
    private const int NameSamples = 3;

    private const int MaximumScreenshotNameLength = 256;
    private const int MaximumItemCount = 1_000_000;
    private const int MaximumEndpointCount = 1_000;
    private const int MaximumRaidEvidenceCount = 1_000;
    private const int MaximumGroupCount = 100;

    /// <summary>The last line of every bundle, stating the structural exclusion boundary.</summary>
    public const string Footer =
        "This report contains only allowlisted categories, capped counts, and build/platform facts. " +
        "It excludes free-form text, paths, screenshot names and pixels, OCR text, coordinates, " +
        "names, credentials, tokens, and exception bodies.";

    /// <summary>
    /// Builds the same sanitized text used by Copy diagnostics and Report a problem.
    /// </summary>
    /// <remarks>
    /// <paramref name="logPath"/> remains in the signature for existing callers, but is never
    /// opened or rendered. The recent names are inspected only for parser compatibility; their
    /// characters and the coordinates encoded in them never cross into the returned text.
    /// </remarks>
    /// <summary>How many self-test lines may be carried, so a future probe list cannot grow this.</summary>
    private const int MaximumSelfTestFacts = 32;

    public static string Describe(
        ApplicationRuntimeSnapshot snapshot,
        IReadOnlyList<string> recentScreenshotNames,
        string? logPath) =>
        Describe(snapshot, recentScreenshotNames, logPath, selfTest: null);

    /// <summary>
    /// The same report, plus the closed projection of the last self-test.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 41] The self-test's own text names folders, files, endpoints and a
    /// position, which docs/SAFETY.md allows it to do locally and does not allow here. What
    /// crosses into an outbound report is a fixed capability identifier and its verdict —
    /// enough to say which capability failed, and nothing that identifies the machine it
    /// failed on. The caller is responsible for passing
    /// <c>SelfTestSummary.ToSupportFacts</c>, which is the only producer of that shape.
    /// </remarks>
    public static string Describe(
        ApplicationRuntimeSnapshot snapshot,
        IReadOnlyList<string> recentScreenshotNames,
        string? logPath,
        IReadOnlyList<(string Key, string Value)>? selfTest) =>
        Describe(snapshot, recentScreenshotNames, logPath, selfTest, startupFaults: null);

    /// <summary>
    /// The same report, plus which pages did not load and whether the last run was killed.
    /// </summary>
    /// <remarks>
    /// Two questions this report could not answer on 2026-09-19, when both were the answer. The
    /// application had died on a map load and the report said "database ready, data current" —
    /// true, and useless, because it had no vocabulary for "the run before this one never reached
    /// its own shutdown". Separately, a page load that failed at startup left several pages empty
    /// for the session, and the report could not name one.
    ///
    /// Both fit the closed projection this file is built on. A page that failed is one identifier
    /// from a fixed set, exactly like the self-test's capability keys above; a run that died is a
    /// boolean. Neither carries a path, a message or an exception body, and a name this file does
    /// not recognise is rendered as "other" rather than passed through — the vocabulary is closed
    /// here, not at the caller, because that is the only place it can be guaranteed.
    /// </remarks>
    public static string Describe(
        ApplicationRuntimeSnapshot snapshot,
        IReadOnlyList<string> recentScreenshotNames,
        string? logPath,
        IReadOnlyList<(string Key, string Value)>? selfTest,
        IReadOnlyList<string>? startupFaults)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(recentScreenshotNames);

        // Keep this explicit. Merely not using the log today is weaker than documenting that an
        // outbound report must never open arbitrary diagnostic text again.
        _ = logPath;

        var assembly = typeof(SupportBundle).Assembly;
        var screenshotShape = ClassifyScreenshotNames(recentScreenshotNames);

        var report = new StringBuilder(capacity: 1_500);
        report.AppendLine("## Tarkov Companion diagnostics preview");
        report.AppendLine();
        report.AppendLine("### Build and platform");
        AppendFact(report, "report schema", "2");
        AppendFact(report, "build", BuildIdentity(assembly));
        AppendFact(report, "platform", Platform());
        AppendFact(report, "architecture", Architecture());
        AppendFact(report, "culture kind", CultureKind());
        AppendFact(report, "decimal style", DecimalStyle());
        report.AppendLine();

        report.AppendLine("### Runtime");
        AppendFact(report, "demo mode", YesNo(snapshot.IsDemoMode));
        AppendFact(report, "offline", YesNo(snapshot.IsOffline));
        AppendFact(report, "database ready", YesNo(snapshot.DatabaseReady));
        report.AppendLine();

        report.AppendLine("### Observation");
        AppendFact(report, "platform supported", YesNo(snapshot.Observation.IsSupported));
        AppendFact(report, "watching logs", YesNo(snapshot.Observation.IsWatchingLogs));
        AppendFact(report, "watching screenshots", YesNo(snapshot.Observation.IsWatchingScreenshots));
        AppendFact(report, "confidence", ConfidenceBand(snapshot.Observation.Confidence.Value));
        report.AppendLine();

        report.AppendLine("### Raid");
        AppendFact(report, "state", RaidState(snapshot.Raid.State));
        AppendFact(report, "map selected", YesNo(snapshot.Raid.MapId is not null));
        AppendFact(report, "position available", YesNo(snapshot.Raid.LastKnownPosition is not null));
        AppendFact(
            report,
            "active extracts",
            BoundedCount(snapshot.Raid.ActiveExtracts?.Count ?? 0, MaximumRaidEvidenceCount));
        AppendFact(
            report,
            "unmatched extract readings",
            BoundedCount(snapshot.Raid.ExtractLinesNotMatched?.Count ?? 0, MaximumRaidEvidenceCount));
        AppendFact(
            report,
            "transits",
            BoundedCount(snapshot.Raid.Transits?.Count ?? 0, MaximumRaidEvidenceCount));
        AppendFact(report, "recent screenshot-name count", screenshotShape.TotalCount);
        AppendFact(report, "screenshot-name sample size", screenshotShape.SampleCount);
        AppendFact(report, "screenshot-name compatibility", screenshotShape.Compatibility);
        report.AppendLine();

        report.AppendLine("### Data");
        AppendFact(report, "availability", DataAvailability(snapshot.Data.Availability));
        AppendFact(report, "items cached", BoundedCount(snapshot.Data.ItemCount, MaximumItemCount));
        AppendFact(
            report,
            "endpoints synced",
            BoundedCount(snapshot.Data.SyncedEndpointCount, MaximumEndpointCount));
        report.AppendLine();

        report.AppendLine("### Sharing");
        AppendFact(report, "enabled", YesNo(snapshot.Group.IsSharing));
        AppendFact(
            report,
            "members present",
            BoundedCount(snapshot.Group.Members?.Count ?? 0, MaximumGroupCount));
        AppendFact(
            report,
            "waypoints present",
            BoundedCount(snapshot.Group.Waypoints?.Count ?? 0, MaximumGroupCount));
        AppendFact(
            report,
            "pings present",
            BoundedCount(snapshot.Group.Pings?.Count ?? 0, MaximumGroupCount));
        AppendFact(report, "relay state stale", YesNo(snapshot.Group.StaleSince is not null));
        // v2r-fast-positions (package 31): measured, so "the markers are slow" can be answered
        // with a number from the machine it happened on rather than with a guess.
        AppendFact(report, "squadmate position latency", snapshot.Group.PositionLatency.Describe());
        report.AppendLine();

        if (selfTest is { Count: > 0 })
        {
            report.AppendLine("### Self-test");
            foreach (var (key, value) in selfTest.Take(MaximumSelfTestFacts))
            {
                AppendFact(report, Bounded(key), Bounded(value));
            }

            report.AppendLine();
        }

        report.AppendLine("### Stability");
        AppendFact(report, "previous run reached shutdown", YesNo(!CrashBreadcrumbs.PreviousRunDied));
        var faults = ClosedStartupFaults(startupFaults);
        AppendFact(report, "pages that did not load at startup", BoundedCount(faults.Count, MaximumStartupFaults));
        foreach (var page in faults)
        {
            AppendFact(report, "page did not load", page);
        }

        report.AppendLine();

        report.AppendLine("### Privacy boundary");
        AppendFact(report, "application log content included", "no");
        AppendFact(report, "runtime detail text included", "no");
        AppendFact(report, "screenshot or OCR content included", "no");
        report.AppendLine();
        report.AppendLine(Footer);
        return report.ToString();
    }

    /// <summary>How many failed pages may be named, so a future page list cannot grow this.</summary>
    private const int MaximumStartupFaults = 32;

    /// <summary>
    /// The pages this report is allowed to name, and the only ones it will.
    /// </summary>
    /// <remarks>
    /// The same ten identifiers <c>MainWindowViewModel.InitializeAsync</c> starts, written out
    /// rather than inferred. Kept here because a closed vocabulary enforced at the caller is not
    /// closed: this file's whole design is that it projects into a fixed set before rendering,
    /// instead of accepting arbitrary text and hoping to find the secrets in it.
    /// </remarks>
    private static readonly IReadOnlySet<string> KnownPages = new HashSet<string>(StringComparer.Ordinal)
    {
        "items", "quests", "history", "scanner", "hideout", "ammo", "keys", "events", "group", "map",
    };

    /// <summary>
    /// The failed pages, reduced to names this report recognises.
    /// </summary>
    /// <remarks>
    /// An unrecognised name becomes "other" rather than being dropped, because "a page failed and
    /// this build cannot say which" is still worth knowing, and rather than being rendered,
    /// because a name this file has not seen is exactly the free-form text it must not carry.
    /// </remarks>
    private static IReadOnlyList<string> ClosedStartupFaults(IReadOnlyList<string>? faults) => faults is null
        ? []
        : [.. faults
            .Take(MaximumStartupFaults)
            .Select(page => KnownPages.Contains(page) ? page : "other")
            .Distinct(StringComparer.Ordinal)];

    /// <summary>A short, single-line value. The projection is closed; this keeps it that way.</summary>
    private static string Bounded(string value)
    {
        const int Maximum = 64;
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var flattened = new string([.. value.Where(character => !char.IsControl(character))]).Trim();
        return flattened.Length <= Maximum ? flattened : flattened[..Maximum];
    }

    private static void AppendFact(StringBuilder report, string label, string value) =>
        report.AppendLine(CultureInfo.InvariantCulture, $"- {label}: {value}");

    private static ScreenshotNameSummary ClassifyScreenshotNames(IReadOnlyList<string> names)
    {
        var inspected = Math.Min(Math.Max(names.Count, 0), NameSamples);
        if (inspected == 0)
        {
            return new("0", "0", "none observed");
        }

        var parser = new ScreenshotFilenameParser();
        var compatible = 0;
        for (var index = 0; index < inspected; index++)
        {
            var name = names[index];
            if (name is null || name.Length > MaximumScreenshotNameLength)
            {
                continue;
            }

            try
            {
                if (parser.TryParse(name, TimeSpan.Zero, out _))
                {
                    compatible++;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                // Classification is optional evidence. A malformed name is incompatible; its
                // exception body is neither useful nor safe to place in an outbound report.
            }
        }

        var compatibility = compatible switch
        {
            0 => "none compatible",
            _ when compatible == inspected => "all compatible",
            _ => "mixed",
        };

        return new(
            BoundedCount(names.Count, NameSamples),
            inspected.ToString(CultureInfo.InvariantCulture),
            compatibility);
    }

    private static string BuildIdentity(Assembly assembly)
    {
        var fallback = assembly.GetName().Version?.ToString() ?? "unknown";
        var value = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (value is null || value.Length > 200)
        {
            return fallback;
        }

        var separator = value.IndexOf('+');
        var versionText = separator < 0 ? value : value[..separator];
        if (!Version.TryParse(versionText, out var version))
        {
            return fallback;
        }

        var safeVersion = version.ToString();
        if (separator < 0)
        {
            return safeVersion;
        }

        var metadata = value.AsSpan(separator + 1);
        var metadataSeparator = metadata.IndexOf('.');
        var commit = metadataSeparator < 0 ? metadata : metadata[..metadataSeparator];
        if (commit.Length is < 7 or > 64)
        {
            return safeVersion;
        }

        foreach (var character in commit)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return safeVersion;
            }
        }

        return safeVersion + "+" + commit[..Math.Min(commit.Length, 12)].ToString();
    }

    private static string BoundedCount(int value, int maximum) => value switch
    {
        < 0 => "invalid",
        _ when value > maximum => string.Create(CultureInfo.InvariantCulture, $"{maximum}+"),
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string ConfidenceBand(double value) => value switch
    {
        <= 0 => "unknown",
        < 0.5 => "low",
        < 0.8 => "medium",
        _ => "high",
    };

    private static string Platform() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsLinux() ? "linux" :
        OperatingSystem.IsMacOS() ? "macos" :
        "other";

    private static string Architecture() => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm => "arm",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.Wasm => "wasm",
        _ => "other",
    };

    private static string DecimalStyle() => CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator switch
    {
        "." => "dot",
        "," => "comma",
        _ => "other",
    };

    private static string CultureKind()
    {
        var culture = CultureInfo.CurrentCulture;
        if (culture.Equals(CultureInfo.InvariantCulture))
        {
            return "invariant";
        }

        return culture.CultureTypes.HasFlag(CultureTypes.UserCustomCulture)
            ? "custom"
            : "standard";
    }

    private static string RaidState(RaidLifecycleState state) => state switch
    {
        RaidLifecycleState.Unknown => "unknown",
        RaidLifecycleState.LauncherOrGameDetected => "game detected",
        RaidLifecycleState.Menu => "menu",
        RaidLifecycleState.LoadingRaid => "loading raid",
        RaidLifecycleState.InRaid => "in raid",
        RaidLifecycleState.PostRaid => "post raid",
        _ => "invalid",
    };

    private static string DataAvailability(
        TarkovCompanion.Application.Services.Runtime.DataAvailability availability) =>
        availability switch
        {
            TarkovCompanion.Application.Services.Runtime.DataAvailability.Unavailable => "unavailable",
            TarkovCompanion.Application.Services.Runtime.DataAvailability.Cached => "cached",
            TarkovCompanion.Application.Services.Runtime.DataAvailability.Current => "current",
            TarkovCompanion.Application.Services.Runtime.DataAvailability.Refreshing => "refreshing",
            TarkovCompanion.Application.Services.Runtime.DataAvailability.DemoFixture => "demo fixture",
            TarkovCompanion.Application.Services.Runtime.DataAvailability.Error => "error",
            _ => "invalid",
        };

    private sealed record ScreenshotNameSummary(
        string TotalCount,
        string SampleCount,
        string Compatibility);
}
