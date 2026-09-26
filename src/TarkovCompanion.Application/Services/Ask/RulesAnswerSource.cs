using System.Globalization;
using System.Text.RegularExpressions;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.Application.Services.Ask;

/// <summary>
/// Answers the Ask box from the app's own data only: the synced catalog, the player's recorded
/// progress, and the Raid map's exits (#712 T8, decision 3).
/// </summary>
/// <remarks>
/// <para>
/// Every fact here is read through a service a page already uses (Intel's item card, the quest
/// board, the hideout requirement catalog, Crafts &amp; barters, the Raid map's extract list), so an
/// answer and the page its link opens can never disagree. Nothing is estimated that a page does not
/// already estimate: the armour rating and straight-line distances are the ones Intel and Raid show,
/// and the card labels them modelled.
/// </para>
/// <para>
/// When the words fit no question, or name nothing in the catalog, the answer says so and offers the
/// closest names; it never picks a weak match and answers as if it were sure.
/// </para>
/// </remarks>
public sealed partial class RulesAnswerSource(
    IItemRepository items,
    IItemIntelService? intel = null,
    IQuestReadService? quests = null,
    IPlayerProfileService? profiles = null,
    IRequirementCatalog? requirements = null,
    IHideoutPrerequisiteCatalog? prerequisites = null,
    IIntelTradeCatalogService? trades = null,
    IItemFactCatalog? facts = null,
    IItemAcquisitionService? acquisitions = null,
    IAskRaidSource? raid = null) : IAnswerSource
{
    /// <summary>How many rows of one kind a card lists before "and N more".</summary>
    private const int RowLimit = 5;

    /// <summary>An item hit below this is offered as a closest match, not answered about.</summary>
    private const double ItemAccept = 0.8;

    public AnswerSourceKind Kind => AnswerSourceKind.Rules;

    public async Task<AskAnswer> AnswerAsync(string question, CancellationToken cancellationToken)
    {
        var parsed = AskGrammar.Parse(question);
        if (parsed is null)
        {
            return AskAnswer.Cannot(AskUnanswered.NotUnderstood, closest: await ClosestAnywhereAsync(question, cancellationToken).ConfigureAwait(false));
        }

        return parsed.Intent switch
        {
            AskIntent.Needs => await NeedsAsync(parsed, cancellationToken).ConfigureAwait(false),
            AskIntent.ItemUses => await ItemUsesAsync(parsed, cancellationToken).ConfigureAwait(false),
            AskIntent.ItemSources => await ItemSourcesAsync(parsed, cancellationToken).ConfigureAwait(false),
            AskIntent.BestExtract => BestExtract(),
            AskIntent.Ammo => await AmmoAsync(parsed, cancellationToken).ConfigureAwait(false),
            _ => AskAnswer.Cannot(AskUnanswered.NotUnderstood),
        };
    }

    // ---------------------------------------------------------------- quests and hideout levels

    private async Task<AskAnswer> NeedsAsync(AskQuestion question, CancellationToken cancellationToken)
    {
        var (stationWords, level) = SplitLevel(question.Subject);
        var stations = requirements is null
            ? []
            : await requirements.GetStationsAsync(cancellationToken).ConfigureAwait(false);
        var stationMatches = AskNameMatcher.Rank(stationWords, stations, station => station.Name);
        var board = await BoardAsync(cancellationToken).ConfigureAwait(false);
        var tasks = board?.Tasks ?? [];
        var questMatches = AskNameMatcher.Rank(question.Subject, tasks, task => task.Name);
        var bestStation = stationMatches.FirstOrDefault();
        var bestQuest = questMatches.FirstOrDefault();

        // A station wins a tie: "Workbench 2" is the hideout even if a quest name has the word in it.
        if (bestStation is { Score: >= AskNameMatcher.Accept } && (bestQuest is null || bestStation.Score >= bestQuest.Score))
        {
            return await HideoutAsync(bestStation.Value, level, cancellationToken).ConfigureAwait(false);
        }

        if (bestQuest is { Score: >= AskNameMatcher.Accept } best)
        {
            var also = questMatches.Skip(1).Where(match => match.Score >= best.Score - 0.05).Select(match => match.Name).Take(3).ToArray();
            return Quest(best.Value, tasks, board!.CatalogProvenance) with { ClosestMatches = also };
        }

        if (tasks.Count == 0 && stations.Count == 0)
        {
            return AskAnswer.Cannot(AskUnanswered.NoData, AskIntent.Needs, question.Subject);
        }

        var closest = questMatches.Take(3).Select(match => (match.Name, match.Score))
            .Concat(stationMatches.Take(2).Select(match => (match.Name, match.Score)))
            .Where(match => match.Score > 0.1)
            .OrderByDescending(match => match.Score)
            .Select(match => match.Name)
            .Take(4)
            .ToArray();
        return AskAnswer.Cannot(AskUnanswered.NoMatch, AskIntent.Needs, question.Subject, closest);
    }

    private AskAnswer Quest(QuestSummaryReadModel task, IReadOnlyList<QuestSummaryReadModel> tasks, QuestCatalogProvenance? provenance)
    {
        var lines = new List<Phrase>();
        if (task.TraderName is { Length: > 0 } trader)
        {
            lines.Add(task.MinimumPlayerLevel is > 0 and var minimum
                ? new Phrase(AskLine.QuestGiverLevel, trader, minimum)
                : new Phrase(AskLine.QuestGiver, trader));
        }

        var usedProfile = false;
        if (task.RecordedState == RecordedTaskState.Completed)
        {
            lines.Add(new(AskLine.QuestFinished));
            usedProfile = true;
        }
        else if (task.Eligibility.State == QuestEligibilityState.Locked)
        {
            lines.Add(new(AskLine.QuestLocked));
            usedProfile = true;
        }

        var names = tasks.ToDictionary(item => item.TaskId, item => item.Name, StringComparer.Ordinal);
        foreach (var prerequisite in task.Prerequisites.Take(2))
        {
            lines.Add(new(AskLine.QuestAfter, names.GetValueOrDefault(prerequisite.RequiredTaskId, prerequisite.RequiredTaskId)));
        }

        AddLimited(lines, task.Objectives.Where(objective => objective.IsOptional != true), Objective);
        if (task.KappaRequired == true)
        {
            lines.Add(new(AskLine.QuestKappa));
        }

        IReadOnlyList<AskSource> sources = usedProfile
            ? [new(AskSourceKind.Catalog, provenance?.FetchedUtc), new(AskSourceKind.Profile)]
            : [new(AskSourceKind.Catalog, provenance?.FetchedUtc)];
        return new(AskIntent.Needs, task.Name, lines, sources, [new(AskLinkKind.PlanQuest, task.Name, task.Name)]);

        static Phrase Objective(QuestObjectiveReadModel objective)
        {
            var counts = objective.Kind is QuestObjectiveKind.FindItem or QuestObjectiveKind.GiveItem or
                QuestObjectiveKind.FindQuestItem or QuestObjectiveKind.GiveQuestItem or QuestObjectiveKind.Shoot or
                QuestObjectiveKind.PlantItem or QuestObjectiveKind.SellItem;
            if (counts && objective.TargetCount is > 1 and var count)
            {
                return objective.FoundInRaidRequired == true
                    ? new Phrase(AskLine.QuestObjectiveCountFir, objective.Description, (long)count)
                    : new Phrase(AskLine.QuestObjectiveCount, objective.Description, (long)count);
            }

            return new Phrase(AskLine.QuestObjective, objective.Description);
        }
    }

    private async Task<AskAnswer> HideoutAsync(HideoutStationSummary station, int? askedLevel, CancellationToken cancellationToken)
    {
        var profile = await ProfileAsync(cancellationToken).ConfigureAwait(false);
        var built = profile?.HideoutStationLevels.GetValueOrDefault(station.StationId) ?? 0;
        var maximum = station.Levels.Count == 0 ? 1 : station.Levels.Max();
        var level = askedLevel ?? Math.Min(built + 1, maximum);
        var lines = new List<Phrase> { new(AskLine.HideoutLevel, level) };
        if (!station.Levels.Contains(level) && station.Levels.Count > 0)
        {
            return AskAnswer.Cannot(
                AskUnanswered.NoMatch,
                AskIntent.Needs,
                $"{station.Name} {level}",
                [.. station.Levels.Order().Select(item => $"{station.Name} {item}")]);
        }

        if (profile is not null && built >= level)
        {
            lines.Add(new(AskLine.HideoutBuilt, built));
        }

        var wanted = (await requirements!.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(false))
            .Where(requirement => requirement.StationId == station.StationId && requirement.TargetLevel == level)
            .ToArray();
        DateTimeOffset? asOf = null;
        var itemLines = new List<(string Name, int Count)>();
        foreach (var requirement in wanted)
        {
            var item = await items.GetAsync(requirement.ItemId, cancellationToken).ConfigureAwait(false);
            asOf ??= item?.Provenance.SourceUpdatedUtc ?? item?.Provenance.ObservedUtc;
            itemLines.Add((item?.Name ?? requirement.ItemId, requirement.Required));
        }

        AddLimited(lines, itemLines.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase), row => new Phrase(AskLine.HideoutItem, row.Name, row.Count));
        if (prerequisites is not null)
        {
            var needs = await prerequisites.GetAsync(cancellationToken).ConfigureAwait(false);
            var stationNames = (await requirements.GetStationsAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(item => item.StationId, item => item.Name, StringComparer.Ordinal);
            // The station's own previous level goes without saying.
            foreach (var other in needs.Stations.Where(item => item.StationId == station.StationId && item.TargetLevel == level && item.RequiredStationId != station.StationId))
            {
                lines.Add(new(AskLine.HideoutStation, stationNames.GetValueOrDefault(other.RequiredStationId, other.RequiredStationId), other.RequiredLevel));
            }

            foreach (var other in needs.Others.Where(item => item.StationId == station.StationId && item.TargetLevel == level))
            {
                lines.Add(new(AskLine.HideoutOther, other.Label));
            }
        }

        IReadOnlyList<AskSource> sources = profile is not null && askedLevel is null
            ? [new(AskSourceKind.Catalog, asOf), new(AskSourceKind.Profile)]
            : [new(AskSourceKind.Catalog, asOf)];
        return new(AskIntent.Needs, station.Name, lines, sources, [new(AskLinkKind.PlanHideout, station.StationId, station.Name)]);
    }

    /// <summary>"Lavatory 2", "lavatory level 2": the station's words and the level, where one ends the text.</summary>
    internal static (string Words, int? Level) SplitLevel(string subject)
    {
        var match = TrailingLevel().Match(subject.Trim());
        return match.Success && int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var level)
            ? (subject[..match.Index].Trim(), level)
            : (subject, null);
    }

    // ---------------------------------------------------------------- items

    private async Task<AskAnswer> ItemUsesAsync(AskQuestion question, CancellationToken cancellationToken)
    {
        var (item, others) = await ResolveItemAsync(question.Subject, cancellationToken).ConfigureAwait(false);
        if (item is null || intel is null)
        {
            return AskAnswer.Cannot(item is null ? AskUnanswered.NoMatch : AskUnanswered.NoData, AskIntent.ItemUses, question.Subject, others);
        }

        var card = await intel.GetAsync(item.Id, cancellationToken).ConfigureAwait(false);
        var lines = new List<Phrase>();
        var needed = false;
        if (card.Key is { } key)
        {
            var locks = string.Join(", ", key.Locks.Where(value => !string.IsNullOrWhiteSpace(value)).Take(3));
            if (locks.Length > 0)
            {
                lines.Add(MapWord(key.MapName) is { } map
                    ? new Phrase(AskLine.KeyOpensOnMap, locks, map)
                    : new Phrase(AskLine.KeyOpens, locks));
            }
            else if (MapWord(key.MapName) is { } map)
            {
                lines.Add(new(AskLine.KeyOpensOnMap, item.ShortName, map));
            }

            if (key.MaximumUses is > 0 and var uses)
            {
                lines.Add(new(AskLine.KeyUses, uses));
            }

            foreach (var task in await KeyQuestNamesAsync(item.Id, cancellationToken).ConfigureAwait(false))
            {
                lines.Add(new(AskLine.UseKeyQuest, task));
                needed = true;
            }
        }

        if (card.Keep is { } keep)
        {
            AddLimited(lines, keep.Quests, row => row.FoundInRaidRequired
                ? new Phrase(AskLine.UseQuestFir, row.TaskName, (long)(row.Remaining ?? 1))
                : new Phrase(AskLine.UseQuest, row.TaskName, (long)(row.Remaining ?? 1)));
            AddLimited(lines, keep.Hideout, row => new Phrase(AskLine.UseHideout, row.StationName, row.TargetLevel, row.Remaining));
            needed |= keep.Quests.Count > 0 || keep.Hideout.Count > 0;
            if (!needed)
            {
                lines.Add(new(AskLine.NotNeeded));
            }
        }

        var tradeUses = trades is null
            ? []
            : (await trades.GetAllAsync(cancellationToken).ConfigureAwait(false))
                .Where(row => row.Inputs.Any(input => input.ItemId == item.Id))
                .OrderBy(row => row.Kind)
                .ThenBy(row => row.Output.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        AddLimited(lines, tradeUses, row => row.Kind == IntelTradeKind.Craft
            ? new Phrase(AskLine.UseCraft, row.SourceName, row.Output.Name)
            : new Phrase(AskLine.UseBarter, row.SourceName, row.Output.Name), limit: 3);
        if (lines.Count == 0)
        {
            // Nothing to say is an answer only when the tables that would have said it were read.
            if (card.Keep is null && trades is null)
            {
                return AskAnswer.Cannot(AskUnanswered.NoData, AskIntent.ItemUses, item.Name);
            }

            lines.Add(new(AskLine.NotNeeded));
        }

        var sources = new List<AskSource> { new(AskSourceKind.Catalog, card.Prices?.UpdatedUtc ?? item.Provenance.SourceUpdatedUtc) };
        if (card.Keep is not null)
        {
            sources.Add(new(AskSourceKind.Profile));
        }

        var links = new List<AskLink> { new(AskLinkKind.IntelItem, item.Id, item.Name) };
        if (tradeUses.Length > 0)
        {
            links.Add(new(AskLinkKind.IntelCrafts));
        }

        return new(AskIntent.ItemUses, item.Name, lines, sources, links, ClosestMatches: others);
    }

    private async Task<AskAnswer> ItemSourcesAsync(AskQuestion question, CancellationToken cancellationToken)
    {
        var (item, others) = await ResolveItemAsync(question.Subject, cancellationToken).ConfigureAwait(false);
        if (item is null || intel is null)
        {
            return AskAnswer.Cannot(item is null ? AskUnanswered.NoMatch : AskUnanswered.NoData, AskIntent.ItemSources, question.Subject, others);
        }

        var card = await intel.GetAsync(item.Id, cancellationToken).ConfigureAwait(false);
        var lines = new List<Phrase>();
        var offers = (card.Acquisitions ?? [])
            .OrderByDescending(offer => offer.Availability.IsObtainable)
            .ThenBy(offer => offer.Offer.Kind)
            .ThenBy(offer => offer.Offer.PriceRoubles ?? long.MaxValue)
            .ToArray();
        AddLimited(lines, offers, offer =>
        {
            var level = offer.Offer.MinimumTraderLevel is > 0 and var value ? $" LL{value}" : string.Empty;
            var open = offer.Availability.IsObtainable;
            return offer.Offer.Kind == ItemAcquisitionKind.Cash
                ? new Phrase(open ? AskLine.SourceCash : AskLine.SourceCashLocked, offer.Offer.TraderName, level, offer.Offer.PriceRoubles ?? 0)
                : new Phrase(open ? AskLine.SourceBarter : AskLine.SourceBarterLocked, offer.Offer.TraderName, level, Ingredients(offer.Offer.BarterCost));
        });

        var crafts = trades is null
            ? []
            : (await trades.GetAllAsync(cancellationToken).ConfigureAwait(false))
                .Where(row => row.Kind == IntelTradeKind.Craft && row.Output.ItemId == item.Id)
                .ToArray();
        AddLimited(lines, crafts, row => Digits(row.LevelLabel) is > 0 and var stationLevel
            ? new Phrase(AskLine.SourceCraft, row.SourceName, stationLevel, Ingredients(row.Inputs))
            : new Phrase(AskLine.SourceCraftNoLevel, row.SourceName, Ingredients(row.Inputs)), limit: 3);
        if (card.FleaEligible && card.Prices?.FleaRoubles is > 0 and var flea)
        {
            lines.Add(new(AskLine.SourceFlea, flea));
        }

        if (lines.Count == 0)
        {
            lines.Add(new(AskLine.SourceNone));
        }

        var sources = new List<AskSource> { new(AskSourceKind.Catalog, card.Prices?.UpdatedUtc ?? item.Provenance.SourceUpdatedUtc) };
        if (offers.Length > 0)
        {
            sources.Add(new(AskSourceKind.Profile));
        }

        var links = new List<AskLink> { new(AskLinkKind.IntelItem, item.Id, item.Name) };
        if (crafts.Length > 0)
        {
            links.Add(new(AskLinkKind.IntelCrafts));
        }

        return new(AskIntent.ItemSources, item.Name, lines, sources, links, ClosestMatches: others);
    }

    /// <summary>
    /// The item the words name, or none with the nearest names when no hit is close enough.
    /// </summary>
    /// <remarks>
    /// Uses the item search every page uses (name, short name, prefix). A question that says "key"
    /// is about a key, so a key hit wins over a better-scoring non-key ("Dorms 314 key" is the
    /// Dorm room 314 marked key, not a container in room 314).
    /// </remarks>
    private async Task<(ItemDefinition? Item, IReadOnlyList<string> Others)> ResolveItemAsync(string subject, CancellationToken cancellationToken)
    {
        // The search is asked a few spellings of the words ("dorms 314 key", "dorm 314"), and every
        // hit is scored again word by word, so a player's plural or a dropped "marked" still finds
        // the item while a sentence that names nothing stays below the bar.
        var found = new Dictionary<string, (ItemDefinition Item, double Score)>(StringComparer.Ordinal);
        foreach (var variant in Variants(subject))
        {
            foreach (var hit in await items.SearchAsync(variant, 8, cancellationToken).ConfigureAwait(false))
            {
                // The search is generous by design (one shared word is a hit), so a hit is taken
                // as meant only when every typed word is in its name or short name, or the search
                // itself found it all but exactly. "zzq flux capacitor" is not a FLUX helmet.
                var words = Math.Max(AskNameMatcher.Score(subject, hit.Item.Name), AskNameMatcher.Score(subject, hit.Item.ShortName));
                var score = words >= AskNameMatcher.Accept || hit.Score >= 0.95
                    ? Math.Max(Math.Max(words, hit.Score), ItemAccept)
                    : Math.Min(hit.Score, ItemAccept - 0.01);
                if (!found.TryGetValue(hit.Item.Id, out var known) || known.Score < score)
                {
                    found[hit.Item.Id] = (hit.Item, score);
                }
            }
        }

        var wantsKey = Regex.IsMatch(subject, @"\bkey\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var ordered = found.Values
            .OrderByDescending(hit => wantsKey && hit.Item.Category == ItemCategory.Key && hit.Score >= ItemAccept)
            .ThenByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Item.Name.Length)
            .ToArray();
        if (ordered.Length == 0 || ordered[0].Score < ItemAccept)
        {
            return (null, [.. ordered.Select(hit => hit.Item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(3)]);
        }

        var best = ordered[0];

        // Only near-equals are worth offering beside an answer.
        var close = ordered.Skip(1).Where(hit => hit.Score >= best.Score - 0.02 && hit.Item.Name != best.Item.Name)
            .Select(hit => hit.Item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        return (best.Item, close);
    }

    private static IEnumerable<string> Variants(string subject)
    {
        var shorter = WithoutKeyWords(subject);
        return new[] { subject, shorter, Singular(subject), Singular(shorter) }
            .Where(variant => variant.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"dorms" as "dorm": the catalog's names are singular, players' words often are not.</summary>
    private static string Singular(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length >= 4 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) ? word[..^1] : word));

    /// <summary>A map's name, or nothing where only its catalog id is known: an id is never shown.</summary>
    private static string? MapWord(string? map) =>
        map is { Length: > 0 } && !Regex.IsMatch(map, "^[0-9a-f]{24}$", RegexOptions.CultureInvariant) ? map : null;

    private static string WithoutKeyWords(string subject) =>
        Regex.Replace(subject, @"\b(?:key|keys|marked|item|items)\b", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();

    private async Task<IReadOnlyList<string>> KeyQuestNamesAsync(string itemId, CancellationToken cancellationToken)
    {
        if (facts is null)
        {
            return [];
        }

        var key = (await facts.GetKeyFactsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.ItemId == itemId);
        if (key is null || key.RelevantTaskIds.Count == 0)
        {
            return [];
        }

        var board = await BoardAsync(cancellationToken).ConfigureAwait(false);
        var names = (board?.Tasks ?? []).ToDictionary(task => task.TaskId, task => task.Name, StringComparer.Ordinal);
        return [.. key.RelevantTaskIds.Select(id => names.GetValueOrDefault(id)).OfType<string>().Distinct(StringComparer.Ordinal).Take(3)];
    }

    // ---------------------------------------------------------------- the raid

    private AskAnswer BestExtract()
    {
        var snapshot = raid?.Current();
        if (snapshot is null || snapshot.Exits.Count == 0)
        {
            return AskAnswer.Cannot(AskUnanswered.NoRaid, AskIntent.BestExtract);
        }

        var side = snapshot.Side switch
        {
            SituationSide.Pmc => AskSideWord.Pmc,
            SituationSide.Scav => AskSideWord.Scav,
            _ => AskSideWord.Unknown,
        };
        var lines = new List<Phrase> { new(AskLine.ExtractOnMap, snapshot.MapName, side) };
        if (snapshot.AreaName is { Length: > 0 } area)
        {
            lines.Add(new(AskLine.ExtractYouAt, area));
        }

        // Offered first: the extract screen proved those are open to this raid. Then nearest.
        var exits = snapshot.Exits.Where(exit => !exit.IsTransit).ToArray();
        if (exits.Length == 0)
        {
            exits = [.. snapshot.Exits];
        }

        var ordered = exits
            .OrderByDescending(exit => exit.WasOffered)
            .ThenBy(exit => exit.Metres ?? double.MaxValue)
            .ThenBy(exit => exit.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        foreach (var exit in ordered)
        {
            lines.Add(exit.Metres is { } metres
                ? new Phrase(exit.WasOffered ? AskLine.ExtractAtOffered : AskLine.ExtractAt, exit.Name, (long)Math.Round(metres), exit.Bearing)
                : new Phrase(AskLine.ExtractNoDistance, exit.Name));
            if (exit.IsTransit)
            {
                lines.Add(new(AskLine.ExtractTransit));
            }

            if (exit.Requirements is { } needs)
            {
                if (needs.RequiresSwitch)
                {
                    lines.Add(new(AskLine.ExtractNeedsSwitch));
                }

                if (needs.Transfer is { } transfer)
                {
                    lines.Add(transfer.IsPayment
                        ? new Phrase(AskLine.ExtractNeedsPayment, transfer.Count, transfer.CurrencySymbol)
                        : new Phrase(AskLine.ExtractNeedsKey, transfer.ItemName ?? transfer.ItemId));
                }

                if (needs.RequiresCoOp)
                {
                    lines.Add(new(AskLine.ExtractNeedsCoOp));
                }

                if (needs.RequiresNoBackpack)
                {
                    lines.Add(new(AskLine.ExtractNeedsNoBackpack));
                }

                if (needs.RequiresNoArmor)
                {
                    lines.Add(new(AskLine.ExtractNeedsNoArmor));
                }

                if (needs.IsOneTime)
                {
                    lines.Add(new(AskLine.ExtractOneTime));
                }
            }
        }

        var sources = new List<AskSource>();
        if (snapshot.PositionTakenUtc is { } taken)
        {
            lines.Add(new(AskLine.ExtractStraightLines));
            sources.Add(new(AskSourceKind.Screenshot, taken));
            sources.Add(new(AskSourceKind.Modelled));
        }
        else
        {
            lines.Add(new(AskLine.ExtractNoScreenshot));
        }

        sources.Add(new(AskSourceKind.Catalog));
        return new(AskIntent.BestExtract, ordered[0].Name, lines, sources, [new(AskLinkKind.Raid, null, snapshot.MapName)]);
    }

    // ---------------------------------------------------------------- ammunition

    private async Task<AskAnswer> AmmoAsync(AskQuestion question, CancellationToken cancellationToken)
    {
        if (facts is null)
        {
            return AskAnswer.Cannot(AskUnanswered.NoData, AskIntent.Ammo, question.Subject);
        }

        var all = await facts.GetAmmoAsync(cancellationToken).ConfigureAwait(false);
        var wanted = CaliberFamily(question.Subject);
        var family = all.Where(stat => CaliberKey.Of(stat.Caliber).StartsWith(wanted, StringComparison.Ordinal)).ToArray();
        var calibers = family.Select(stat => stat.Caliber).Distinct(StringComparer.Ordinal).ToArray();
        if (family.Length == 0)
        {
            return AskAnswer.Cannot(AskUnanswered.NoMatch, AskIntent.Ammo, question.Subject);
        }

        if (calibers.Length > 1 && !calibers.Any(caliber => CaliberKey.Of(caliber) == wanted))
        {
            return AskAnswer.Cannot(
                AskUnanswered.Ambiguous,
                AskIntent.Ammo,
                question.Subject,
                [.. calibers.Select(caliber => CaliberText.Describe(caliber)).Order(StringComparer.Ordinal)]);
        }

        if (calibers.Length > 1)
        {
            family = [.. family.Where(stat => CaliberKey.Of(stat.Caliber) == wanted)];
        }

        var caliberName = CaliberText.Describe(family[0].Caliber);
        var prices = question.PriceCapRoubles is null
            ? new Dictionary<string, long>(StringComparer.Ordinal)
            : await PricesAsync(family.Select(stat => stat.ItemId).ToArray(), cancellationToken).ConfigureAwait(false);
        var armorClass = question.ArmorClass;
        var threshold = (armorClass ?? 0) * 10;
        IEnumerable<AmmoStats> ranked = family
            .OrderByDescending(stat => stat.Penetration)
            .ThenByDescending(stat => stat.Damage);
        if (question.PriceCapRoubles is { } cap)
        {
            ranked = ranked.Where(stat => prices.TryGetValue(stat.ItemId, out var price) && price <= cap);
        }

        var candidates = ranked.ToArray();
        var lines = new List<Phrase>();
        DateTimeOffset? asOf = family.Max(stat => (DateTimeOffset?)(stat.Provenance.SourceUpdatedUtc ?? stat.Provenance.ObservedUtc));
        if (candidates.Length == 0)
        {
            lines.Add(new(AskLine.AmmoNoneUnderCap, caliberName, question.PriceCapRoubles ?? 0));
            return new(AskIntent.Ammo, caliberName, lines, [new(AskSourceKind.Catalog, asOf)], [new(AskLinkKind.IntelAmmo)]);
        }

        var good = armorClass is null ? candidates : [.. candidates.Where(stat => stat.Penetration >= threshold)];
        var picks = good.Length > 0 ? good.Take(3).ToArray() : [];
        foreach (var stat in picks)
        {
            var name = (await items.GetAsync(stat.ItemId, cancellationToken).ConfigureAwait(false))?.Name ?? stat.ItemId;
            lines.Add(prices.TryGetValue(stat.ItemId, out var price)
                ? new Phrase(AskLine.AmmoPickPriced, name, stat.Penetration, stat.Damage, price)
                : new Phrase(AskLine.AmmoPick, name, stat.Penetration, stat.Damage));
        }

        if (picks.Length == 0)
        {
            var best = candidates[0];
            var name = (await items.GetAsync(best.ItemId, cancellationToken).ConfigureAwait(false))?.Name ?? best.ItemId;
            lines.Add(new(AskLine.AmmoNoneGood, caliberName, armorClass ?? 0, name, best.Penetration));
        }

        var sources = new List<AskSource> { new(AskSourceKind.Catalog, asOf) };
        if (armorClass is { } rated)
        {
            lines.Add(new(AskLine.AmmoRule, threshold, rated));
            sources.Add(new(AskSourceKind.Modelled));
        }

        var top = picks.FirstOrDefault() ?? candidates[0];
        var topName = (await items.GetAsync(top.ItemId, cancellationToken).ConfigureAwait(false))?.Name ?? top.ItemId;
        return new(AskIntent.Ammo, caliberName, lines, sources, [new(AskLinkKind.IntelAmmo), new(AskLinkKind.IntelItem, top.ItemId, topName)]);
    }

    /// <summary>The cheapest known price per round: the flea's, or any trader's cash price.</summary>
    private async Task<Dictionary<string, long>> PricesAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        var prices = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (await items.GetPriceAsync(id, cancellationToken).ConfigureAwait(false) is { FleaPriceRoubles: > 0 and var flea })
            {
                prices[id] = flea;
            }
        }

        if (acquisitions is not null)
        {
            foreach (var offer in await acquisitions.GetAsync(ids, cancellationToken).ConfigureAwait(false))
            {
                if (offer.Offer is { Kind: ItemAcquisitionKind.Cash, PriceRoubles: > 0 and var cash } &&
                    (!prices.TryGetValue(offer.Offer.ItemId, out var known) || cash < known))
                {
                    prices[offer.Offer.ItemId] = cash;
                }
            }
        }

        return prices;
    }

    /// <summary>What a typed caliber folds to, including the names players use for a few of them.</summary>
    internal static string CaliberFamily(string typed)
    {
        var folded = CaliberKey.Of(typed);
        if (folded.StartsWith("CALIBER", StringComparison.Ordinal))
        {
            folded = folded["CALIBER".Length..];
        }

        return folded switch
        {
            "1270" or "12GA" or "12GAUGE" or "12G" => "12G",
            "2070" or "20GA" or "20GAUGE" or "20G" => "20G",
            "366" => "366TKM",
            "300" or "300BLK" => "762X35",
            "338" => "86X70",
            "45" or "45ACP" => "1143X23",
            _ => folded.Replace("MM", string.Empty, StringComparison.Ordinal),
        };
    }

    // ---------------------------------------------------------------- helpers

    private async Task<IReadOnlyList<string>> ClosestAnywhereAsync(string question, CancellationToken cancellationToken)
    {
        // Only the longest word run that is plausibly a name: a sentence is not an item.
        var words = AskGrammar.Clean(question).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3 && !CommonWords.Contains(word))
            .ToArray();
        if (words.Length == 0)
        {
            return [];
        }

        var hits = await items.SearchAsync(string.Join(' ', words), 3, cancellationToken).ConfigureAwait(false);
        return [.. hits.Select(hit => hit.Item.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "what", "where", "which", "who", "how", "the", "and", "for", "are", "is", "does", "can", "should", "you", "your",
        "about", "with", "this", "that", "there", "from", "have", "has", "why", "when", "much", "many", "tell",
    };

    private async Task<QuestBoardReadModel?> BoardAsync(CancellationToken cancellationToken)
    {
        if (quests is null)
        {
            return null;
        }

        var profile = await ProfileAsync(cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        return await quests.GetQuestBoardAsync(new(profile.Id, profile.GameMode, profile.ProfileGeneration), cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlayerProfile?> ProfileAsync(CancellationToken cancellationToken) =>
        profiles is null ? null : await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);

    private static void AddLimited<T>(List<Phrase> lines, IEnumerable<T> rows, Func<T, Phrase> say, int limit = RowLimit)
    {
        var all = rows as IReadOnlyCollection<T> ?? rows.ToArray();
        foreach (var row in all.Take(limit))
        {
            lines.Add(say(row));
        }

        if (all.Count > limit)
        {
            lines.Add(Phrase.Counted(AskLine.More, all.Count - limit));
        }
    }

    private static string Ingredients(IReadOnlyList<IntelTradeIngredient> ingredients) =>
        string.Join(", ", ingredients.Take(3).Select(item => item.Count > 1 ? $"{item.Count} × {item.Name}" : item.Name)) +
        (ingredients.Count > 3 ? ", …" : string.Empty);

    /// <summary>The number in Crafts &amp; barters' "Level 2" label; 0 where the feed stated none.</summary>
    private static int Digits(string label) =>
        int.TryParse(new string([.. label.Where(char.IsDigit)]), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    [GeneratedRegex(@"(?:\s+(?:level|lvl|lv|l))?\s+(?<n>[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingLevel();
}
