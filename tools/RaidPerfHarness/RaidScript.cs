using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.RaidPerfHarness;

/// <summary>
/// A raid that can be replayed at any speed: where the player and each squadmate are at any
/// moment, as the screenshots and group exchanges that would have reported it.
/// </summary>
/// <remarks>
/// Positions are world positions found by probing the map's own transform, so they land on the
/// plan of whichever map is loaded rather than off its edge (the render preview's demos do the
/// same). Everybody walks a slow closed curve across the middle of the plan, each at their own
/// phase, and only moves when they "take a screenshot" — between shots the last position is
/// still the position, which is the whole reason a marker is drawn as a last-known place.
/// </remarks>
internal sealed class RaidScript
{
    private readonly List<(double X, double Z, double PlanX, double PlanY)> _candidates = [];
    private readonly string[] _names = ["Geo", "Riley", "Sam", "Alex", "Jordan", "Casey"];
    private readonly ScreenshotPosition?[] _last;
    private readonly List<GroupTrailPointView>[] _trails;
    private readonly int _members;

    public RaidScript(MapRenderModel model, int groupSize)
    {
        MapId = model.Location.Id;
        _members = Math.Max(1, groupSize);
        _last = new ScreenshotPosition?[_members];
        _trails = [.. Enumerable.Range(0, _members).Select(_ => new List<GroupTrailPointView>())];
        Probe(model);
    }

    public string MapId { get; }

    public bool HasGround => _candidates.Count > 0;

    /// <summary>Where a screenshot taken by member <paramref name="who"/> at raid second <paramref name="seconds"/> lands.</summary>
    public ScreenshotPosition Shot(int who, double seconds, DateTimeOffset now, int ordinal)
    {
        var phase = who * 1.1;
        var planX = 50 + 34 * Math.Sin((seconds / 210) + phase);
        var planY = 50 + 30 * Math.Cos((seconds / 290) + (phase * 1.7));
        var heading = (seconds / 210 * 57.29578 + who * 40) % 360;
        var shot = new ScreenshotPosition(
            now,
            At(planX, planY),
            default,
            heading,
            null,
            null,
            $"2026-09-18[12-00]_{ordinal}_{who}.png");
        _last[who] = shot;
        return shot;
    }

    /// <summary>What the relay would return for this room at <paramref name="now"/>: everybody but the local player.</summary>
    public GroupSnapshot Exchange(DateTimeOffset now, double raidSeconds, Random random)
    {
        var members = new List<GroupMemberView>(_members - 1);
        for (var who = 1; who < _members; who++)
        {
            var shot = _last[who];
            var trail = _trails[who];
            members.Add(new GroupMemberView(
                _names[who % _names.Length],
                MapId,
                RaidLifecycleState.InRaid,
                "PMC",
                shot?.Position,
                shot?.HeadingDegrees,
                shot is null ? null : now - shot.Timestamp,
                [],
                ["Delivery from the Past"])
            {
                // The relay keeps ten; a member is heard from every exchange or so.
                Since = TimeSpan.FromSeconds(random.NextDouble() * 4),
                Trail = [.. trail.TakeLast(10)],
            });
        }

        var waypoints = new List<GroupWaypointView>();
        for (var index = 0; index < 3; index++)
        {
            var at = At(25 + (index * 22), 35 + (index * 12));
            waypoints.Add(new GroupWaypointView(index + 1, _names[index % _names.Length], MapId, at.X, 0, at.Z, index == 0 ? "Dorms" : null, null)
            {
                CreatedUtc = now.AddMinutes(-6 + index),
            });
        }

        var ping = At(55, 30);
        return new GroupSnapshot(
            true,
            members,
            $"Sharing as Clay · {_members - 1} others here",
            now)
        {
            Waypoints = waypoints,
            Pings = raidSeconds % 300 < 60
                ? [new GroupPingView(99, "Sam", MapId, ping.X, 0, ping.Z, null, now.AddSeconds(-(raidSeconds % 300)))]
                : [],
        };
    }

    /// <summary>Records a member's shot so later exchanges carry it in their trail.</summary>
    public void Remember(int who, ScreenshotPosition shot)
    {
        if (who == 0)
        {
            return;
        }

        _trails[who].Add(new GroupTrailPointView(shot.Position.X, shot.Position.Z, TimeSpan.Zero));
        if (_trails[who].Count > 10)
        {
            _trails[who].RemoveAt(0);
        }
    }

    private WorldPosition At(double planX, double planY)
    {
        if (_candidates.Count == 0)
        {
            return new(0, 0, 0);
        }

        var best = _candidates.MinBy(item => Math.Pow(item.PlanX - planX, 2) + Math.Pow(item.PlanY - planY, 2));
        return new(best.X, 0, best.Z);
    }

    private void Probe(MapRenderModel model)
    {
        if (MapPlanProjection.For(model) is not { IsValid: true } rect)
        {
            return;
        }

        for (var x = -1200.0; x <= 1200; x += 10)
        {
            for (var z = -1200.0; z <= 1200; z += 10)
            {
                if (!model.TryMapPosition(new(x, 0, z), out var point))
                {
                    continue;
                }

                var planX = (point.X - rect.MinimumX) / rect.Width * 100;
                var planY = (point.Y - rect.MinimumY) / rect.Height * 100;
                if (double.IsFinite(planX) && double.IsFinite(planY) && planX is > 4 and < 96 && planY is > 4 and < 96)
                {
                    _candidates.Add((x, z, planX, planY));
                }
            }
        }
    }
}
