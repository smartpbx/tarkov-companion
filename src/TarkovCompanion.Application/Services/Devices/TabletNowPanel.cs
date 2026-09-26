using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>
/// [#712 0-11] The desktop's Now panel, for the paired tablet: the same words in the same block
/// order, already worded by the desktop.
/// </summary>
/// <remarks>
/// Rides on the map surface like <see cref="TabletLootResult"/>, so an older page, which reads only
/// the keys it knows, never sees it, and an older desktop simply never sends it.
///
/// Nothing here ticks. The desktop panel redraws every second, and a payload that changed every
/// second would republish the whole surface every second. So every number that moves with time
/// alone is sent as the stamp it is counted from, and the tablet counts it: the raid clock as
/// <see cref="TabletNowClock.AtUtc"/> in a "{clock}" slot, and each age as a stamp beside a text
/// that may hold an "{ago}" slot. What flips at a moment (late raid, the run-through note, a
/// verdict's focus, YOU going stale) is decided by the desktop and arrives as a change of its own.
///
/// Every string is clipped to <see cref="MaximumText"/> and every list is capped, so the worst case
/// stays inside the relay's own bound for this block (<c>RelayNowPanelBound</c>).
/// </remarks>
public sealed record TabletNowPanel(
    int Version,
    string Phase,
    string? MapName,
    string Tone,
    TabletNowBlock Now,
    TabletNowYou? You,
    TabletNowSquad? Squad,
    TabletNowNext? Next,
    TabletNowScan Scan,
    string? Because,
    TabletNowWords Words)
{
    /// <summary>The shape this build writes. A page renders what it knows of any version from 1 on.</summary>
    public const int CurrentVersion = 1;

    public const int MaximumText = 140;

    /// <summary>A squad is five at most in the game; eight leaves room without letting a list grow.</summary>
    public const int MaximumSquadRows = 8;

    /// <summary>The desktop's own four (NowPanelState.MaximumVerdictRows).</summary>
    public const int MaximumVerdictRows = 4;

    /// <summary>This panel with every string clipped and every list capped.</summary>
    public TabletNowPanel Bounded() => this with
    {
        Phase = Clip(Phase),
        MapName = ClipOrNull(MapName),
        Tone = Clip(Tone),
        Now = Now with
        {
            Heading = Clip(Now.Heading),
            Headline = Clip(Now.Headline),
            Clock = Now.Clock is { } clock ? clock with { Kind = Clip(clock.Kind), Basis = Clip(clock.Basis) } : null,
            Detail = Clip(Now.Detail),
            Note = Clip(Now.Note),
        },
        You = You is { } you
            ? you with
            {
                Heading = Clip(you.Heading),
                Where = Clip(you.Where),
                Age = Clip(you.Age),
                Exit = Clip(you.Exit),
                ExitNote = Clip(you.ExitNote),
            }
            : null,
        Squad = Squad is { } squad
            ? squad with
            {
                Heading = Clip(squad.Heading),
                Empty = Clip(squad.Empty),
                EmptyHint = Clip(squad.EmptyHint),
                Rows =
                [
                    .. squad.Rows.Take(MaximumSquadRows).Select(row => row with
                    {
                        Name = Clip(row.Name),
                        Where = Clip(row.Where),
                        Colour = ClipOrNull(row.Colour),
                    }),
                ],
            }
            : null,
        Next = Next is { } next
            ? next with
            {
                Heading = Clip(next.Heading),
                Label = Clip(next.Label),
                Detail = Clip(next.Detail),
                Then = Clip(next.Then),
                Hint = Clip(next.Hint),
            }
            : null,
        Scan = Scan with
        {
            Heading = Clip(Scan.Heading),
            Line = Clip(Scan.Line),
            Hint = Clip(Scan.Hint),
            Rows =
            [
                .. Scan.Rows.Take(MaximumVerdictRows).Select(row => row with
                {
                    Verdict = Clip(row.Verdict),
                    Word = Clip(row.Word),
                    Name = Clip(row.Name),
                    Reason = Clip(row.Reason),
                    Value = Clip(row.Value),
                }),
            ],
        },
        Because = ClipOrNull(Because),
        Words = new(
            Clip(Words.Seconds),
            Clip(Words.Minutes),
            Clip(Words.Ago),
            Clip(Words.Ping),
            Clip(Words.PingTip)),
    };

    /// <summary>
    /// Every stamp moved by <paramref name="correction"/>: the desktop's clock taken to the relay's,
    /// as the surface's own ping expiries are (#891). A PC four hours fast would otherwise show a
    /// raid four hours long on the tablet.
    /// </summary>
    public TabletNowPanel Shifted(TimeSpan correction) => correction == TimeSpan.Zero
        ? this
        : this with
        {
            Now = Now with
            {
                Clock = Now.Clock is { } clock ? clock with { AtUtc = clock.AtUtc + correction } : null,
                DetailSinceUtc = Now.DetailSinceUtc + correction,
            },
            You = You is { } you ? you with { SinceUtc = you.SinceUtc + correction } : null,
            Squad = Squad is { } squad ? squad with { Rows = [.. squad.Rows.Select(row => row with { SeenUtc = row.SeenUtc + correction })] } : null,
            Scan = Scan with { SinceUtc = Scan.SinceUtc + correction },
        };

    internal static string Clip(string? value) => TabletCaptureReview.Clip(value);

    private static string? ClipOrNull(string? value) => string.IsNullOrEmpty(value) ? null : Clip(value);
}

