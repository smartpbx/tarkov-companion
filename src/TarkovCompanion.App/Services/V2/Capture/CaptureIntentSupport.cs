using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>Which intents a capture can be armed for and get an answer back.</summary>
/// <remarks>
/// Health and Map/extracts could be armed and returned nothing: the composite handoff
/// acknowledged them and produced no output, so the player got a progress bar and then
/// silence. Nothing reads those screens yet. They stay in the picker, disabled and saying so,
/// rather than vanishing, so nobody wonders where they went.
/// </remarks>
public static class CaptureIntentSupport
{
    public const string NotSupportedYet = "not supported yet";

    public static bool IsSupported(ScanIntent intent) =>
        intent is not (ScanIntent.ExtractsAndMap or ScanIntent.HealthAndCharacter);
}
