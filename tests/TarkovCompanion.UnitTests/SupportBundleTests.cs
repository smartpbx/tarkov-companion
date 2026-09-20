using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

public sealed class SupportBundleTests
{
    /// <summary>The report remains useful without accepting arbitrary text into its payload.</summary>
    [Fact]
    public void PreviewCarriesOnlyClosedOperationalFacts()
    {
        var snapshot = Snapshot() with
        {
            DatabaseReady = true,
            Data = new(DataAvailability.Cached, 42, 7, null, "not rendered"),
            Observation = new(
                true,
                true,
                true,
                @"C:\ignored",
                "/ignored",
                new Confidence(0.75),
                "not rendered"),
        };

        var report = SupportBundle.Describe(
            snapshot,
            ["2026-09-13[20-15]_123.4, 5.6, -78.9_0.0, 0.0, 0.0, 1.0_12.00.png"],
            "/ignored/application.log");

        Assert.StartsWith("## Tarkov Companion diagnostics preview", report, StringComparison.Ordinal);
        Assert.Contains("- report schema: 2", report, StringComparison.Ordinal);
        Assert.Matches(
            @"(?m)^- build: (?:unknown|\d+(?:\.\d+){1,3}(?:\+[0-9a-fA-F]{7,12})?)\r?$",
            report);
        Assert.Matches("(?m)^- culture kind: (?:invariant|standard|custom)\r?$", report);
        Assert.Contains("- database ready: yes", report, StringComparison.Ordinal);
        Assert.Contains("- watching logs: yes", report, StringComparison.Ordinal);
        Assert.Contains("- confidence: medium", report, StringComparison.Ordinal);
        Assert.Contains("- availability: cached", report, StringComparison.Ordinal);
        Assert.Contains("- items cached: 42", report, StringComparison.Ordinal);
        Assert.Contains("- endpoints synced: 7", report, StringComparison.Ordinal);
        Assert.Contains("- screenshot-name compatibility: all compatible", report, StringComparison.Ordinal);
        Assert.EndsWith(SupportBundle.Footer + Environment.NewLine, report, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every previously free-form source is hostile here; none may cross the report boundary.
    /// </summary>
    /// <remarks>
    /// This is intentionally one complete-payload proof. Testing a path redactor by itself had
    /// left the same path intact when it arrived through a detail, screenshot name, or log line.
    /// </remarks>
    [Fact]
    public void HostileRuntimeAndLogTextCannotReachTheCompletePreview()
    {
        const string windowsPath = @"C:\Users\Clayton-Private\AppData\Roaming\secret.txt";
        const string uncPath = @"\\nas01\players\Clayton-Private\secret.txt";
        const string posixPath = "/home/clayton-private/.config/tarkov/token.txt";
        const string screenshotName =
            "2026-09-13[20-15]_987654.321, -456789.123, 314159.265_0.0, 0.0, 0.0, 1.0_12.00.png";
        const string ocrText = "OCR_PRIVATE_EXTRACT_TEXT";
        const string displayName = "Squadmate_Display_Name_Secret";
        // Assemble the hostile credential at runtime so the repository's secret scanner does
        // not mistake this negative-test fixture for a committed credential.
        var token = string.Concat(
            "Author",
            "ization: Bear",
            "er super-secret-auth-token-281");
        const string groupKey = "X-Group-Key: private-room-key-281";
        const string pixels = "PNG_PIXEL_BYTES_89504E47_PRIVATE";
        const string markdownInjection = "```\r\nforged-report-field: private-markdown-281";
        const string jsonSecret = "{\"key\":\"json-private-key-281\"}";
        const string querySecret = "https://relay.invalid/report?token=query-private-token-281";
        const string exceptionBody =
            "System.InvalidOperationException: private exception body at Secret.Namespace.Throw()";
        const string observationDetail = "OBSERVATION_PRIVATE_DETAIL";
        const string dataDetail = "DATA_PRIVATE_DETAIL";
        const string groupDetail = "GROUP_PRIVATE_DETAIL";
        const string mapId = "MAP_PRIVATE_DETAIL";
        const string extractId = "EXTRACT_ID_PRIVATE_DETAIL";
        const string extractName = "EXTRACT_NAME_PRIVATE_DETAIL";
        const string evidenceSource = "EVIDENCE_SOURCE_PRIVATE_DETAIL";
        const string waypointLabel = "WAYPOINT_LABEL_PRIVATE_DETAIL";
        const string reachedBy = "REACHED_BY_PRIVATE_DETAIL";
        const string hudDetail = "HUD_PRIVATE_DETAIL";
        const string sideBasis = "SIDE_BASIS_PRIVATE_DETAIL";
        const string startedByEvent = "START_EVENT_PRIVATE_DETAIL";

        var position = new ScreenshotPosition(
            DateTimeOffset.UnixEpoch,
            new WorldPosition(987654.321, -456789.123, 314159.265),
            new QuaternionOrientation(0, 0, 0, 1),
            271.828,
            TimeSpan.FromSeconds(1618.033),
            42,
            screenshotName);

        var member = new GroupMemberView(
            displayName,
            mapId,
            RaidLifecycleState.InRaid,
            "SIDE_PRIVATE_DETAIL",
            null,
            null,
            null,
            [token],
            [ocrText]);
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            Data = new(DataAvailability.Error, int.MaxValue, -9, null, dataDetail),
            Observation = new(
                true,
                true,
                true,
                windowsPath,
                posixPath,
                Confidence.Certain,
                observationDetail),
            Raid = snapshot.Raid with
            {
                MapId = mapId,
                State = RaidLifecycleState.InRaid,
                Side = "SIDE_PRIVATE_DETAIL",
                SideBasis = sideBasis,
                StartedByEventId = startedByEvent,
                LastKnownPosition = position,
                PositionTrail = [position],
                ActiveExtracts = [new(extractId, extractName, Confidence.Certain, evidenceSource)],
                Hud = new(true, hudDetail, DateTimeOffset.UnixEpoch, [new("HUD_KIND_PRIVATE", 10, 20)]),
                ExtractLinesNotMatched = [ocrText, exceptionBody],
                Transits = [uncPath],
            },
            Scan = snapshot.Scan with
            {
                CanonicalItemId = token,
                ItemName = displayName,
                Source = pixels,
                Detail = exceptionBody,
            },
            Group = new(
                true,
                [member],
                $"{groupDetail} {groupKey} {token} {markdownInjection} {jsonSecret} {querySecret}",
                DateTimeOffset.UnixEpoch)
            {
                Waypoints =
                [
                    new(1, displayName, mapId, 987654.321, -456789.123, 314159.265, waypointLabel, reachedBy),
                ],
                Pings =
                [
                    new(2, displayName, mapId, -987654.321, 456789.123, -314159.265, waypointLabel, DateTimeOffset.UnixEpoch),
                ],
                MyLoadout = [pixels, ocrText, token],
                MySide = "MY_SIDE_PRIVATE_DETAIL",
                StaleSince = DateTimeOffset.UnixEpoch,
            },
        };

        var log = Path.Combine(Path.GetTempPath(), $"support-bundle-{Guid.NewGuid():N}.log");
        File.WriteAllText(
            log,
            string.Join(
                Environment.NewLine,
                windowsPath,
                uncPath,
                posixPath,
                screenshotName,
                ocrText,
                displayName,
                token,
                groupKey,
                pixels,
                markdownInjection,
                jsonSecret,
                querySecret,
                exceptionBody));

        try
        {
            var report = SupportBundle.Describe(
                snapshot,
                [screenshotName, windowsPath, uncPath, posixPath],
                log);

            var forbidden = new[]
            {
                windowsPath,
                uncPath,
                posixPath,
                screenshotName,
                "987654.321",
                "-456789.123",
                ocrText,
                displayName,
                "super-secret-auth-token-281",
                "private-room-key-281",
                pixels,
                "private-markdown-281",
                "json-private-key-281",
                "query-private-token-281",
                exceptionBody,
                observationDetail,
                dataDetail,
                groupDetail,
                mapId,
                "SIDE_PRIVATE_DETAIL",
                extractId,
                extractName,
                evidenceSource,
                waypointLabel,
                reachedBy,
                hudDetail,
                sideBasis,
                startedByEvent,
                "HUD_KIND_PRIVATE",
                "MY_SIDE_PRIVATE_DETAIL",
            };
            Assert.All(
                forbidden,
                value => Assert.DoesNotContain(value, report, StringComparison.OrdinalIgnoreCase));

            Assert.Contains("- items cached: 1000000+", report, StringComparison.Ordinal);
            Assert.Contains("- endpoints synced: invalid", report, StringComparison.Ordinal);
            Assert.Contains("- members present: 1", report, StringComparison.Ordinal);
            Assert.Contains("- waypoints present: 1", report, StringComparison.Ordinal);
            Assert.Contains("- pings present: 1", report, StringComparison.Ordinal);
            Assert.Contains("- position available: yes", report, StringComparison.Ordinal);
            Assert.Contains("- active extracts: 1", report, StringComparison.Ordinal);
            Assert.Contains("- unmatched extract readings: 2", report, StringComparison.Ordinal);
            Assert.Contains("- recent screenshot-name count: 3+", report, StringComparison.Ordinal);
            Assert.Contains("- screenshot-name sample size: 3", report, StringComparison.Ordinal);
            Assert.Contains("- screenshot-name compatibility: mixed", report, StringComparison.Ordinal);
            Assert.Contains("- application log content included: no", report, StringComparison.Ordinal);
            AssertClosedSchema(report);
            Assert.True(report.Length < 3_000, "The closed report unexpectedly grew beyond its bounded schema.");
            Assert.EndsWith(SupportBundle.Footer + Environment.NewLine, report, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(log);
        }
    }

    /// <summary>An absent sample says so without touching a supplied log path.</summary>
    [Fact]
    public void MissingEvidenceUsesFixedCategories()
    {
        var report = SupportBundle.Describe(
            Snapshot(),
            [],
            @"\\server-that-must-not-be-opened\share\report.log");

        Assert.Contains("- recent screenshot-name count: 0", report, StringComparison.Ordinal);
        Assert.Contains("- screenshot-name sample size: 0", report, StringComparison.Ordinal);
        Assert.Contains("- screenshot-name compatibility: none observed", report, StringComparison.Ordinal);
        Assert.DoesNotContain("server-that-must-not-be-opened", report, StringComparison.Ordinal);
    }

    private static void AssertClosedSchema(string report)
    {
        var lines = report.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);
        var headings = lines.Where(line => line.StartsWith('#')).ToArray();
        Assert.Equal(
            [
                "## Tarkov Companion diagnostics preview",
                "### Build and platform",
                "### Runtime",
                "### Observation",
                "### Raid",
                "### Data",
                "### Sharing",
                "### Stability",
                "### Privacy boundary",
            ],
            headings);

        var facts = lines.Where(line => line.StartsWith("- ", StringComparison.Ordinal)).ToArray();
        var labels = facts.Select(line =>
        {
            var separator = line.IndexOf(':', 2);
            Assert.True(separator > 2, $"Diagnostic fact has no label separator: {line}");
            return line[2..separator];
        }).ToArray();
        Assert.Equal(
            [
                "report schema",
                "build",
                "platform",
                "architecture",
                "culture kind",
                "decimal style",
                "demo mode",
                "offline",
                "database ready",
                "platform supported",
                "watching logs",
                "watching screenshots",
                "confidence",
                "state",
                "map selected",
                "position available",
                "active extracts",
                "unmatched extract readings",
                "transits",
                "recent screenshot-name count",
                "screenshot-name sample size",
                "screenshot-name compatibility",
                "availability",
                "items cached",
                "endpoints synced",
                "enabled",
                "members present",
                "waypoints present",
                "pings present",
                "relay state stale",
                // v2r-fast-positions (package 31): counts and durations, never who or where.
                "squadmate position latency",
                // Whether the last run was killed, and how many pages failed to load. Two facts
                // the report had no vocabulary for on 2026-09-19, when both were the answer. The
                // page names themselves are conditional and so are asserted by their own tests.
                "previous run reached shutdown",
                "pages that did not load at startup",
                "application log content included",
                "runtime detail text included",
                "screenshot or OCR content included",
            ],
            labels);

        Assert.Equal(headings.Length + facts.Length + 1, lines.Length);
        Assert.Equal(SupportBundle.Footer, lines[^1]);
    }

    /// <summary>
    /// The report names which pages did not load, from a closed set.
    /// </summary>
    /// <remarks>
    /// A page load that failed at startup used to leave the pane empty and the report silent, so
    /// "several pages never filled in" could not be matched to a cause. These are the same
    /// identifiers MainWindowViewModel starts them under, which is why they can be named at all:
    /// one of a fixed set is not free-form text.
    /// </remarks>
    [Fact]
    public void ThePagesThatDidNotLoadAreNamed()
    {
        var report = SupportBundle.Describe(
            Snapshot(),
            [],
            "/ignored/application.log",
            selfTest: null,
            startupFaults: ["hideout", "map"]);

        Assert.Contains("### Stability", report, StringComparison.Ordinal);
        Assert.Contains("- pages that did not load at startup: 2", report, StringComparison.Ordinal);
        Assert.Contains("- page did not load: hideout", report, StringComparison.Ordinal);
        Assert.Contains("- page did not load: map", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name this report does not know does not reach it.
    /// </summary>
    /// <remarks>
    /// The point of the closed projection, applied to the newest thing that enters it. A caller
    /// that one day passes a path, a message or a player's own words must not be able to put them
    /// in an outbound report by calling them a page. It is still counted — "a page failed and this
    /// build cannot say which" is worth knowing — but it is not rendered.
    /// </remarks>
    [Fact]
    public void APageNameTheReportDoesNotKnowIsNotRendered()
    {
        var report = SupportBundle.Describe(
            Snapshot(),
            [],
            null,
            selfTest: null,
            startupFaults: [@"C:\Users\Clay\AppData\secret.db", "hideout"]);

        Assert.DoesNotContain("Clay", report, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.db", report, StringComparison.Ordinal);
        Assert.Contains("- page did not load: other", report, StringComparison.Ordinal);
        Assert.Contains("- page did not load: hideout", report, StringComparison.Ordinal);
    }

    /// <summary>A healthy run says so, in the one vocabulary the report has for it.</summary>
    /// <remarks>
    /// The fact that was missing on 2026-09-19: the application had died on a map load and the
    /// report said "database ready, data current", which was true and useless.
    /// </remarks>
    [Fact]
    public void TheReportSaysWhetherThePreviousRunReachedItsOwnShutdown()
    {
        var report = SupportBundle.Describe(Snapshot(), [], null, selfTest: null, startupFaults: null);

        Assert.Contains("- previous run reached shutdown: ", report, StringComparison.Ordinal);
        Assert.Contains("- pages that did not load at startup: 0", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// After a run that died, the report says where it was, reduced to the characters an address
    /// is made of: no spaces, no path separators, and no longer than any other fact.
    /// </summary>
    [Fact]
    public void AfterARunThatDiedTheReportNamesItsLastPageAndMap()
    {
        var report = SupportBundle.Describe(
            Snapshot(),
            [],
            null,
            selfTest: null,
            startupFaults: null,
            previousRun: new("#/plan", "reserve/reserve-2d C:\\Users\\somebody", MapWasBeingDrawn: true, WasFrozen: false));

        Assert.Contains("- previous run last page: #/plan", report, StringComparison.Ordinal);
        Assert.Contains("- previous run last map: reserve/reserve-2dC:Userssomebody", report, StringComparison.Ordinal);
        Assert.Contains("- previous run died drawing that map: yes", report, StringComparison.Ordinal);
        Assert.Contains("- previous run was frozen: no", report, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", report, StringComparison.Ordinal);
        Assert.DoesNotContain(" somebody", report, StringComparison.Ordinal);

        var bounded = SupportBundle.Describe(
            Snapshot(), [], null, selfTest: null, startupFaults: null,
            previousRun: new(new string('a', 500), null, MapWasBeingDrawn: false, WasFrozen: true));
        Assert.Contains("- previous run last page: " + new string('a', 64) + Environment.NewLine, bounded, StringComparison.Ordinal);
        Assert.Contains("- previous run last map: unknown", bounded, StringComparison.Ordinal);
        Assert.Contains("- previous run was frozen: yes", bounded, StringComparison.Ordinal);
    }

    private static ApplicationRuntimeSnapshot Snapshot() => new RuntimeStateStore(new(
        false,
        Offline: true,
        GameMode.Regular,
        "en",
        TimeSpan.FromHours(9),
        TimeSpan.FromMinutes(5))).Current;
}
