using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.Application.Services.Ask;

/// <summary>What kind of thing produces an answer.</summary>
public enum AnswerSourceKind
{
    /// <summary>The deterministic grammar over the catalog, the profile and the situation.</summary>
    Rules,

    /// <summary>
    /// A model running on this PC (decision 3, 2026-09-25): off by default, turned on in a setting,
    /// and never sent anything off the machine. No such source ships yet; this is its seam.
    /// </summary>
    LocalModel,
}

/// <summary>
/// Something that can answer a typed question. The rules source always exists; a local model can be
/// added behind a setting without the Ask box changing.
/// </summary>
/// <remarks>
/// A source returns an answer card or, where it cannot answer, an <see cref="AskAnswer.Cannot"/>
/// card that says why and lists the closest names it knows. It never returns an invented answer:
/// the rules source only reads the app's data, and a local model's card is labelled
/// <see cref="AskSourceKind.LocalModel"/> so the player can tell the two apart.
/// </remarks>
public interface IAnswerSource
{
    AnswerSourceKind Kind { get; }

    Task<AskAnswer> AnswerAsync(string question, CancellationToken cancellationToken);
}

/// <summary>The raid facts an extract question needs, from the Raid map and the situation.</summary>
/// <param name="MapName">The map the exits are listed for, as the catalog names it.</param>
/// <param name="Side">The side the exits were filtered for.</param>
/// <param name="PositionTakenUtc">When the screenshot that placed the player was taken; null before one.</param>
/// <param name="AreaName">Where that screenshot put them, in words, where the catalog names the place.</param>
public sealed record AskRaidSnapshot(
    string MapName,
    SituationSide Side,
    DateTimeOffset? PositionTakenUtc,
    string? AreaName,
    IReadOnlyList<AskExit> Exits);

/// <summary>One exit: straight-line distance and bearing from the player, and what it needs.</summary>
/// <param name="WasOffered">A scan of the extract screen named it this raid.</param>
public sealed record AskExit(
    string Name,
    double? Metres,
    string Bearing,
    bool WasOffered,
    bool IsTransit,
    MapExtractRequirements? Requirements);

/// <summary>Supplies the Raid map's current exits to the Ask box; null when no raid map is open.</summary>
public interface IAskRaidSource
{
    AskRaidSnapshot? Current();
}

/// <summary>Whether the optional local model may be asked (off by default).</summary>
public interface IAskSettings
{
    bool LocalModelEnabled { get; }
}

/// <summary>
/// The Ask box's one entry: the rules first, then a local model only when one is registered and
/// the player turned it on.
/// </summary>
public sealed class AskService(IEnumerable<IAnswerSource> sources, IAskSettings? settings = null)
{
    private readonly IReadOnlyList<IAnswerSource> _sources = [.. sources ?? throw new ArgumentNullException(nameof(sources))];

    public async Task<AskAnswer> AskAsync(string question, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return AskAnswer.Cannot(AskUnanswered.NotUnderstood);
        }

        AskAnswer? first = null;
        foreach (var source in _sources.Where(source => source.Kind == AnswerSourceKind.Rules))
        {
            var answer = await source.AnswerAsync(question, cancellationToken).ConfigureAwait(false);
            if (answer.IsAnswered)
            {
                return answer;
            }

            first ??= answer;
        }

        // Only a question the rules did not understand goes further: a question they understood
        // and found no match for ("Gunsmith 99") has its honest answer already.
        if (settings?.LocalModelEnabled == true && first is null or { Unanswered: AskUnanswered.NotUnderstood })
        {
            foreach (var source in _sources.Where(source => source.Kind == AnswerSourceKind.LocalModel))
            {
                var answer = await source.AnswerAsync(question, cancellationToken).ConfigureAwait(false);
                if (answer.IsAnswered)
                {
                    return answer with { Sources = [.. answer.Sources.Where(item => item.Kind != AskSourceKind.LocalModel), new(AskSourceKind.LocalModel)] };
                }
            }
        }

        return first ?? AskAnswer.Cannot(AskUnanswered.NotUnderstood);
    }
}
