using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Application.Services.Runtime;

/// <summary>
/// [#314] What the scanner says about itself at startup, as codes the App words in Setup &gt;
/// Recognition. <see cref="ScanExecutionResult.Detail"/> keeps the English for the other screens.
/// </summary>
[PhraseCodes("Setup.ScannerStatus")]
public enum ScannerStatus
{
    NotOnPlatform,
    Ready,

    /// <summary>{0}: the engine's reason, an <c>OcrUnavailableReason</c> phrase or its own English.</summary>
    CannotRun,

    /// <summary>{0}: the engine's provider name.</summary>
    DidNotStart,
}
