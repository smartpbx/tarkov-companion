using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    /// <summary>[#314] Why the OCR engine cannot run: its code in words, its English where it has no code.</summary>
    public static string OcrReason(OcrEngineAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        return availability.Why is { } why
            ? PhraseText.Say(why)
            : availability.Reason ?? SettingsRecognitionNoReason;
    }
}
