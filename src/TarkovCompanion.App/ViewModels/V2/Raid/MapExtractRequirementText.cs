using System.Globalization;
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
            parts.Add("Needs power: " + string.Join(", then ", requirements.SwitchChain.Select(item => item.Name)));
        }

        if (requirements.Transfer is { } transfer)
        {
            parts.Add(transfer.CurrencySymbol is { } symbol
                ? string.Create(CultureInfo.CurrentCulture, $"Costs {transfer.Count:N0} {symbol}")
                : transfer.ItemName is { Length: > 0 } name
                    ? transfer.Count == 1 ? $"Needs key: {name}" : $"Needs {transfer.Count:N0} × {name}"
                    : transfer.Count == 1 ? "Needs an item" : $"Needs {transfer.Count:N0} of an item");
        }

        foreach (var condition in requirements.Conditions)
        {
            switch (condition.Kind)
            {
                case MapExtractConditionKind.NoBackpack:
                    parts.Add("No backpack");
                    break;
                case MapExtractConditionKind.NoArmor:
                    parts.Add("No armored vest");
                    break;
                case MapExtractConditionKind.Items when condition.Items.Count > 0:
                    parts.Add("Bring " + string.Join(" + ", condition.Items));
                    break;
                case MapExtractConditionKind.TimedWindow when condition.TimedWindow is { } window:
                    parts.Add(DescribeTimedWindow(extractName, window, timeLeft));
                    break;
            }
        }

        if (requirements.RequiresCoOp)
        {
            parts.Add("Needs co-op partner");
        }

        if (requirements.IsOneTime)
        {
            parts.Add("One use");
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
        var subject = extractName.Contains("train", StringComparison.OrdinalIgnoreCase) ? "Train" : extractName;
        if (!TimeSpan.TryParse(timeLeft, CultureInfo.InvariantCulture, out var left) || left < TimeSpan.Zero)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{subject} arrives with {window.ArrivalStartsAtTimeLeft.TotalMinutes:0}–{window.ArrivalEndsAtTimeLeft.TotalMinutes:0} min left, stays {window.Duration.TotalMinutes:0} min");
        }

        if (left > window.ArrivalStartsAtTimeLeft)
        {
            var until = Math.Max(1, (int)Math.Ceiling((left - window.ArrivalStartsAtTimeLeft).TotalMinutes));
            return string.Create(CultureInfo.InvariantCulture, $"{subject} in ~{until} min, stays {window.Duration.TotalMinutes:0} min");
        }

        if (left > window.ArrivalEndsAtTimeLeft)
        {
            var until = Math.Max(1, (int)Math.Ceiling((left - window.ArrivalEndsAtTimeLeft).TotalMinutes));
            return string.Create(CultureInfo.InvariantCulture, $"{subject} arriving now–~{until} min, stays {window.Duration.TotalMinutes:0} min");
        }

        var earliestDeparture = window.ArrivalStartsAtTimeLeft - window.Duration;
        var latestDeparture = window.ArrivalEndsAtTimeLeft - window.Duration;
        if (left > earliestDeparture)
        {
            var maximum = Math.Max(1, (int)Math.Ceiling((left - latestDeparture).TotalMinutes));
            return $"{subject} here, up to {maximum} min left";
        }

        if (left > latestDeparture)
        {
            var maximum = Math.Max(1, (int)Math.Ceiling((left - latestDeparture).TotalMinutes));
            return $"{subject} may still be here, up to {maximum} min left";
        }

        return $"{subject} has left";
    }
}
