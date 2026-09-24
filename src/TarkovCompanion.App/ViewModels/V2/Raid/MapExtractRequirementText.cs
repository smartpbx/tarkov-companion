using System.Globalization;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>Short player-facing words for the typed requirements carried by an extract row.</summary>
internal static class MapExtractRequirementText
{
    public static string Describe(MapExtractRequirements? requirements, string extractName, string? timeLeft = null)
    {
        if (requirements is null)
        {
            return string.Empty;
        }

        var parts = new List<string>(8);
        if (requirements.SwitchChain.Count > 0)
        {
            parts.Add(RaidText.NeedsPower(string.Join(RaidText.PowerChainJoiner, requirements.SwitchChain.Select(item => item.Name))));
        }

        if (requirements.Transfer is { } transfer)
        {
            parts.Add(transfer.CurrencySymbol is { } symbol
                ? RaidText.CostsCurrency(transfer.Count, symbol)
                : transfer.ItemName is { Length: > 0 } name
                    ? transfer.Count == 1 ? RaidText.NeedsKey(name) : RaidText.NeedsCountOf(transfer.Count, name)
                    : transfer.Count == 1 ? RaidText.NeedsAnItem : RaidText.NeedsCountOfAnItem(transfer.Count));
        }

        foreach (var condition in requirements.Conditions)
        {
            switch (condition.Kind)
            {
                case MapExtractConditionKind.NoBackpack:
                    parts.Add(RaidText.NoBackpack);
                    break;
                case MapExtractConditionKind.NoArmor:
                    parts.Add(RaidText.NoArmoredVest);
                    break;
                case MapExtractConditionKind.Items when condition.Items.Count > 0:
                    parts.Add(RaidText.Bring(string.Join(" + ", condition.Items)));
                    break;
                case MapExtractConditionKind.TimedWindow when condition.TimedWindow is { } window:
                    parts.Add(DescribeTimedWindow(extractName, window, timeLeft));
                    break;
            }
        }

        if (requirements.RequiresCoOp)
        {
            parts.Add(RaidText.NeedsCoOpPartner);
        }

        if (requirements.IsOneTime)
        {
            parts.Add(RaidText.OneUse);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Relates a checked schedule to the same countdown already shown above the rows.</summary>
    /// <remarks>
    /// A train's arrival varies inside its published window. The middle and late phrases keep
    /// that uncertainty instead of presenting a schedule-derived estimate as a live detection.
    /// </remarks>
    internal static string DescribeTimedWindow(string extractName, MapExtractTimedWindow window, string? timeLeft)
    {
        var subject = extractName.Contains("train", StringComparison.OrdinalIgnoreCase) ? RaidText.Train : extractName;
        if (!TimeSpan.TryParse(timeLeft, CultureInfo.InvariantCulture, out var left) || left < TimeSpan.Zero)
        {
            return RaidText.TrainArrivesWith(
                subject,
                window.ArrivalStartsAtTimeLeft.TotalMinutes,
                window.ArrivalEndsAtTimeLeft.TotalMinutes,
                window.Duration.TotalMinutes);
        }

        if (left > window.ArrivalStartsAtTimeLeft)
        {
            var until = Math.Max(1, (int)Math.Ceiling((left - window.ArrivalStartsAtTimeLeft).TotalMinutes));
            return RaidText.TrainIn(subject, until, window.Duration.TotalMinutes);
        }

        if (left > window.ArrivalEndsAtTimeLeft)
        {
            var until = Math.Max(1, (int)Math.Ceiling((left - window.ArrivalEndsAtTimeLeft).TotalMinutes));
            return RaidText.TrainArrivingNow(subject, until, window.Duration.TotalMinutes);
        }

        var earliestDeparture = window.ArrivalStartsAtTimeLeft - window.Duration;
        var latestDeparture = window.ArrivalEndsAtTimeLeft - window.Duration;
        if (left > earliestDeparture)
        {
            var maximum = Math.Max(1, (int)Math.Ceiling((left - latestDeparture).TotalMinutes));
            return RaidText.TrainHere(subject, maximum);
        }

        if (left > latestDeparture)
        {
            var maximum = Math.Max(1, (int)Math.Ceiling((left - latestDeparture).TotalMinutes));
            return RaidText.TrainMayStillBeHere(subject, maximum);
        }

        return RaidText.TrainHasLeft(subject);
    }
}