/// <summary>The raid clock as the anchor it is counted from, never as a number that was read live.</summary>
/// <param name="Kind">"left": <see cref="AtUtc"/> is when it reaches zero. "elapsed": when the raid started.</param>
/// <param name="Basis">Observed (read off the extract screen), Counted, or Elapsed: the latter two are counts.</param>
/// <param name="IsCounted">True unless the game's own screen said it: the tablet never shows it as the game's clock.</param>
public sealed record TabletNowClock(string Kind, DateTimeOffset AtUtc, string Basis, bool IsCounted);

/// <param name="Headline">"Not in raid", or "{clock} left" with the slot the tablet fills from <see cref="Clock"/>.</param>
/// <param name="Detail">The clock's basis; "{ago}" is filled from <see cref="DetailSinceUtc"/>.</param>
public sealed record TabletNowBlock(
    string Heading,
    string Headline,
    TabletNowClock? Clock,
    string Detail,
    DateTimeOffset? DetailSinceUtc,
    string Note,
    bool NoteIsDone);

/// <param name="Age">Shown as it is when <see cref="SinceUtc"/> is null ("Take one in game…"); else the tablet counts it.</param>
public sealed record TabletNowYou(
    string Heading,
    string Where,
    string Age,
    DateTimeOffset? SinceUtc,
    bool IsStale,
    string Exit,
    string ExitNote);

/// <param name="SeenUtc">When their last shared position was taken; the tablet shows its age. Null: no age to show.</param>
/// <param name="CanPing">The desktop would ping this row: in raid, placed, on a map.</param>
public sealed record TabletNowSquadRow(
    string Name,
    string Where,
    DateTimeOffset? SeenUtc,
    bool CanPing,
    bool IsAway,
    string? Colour);

public sealed record TabletNowSquad(string Heading, IReadOnlyList<TabletNowSquadRow> Rows, string Empty, string EmptyHint);

/// <param name="Label">Empty: no route open, and <see cref="Hint"/> says how to open one.</param>
public sealed record TabletNowNext(string Heading, string Label, string Detail, string Then, string Hint);

public sealed record TabletNowVerdictRow(string Verdict, string Word, string Name, string Reason, string Value);

/// <param name="Line">"Take 2 · Swap 1 · Leave 1", the last screen read, or "No scan yet".</param>
/// <param name="SinceUtc">When it was read; null when there is nothing.</param>
/// <param name="Rows">A fresh verdict's top rows: non-empty only while it has the room, as on the desktop.</param>
public sealed record TabletNowScan(
    string Heading,
    string Line,
    DateTimeOffset? SinceUtc,
    string Hint,
    IReadOnlyList<TabletNowVerdictRow> Rows);

/// <summary>The desktop's own words for an age, so the tablet counts in the desktop's language.</summary>
/// <param name="Seconds">"{0} s".</param>
/// <param name="Minutes">"{0} min".</param>
/// <param name="Ago">"{0} ago".</param>
/// <param name="PingTip">"Ping {0}'s last shared spot for the squad".</param>
public sealed record TabletNowWords(string Seconds, string Minutes, string Ago, string Ping, string PingTip);

/// <summary>The Now panel alone, as it sits inside the surface: for telling a change from a repeat.</summary>
public static class TabletNowPanelJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static byte[] Serialize(TabletNowPanel panel) => JsonSerializer.SerializeToUtf8Bytes(panel, Options);
}

/// <summary>
/// [#712 0-11] Lets a Now panel through only when it says something the last one did not.
/// </summary>
/// <remarks>
/// The desktop panel refreshes every second and the payload is built to be equal from one second to
/// the next (every count is an anchor), so this is what turns sixty refreshes a minute into the few
/// real changes a raid has. Compared as JSON, the form the tablet receives.
/// </remarks>
public sealed class TabletNowChangeGate
{
    private readonly Lock _gate = new();
    private byte[]? _last;

    /// <summary>True when <paramref name="panel"/> differs from the last one let through; none is where it starts.</summary>
    public bool Offer(TabletNowPanel? panel)
    {
        var content = panel is null ? null : TabletNowPanelJson.Serialize(panel);
        lock (_gate)
        {
            var same = content is null
                ? _last is null
                : _last is not null && content.AsSpan().SequenceEqual(_last);
            if (same)
            {
                return false;
            }

            _last = content;
            return true;
        }
    }
}
