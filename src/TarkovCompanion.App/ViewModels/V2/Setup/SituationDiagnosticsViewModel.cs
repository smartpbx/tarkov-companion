using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// [#712 0-2] Setup › Updates &amp; Diagnostics, developer mode only: the current situation and why.
/// </summary>
/// <remarks>
/// Raw on purpose. This is where a wrong automatic switch is diagnosed, so it prints every fact
/// with its source, confidence and "because", and the last phase changes, rather than the human
/// units the Now panel (0-4) will use.
/// </remarks>
public sealed class SituationDiagnosticsViewModel : BindableViewModel
{
    private readonly SituationService _situation;
    private readonly TimeProvider _clock;
    private readonly Action<Action> _post;
    private IReadOnlyList<string> _lines = [];
    private IReadOnlyList<string> _transitions = [];

    public SituationDiagnosticsViewModel(SituationService situation, TimeProvider? clock, Action<Action>? post)
    {
        _situation = situation ?? throw new ArgumentNullException(nameof(situation));
        _clock = clock ?? TimeProvider.System;
        _post = post ?? (action => action());
        _situation.Changed += (_, _) => _post(Refresh);
        Refresh();
    }

    public string Heading => "Situation";

    public IReadOnlyList<string> Lines
    {
        get => _lines;
        private set => SetProperty(ref _lines, value);
    }

    public IReadOnlyList<string> Transitions
    {
        get => _transitions;
        private set
        {
            if (SetProperty(ref _transitions, value))
            {
                OnPropertyChanged(nameof(TransitionsText));
            }
        }
    }

    /// <summary>One block of text: a list of equal strings re-rendered as a list drew a row twice.</summary>
    public string TransitionsText => string.Join(Environment.NewLine, _transitions);

    public void Refresh()
    {
        Lines = Describe(_situation.Current, _clock.GetUtcNow());
        Transitions = [.. _situation.Transitions.Reverse().Take(8).Select(transition =>
            $"v{transition.Version} {LocalTime.Time(transition.AtUtc)} {transition.From} → {transition.To}: {transition.Because}")];
    }

    internal static IReadOnlyList<string> Describe(Situation situation, DateTimeOffset nowUtc)
    {
        var lines = new List<string>
        {
            $"v{situation.Version} · {Fact(situation.Phase, nowUtc)}",
        };
        if (situation.Map is { } map)
        {
            lines.Add($"Map · {Fact(map, nowUtc)}");
        }

        if (situation.Side is { } side)
        {
            lines.Add($"Side · {Fact(side, nowUtc)}");
        }

        if (situation.Outcome is { } outcome)
        {
            lines.Add($"Outcome · {Fact(outcome, nowUtc)}");
        }

        if (situation.Profile is { } profile)
        {
            lines.Add($"Profile · {profile.Value}");
        }

        if (situation.Clock is { } clock)
        {
            var left = clock.RemainingAt(nowUtc) is { } remaining ? $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00} left" : "no time left known";
            lines.Add($"Clock · {left} · {clock.Basis}{(clock.IsInferred ? " (modelled)" : string.Empty)} · {clock.Because}");
        }

        if (situation.You is { } you)
        {
            lines.Add($"You · {you.AreaName ?? "area unknown"} · {you.FloorName ?? "floor unknown"} · facing {you.Facing ?? "?"} · {Age(you.AgeAt(nowUtc))} old · {you.Because}");
        }

        foreach (var member in situation.Squad)
        {
            var where = member.DistanceMetres is { } metres ? $" · {metres:0} m {member.Compass}" : string.Empty;
            var age = member.AgeAt(nowUtc) is { } memberAge ? $" · {Age(memberAge)} old" : string.Empty;
            lines.Add($"Squad · {member.Name} · {member.State} · {member.AreaName ?? member.MapId ?? "?"}{where}{age}");
        }

        if (situation.Next is { } next)
        {
            lines.Add($"Next · {next.Label}{(next.DistanceMetres is { } distance ? $" · {distance:0} m" : string.Empty)} · {next.Because}");
        }

        if (situation.Then is { } then)
        {
            lines.Add($"Then · {then.Label}");
        }

        if (situation.LastScan is { } scan)
        {
            lines.Add($"Last scan · {scan.Kind} · {scan.Headline ?? $"{scan.ItemCount} item(s)"} · {scan.Action ?? "no verdict"} · {scan.Because}");
        }

        return lines;
    }

    private static string Fact<T>(SituationFact<T> fact, DateTimeOffset nowUtc) =>
        $"{fact.Value} · {fact.Source}{(fact.IsInferred ? " (inferred)" : string.Empty)} · {fact.Confidence.Value:0.00} · {Age(fact.AgeAt(nowUtc))} ago · {fact.Because}";

    private static string Age(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? $"{age.TotalSeconds:0} s"
        : age < TimeSpan.FromHours(1) ? $"{age.TotalMinutes:0} min"
        : $"{age.TotalHours:0.#} h";
}
