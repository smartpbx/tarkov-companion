using System.Globalization;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>Short player-facing words for the typed requirements carried by an extract row.</summary>
internal static class MapExtractRequirementText
{
    public static string Describe(MapExtractRequirements? requirements)
    {
        if (requirements is null)
        {
            return string.Empty;
        }

        var parts = new List<string>(4);
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
}
