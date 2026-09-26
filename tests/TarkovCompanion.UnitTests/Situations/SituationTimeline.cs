using System.Globalization;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.UnitTests.Situations;

/// <summary>
/// The fixture-timeline harness (ADR 0022): synthetic log lines, screenshot names, scans, relay
/// states and clock ticks go in, in order, through the same parsers and raid state the app uses;
/// the situation after each step comes out.
/// </summary>
/// <remarks>
/// Every line is synthetic. The shapes follow the real 2026-09-23..25 logs; the ids, profile ids,
/// addresses and account ids are made up, because the repository is public.
/// </remarks>
internal sealed class SituationTimeline : IDisposable
{
    public const string PmcProfile = "PMCPROFILE0000000000001";
    public const string ScavProfile = "SCAVPROFILE000000000001";

    /// <summary>The player's PC runs four hours behind UTC, like the owner's.</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test-4", TimeSpan.FromHours(-4), "test-4", "test-4");

    private readonly EftLogParser _parser = new();
    private readonly RaidStateService _raid = new();
    private readonly SquadStateService _squad = new();
    private readonly RuntimeStateStore _runtime;
    private readonly IDisposable _zone;

    public SituationTimeline()
    {
        _zone = LocalTime.UseZone(Zone);
        Clock = new ManualClock(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
        _runtime = new RuntimeStateStore(new(false, true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5)), Clock);
        _runtime.Update(state => state with
        {
            Observation = new EftObservationState(true, true, true, "Logs", "Screenshots", Confidence.Certain, "watching"),
        });
        Places = new FakePlaces();
        Service = new SituationService(_runtime, Clock, Places, refresh: TimeSpan.Zero);
        Service.Changed += (_, change) => Seen.Add(change.Current);
    }

    public ManualClock Clock { get; }

    public FakePlaces Places { get; }

    public SituationService Service { get; }

    public List<Situation> Seen { get; } = [];

    public Situation Now => Service.Current;

    /// <summary>The phases the situation passed through, each once per stretch.</summary>
    public IReadOnlyList<SituationPhase> Phases =>
        Seen.Select(situation => situation.Phase.Value)
            .Where((phase, index) => index == 0 || Seen[index - 1].Phase.Value != phase)
            .ToArray();

    public static string AppLine(string stamp, string text) => $"{stamp}|1.1.5.1.47510|Info|application|{text}";

    public static string Notification(string stamp, string type, string status, string location, string shortId, string profile) =>
        $"{stamp}|1.1.5.1.47510|Info|backend|WebSocketSharp - message received: NOTIFICATION [EV-{shortId}-{type}] {type} " +
        "[{\"type\":\"" + type + "\",\"eventId\":\"EV-" + shortId + "-" + type + "\",\"profileid\":\"" + profile +
        "\",\"status\":\"" + status + "\",\"location\":\"" + location +
        "\",\"raidMode\":\"Online\",\"mode\":\"deathmatch\",\"shortId\":\"" + shortId + "\"}]";

    public static string ProfileStatus(string stamp, string location, string shortId, string profile) =>
        $"{stamp}|1.1.5.1.47510|Debug|application|TRACE-NetworkGameCreate profileStatus: " +
        $"'Profileid: {profile}, Status: Busy, RaidMode: Online, Ip: 0.0.0.0, Port: 17000, Location: {location}, " +
        $"Sid: SID, GameMode: deathmatch, shortId: {shortId}'";

    public static string ProfileReload(string stamp) =>
        AppLine(stamp, $"CompleteSelectedProfile ProfileId:{PmcProfile} AccountId:1");

    /// <summary>One line of the game's log, read at the moment it was written.</summary>
    public SituationTimeline Log(string line)
    {
        var written = RaidReplayDecision.WrittenUtc(line, Zone) ?? throw new ArgumentException("No stamp.", nameof(line));
        Clock.Now = written;
        if (_parser.ParseLine(line, written) is { } evidence)
        {
            _raid.Apply(evidence);
            Publish();
        }

        if (GroupNotificationParser.ParseLine(line, written) is { } group)
        {
            _squad.Apply(group);
            _runtime.Update(state => state with { Squad = _squad.Current });
        }

        if (RaidPhaseMarkerParser.ParseLine(line, written) is { } marker)
        {
            Service.Observe(marker);
        }

        return this;
    }

    public SituationTimeline Logs(params string[] lines)
    {
        foreach (var line in lines)
        {
            Log(line);
        }

        return this;
    }

