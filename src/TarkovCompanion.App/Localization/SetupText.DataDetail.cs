using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    /// <summary>[#314] The game-data status line: the coordinator's code in words, or a fixture's own line.</summary>
    public static string DataDetail(RuntimeDataState data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.DetailPhrase is { } detail ? PhraseText.Say(detail) : data.Detail;
    }

    /// <summary>[#314] <see cref="DataDetail(RuntimeDataState)"/>, or null before there is any state.</summary>
    public static string? DataDetailOf(RuntimeDataState? data) => data is null ? null : DataDetail(data);

    /// <summary>[#314] What the scanner said about itself, in words, or the scan's own detail.</summary>
    public static string ScanDetail(ScanExecutionResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return scan.DetailPhrase is { } detail ? PhraseText.Say(detail) : scan.Detail;
    }
}
