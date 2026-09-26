using TarkovCompanion.Application.Services.Group;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>Which side of the map a squad ping lies toward, as the player sees the map now.</summary>
public enum MapEdge
{
    None,
    Top,
    Right,
    Bottom,
    Left,
}

/// <summary>
/// [#712 0-5] Which of the group's pings are new, and which squadmate sent each one.
/// </summary>
/// <remarks>
/// A ping counts only once it arrives after the first look: the pings already standing when the
/// page opened (or the app started mid-session) are not news, and pulsing every one of them at
/// once would say nothing. Only a ping whose sender is a relay member counts, so this player's own
/// pings (forwarded marks, or a ping from a SQUAD row, which carry this player's name) never light
/// anything. Everything here came from the squadmates' own companions through the relay.
/// </remarks>
internal sealed class SquadPingWatch
{
    private HashSet<long>? _seen;

    public IReadOnlyList<GroupPingView> Arrived(GroupSnapshot group, Func<long, bool>? isOwn = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        var ids = group.Pings.Select(ping => ping.Id).ToHashSet();
        if (_seen is null)
        {
            _seen = ids;
            return [];
        }

        var arrived = group.Pings
            .Where(ping => !_seen.Contains(ping.Id))
            .Where(ping => isOwn?.Invoke(ping.Id) != true)
            .Where(ping => group.Members.Any(member => string.Equals(member.Name, ping.By, StringComparison.Ordinal)))
            .ToArray();
        _seen = ids;
        return arrived;
    }

    /// <summary>
    /// The map edge nearest the direction from <paramref name="fromX"/>,<paramref name="fromY"/>
    /// (the player, or the view's centre) to the ping, both in viewport pixels, so a turned or panned
    /// map lights the side the player will look toward.
    /// </summary>
    public static MapEdge EdgeToward(double fromX, double fromY, double toX, double toY, double width, double height)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || width <= 0 || height <= 0 || (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5))
        {
            return MapEdge.None;
        }

        // Scaled by the half-extents: on a wide map a ping 45° up-right is nearer the top edge.
        var across = dx / (width / 2);
        var down = dy / (height / 2);
        return Math.Abs(across) >= Math.Abs(down)
            ? across > 0 ? MapEdge.Right : MapEdge.Left
            : down > 0 ? MapEdge.Bottom : MapEdge.Top;
    }
}

/// <summary>[#712 0-5] The map edge that lights for two seconds toward a squadmate's new ping.</summary>
public sealed class SquadEdgePulseViewModel : BindableViewModel, IDisposable
{
    /// <summary>#712 T3: "pulses the map edge nearest its bearing for two seconds".</summary>
    public static readonly TimeSpan PulseLength = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _clock;
    private readonly Action<Action> _post;
    private MapEdge _edge;
    private string _colour = "#E0B45C";
    private ITimer? _end;

    public SquadEdgePulseViewModel(TimeProvider clock, Action<Action> post)
    {
        _clock = clock;
        _post = post;
    }

    public MapEdge Edge
    {
        get => _edge;
        private set
        {
            if (SetProperty(ref _edge, value))
            {
                OnPropertyChanged(nameof(IsTop));
                OnPropertyChanged(nameof(IsRight));
                OnPropertyChanged(nameof(IsBottom));
                OnPropertyChanged(nameof(IsLeft));
            }
        }
    }

    public bool IsTop => _edge == MapEdge.Top;

    public bool IsRight => _edge == MapEdge.Right;

    public bool IsBottom => _edge == MapEdge.Bottom;

    public bool IsLeft => _edge == MapEdge.Left;

    /// <summary>The squadmate's own map colour, so the edge matches their dot and their row.</summary>
    public string Colour
    {
        get => _colour;
        private set => SetProperty(ref _colour, value);
    }

    public void Pulse(MapEdge edge, string? colour)
    {
        if (edge == MapEdge.None)
        {
            return;
        }

        Colour = colour ?? "#E0B45C";
        Edge = edge;
        _end?.Dispose();
        _end = _clock.CreateTimer(_ => _post(() => Edge = MapEdge.None), null, PulseLength, Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _end?.Dispose();
}