    /// <summary>A screenshot the game named, at the PC time in its name.</summary>
    public SituationTimeline Screenshot(string localStamp, double x, double y, double z, double headingDegrees)
    {
        var local = DateTime.ParseExact(localStamp, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Clock.Now = new DateTimeOffset(local, Zone.BaseUtcOffset).ToUniversalTime();
        var position = new ScreenshotPosition(
            Clock.Now,
            new WorldPosition(x, y, z),
            new QuaternionOrientation(0, 0, 0, 1),
            headingDegrees,
            null,
            null,
            $"{local:yyyy-MM-dd[HH-mm]}_{x}, {y}, {z}_0, 0, 0, 1_0.00 (0).png");
        _raid.ApplyPosition(position);
        Publish();
        return this;
    }

    /// <summary>The same screenshot parsed from its real-shaped name, so the filename path is covered too.</summary>
    public SituationTimeline ScreenshotNamed(string filename)
    {
        var parser = new ScreenshotFilenameParser(Clock);
        Assert.True(parser.TryParse(filename, Zone.BaseUtcOffset, out var position));
        Clock.Now = position!.Timestamp;
        _raid.ApplyPosition(position);
        Publish();
        return this;
    }

    /// <summary>The extract screen photographed with the raid clock on it.</summary>
    public SituationTimeline ExtractScreen(TimeSpan raidClock)
    {
        _raid.ApplyExtracts([], Clock.Now, raidClock);
        Publish();
        return Scan(ScanContext.ExtractList);
    }

    public SituationTimeline Scan(ScanContext context)
    {
        Service.Observe(new ScanOutcome(
            Guid.NewGuid(),
            ScanCompletionStatus.Complete,
            context,
            Clock.Now,
            new RecognitionResult(context, [], Clock.Now),
            null,
            null,
            null,
            null,
            []));
        return this;
    }

    public SituationTimeline Relay(params GroupMemberView[] members)
    {
        _runtime.Update(state => state with { Group = new GroupSnapshot(true, members, "sharing", Clock.Now) });
        return this;
    }

    public static GroupMemberView Member(string name, string? map, RaidLifecycleState state, WorldPosition? position, TimeSpan? age) =>
        new(name, map, state, "PMC", position, 90, age, [], []);

    public SituationTimeline Outcome(SituationOutcome outcome)
    {
        var raidId = Now.RaidId ?? throw new InvalidOperationException("No raid to answer for.");
        Service.ReportOutcome(raidId, new(outcome, Confidence.Certain, SituationSource.Player, Clock.Now, $"You answered {outcome}."));
        return this;
    }

    /// <summary>Time passes with nothing written.</summary>
    public SituationTimeline At(string localStamp)
    {
        var local = DateTime.Parse(localStamp, CultureInfo.InvariantCulture);
        Clock.Now = new DateTimeOffset(local, Zone.BaseUtcOffset).ToUniversalTime();
        Service.Refresh();
        return this;
    }

    /// <summary>The PC clock is set mid-session (#891), and the app's jump detector rebases what it holds.</summary>
    public SituationTimeline StepClock(TimeSpan jump)
    {
        Clock.Now += jump;
        _raid.RebaseClock(jump);
        Publish();
        Service.RebaseClock(jump);
        return this;
    }

    public void Dispose()
    {
        Service.Dispose();
        _zone.Dispose();
    }

    private void Publish() => _runtime.Update(state => state with { Raid = _raid.Current });

    internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    /// <summary>Customs cut in two at x = 0: Dorms to the west, Old Gas Station to the east.</summary>
    internal sealed class FakePlaces : ISituationPlaces
    {
        public event EventHandler? Changed;

        /// <summary>Answers "the catalog just arrived" from inside the question, as a cached load once did.</summary>
        public bool RaiseOnDescribe { get; set; }

        public SituationPlace Describe(string mapId, WorldPosition position)
        {
            if (RaiseOnDescribe)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return Name(mapId, position);
        }

        private static SituationPlace Name(string mapId, WorldPosition position) =>
            string.Equals(mapId, "customs", StringComparison.OrdinalIgnoreCase)
                ? new(position.X < 0 ? "Dorms" : "Old Gas Station", position.Y > 3 ? "2nd floor" : "Ground floor")
                : new(null, null);

        public TimeSpan? RaidLength(string mapId, string? side) =>
            RaidTimer.LengthFor(side, TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(25));

        public string? MapName(string mapId) => mapId switch
        {
            "customs" => "Customs",
            "lighthouse" => "Lighthouse",
            "the-lab" => "The Lab",
            _ => null,
        };

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
