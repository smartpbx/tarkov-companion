using System.Globalization;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Sound;

namespace TarkovCompanion.App.Services.Sound;

/// <summary>[#712 0-10] The spoken words, from the string tables.</summary>
/// <remarks>
/// Money is rounded the way a person says it ("1.2 million", "45 thousand"): a spoken figure is
/// for deciding, and nine digits read aloud take longer than the decision.
/// </remarks>
public sealed class LocalizedSoundLines : ISoundLines
{
    public string Test => SetupText.SoundTestLine;

    public string LootVerdict(LootVerdictLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var what = line.Name is { } name
            ? line.Word switch
            {
                LootVerdictWord.Keep => SetupText.SoundLineKeep(name),
                LootVerdictWord.SellFlea => SetupText.SoundLineSellFlea(name),
                LootVerdictWord.SellTrader => SetupText.SoundLineSellTrader(name),
                LootVerdictWord.Leave => SetupText.SoundLineLeave(name),
                LootVerdictWord.Use => SetupText.SoundLineUse(name),
                LootVerdictWord.DoNotUse => SetupText.SoundLineDoNotUse(name),
                _ => name,
            }
            : SetupText.SoundLineItems(line.ItemCount);
        return line.Value is { } value
            ? SetupText.SoundLineWithValue(what, Money(value))
            : SetupText.SoundLineNoValue(what);
    }

    public static string Money(long roubles)
    {
        var culture = CultureInfo.CurrentCulture;
        return roubles switch
        {
            >= 999_500 => SetupText.SoundMoneyMillions((Math.Round(roubles / 100_000d) / 10).ToString("0.#", culture)),
            >= 1_000 => SetupText.SoundMoneyThousands(Math.Round(roubles / 1_000d).ToString("0", culture)),
            _ => SetupText.SoundMoneyRoubles(roubles.ToString("0", culture)),
        };
    }
}
