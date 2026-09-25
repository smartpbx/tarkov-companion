using System.Text.Json;
using TarkovCompanion.Core.Network;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>
/// [#292] Config/network.json: Local only and the per-service switches from Setup › Data &amp; Privacy.
/// </summary>
/// <remarks>
/// A missing file is the defaults. A switch missing from the file keeps its default, so a later
/// service added to the record starts on for somebody who saved before it existed.
/// </remarks>
public sealed class JsonFileNetworkControlsStore(string path) : INetworkControlsStore
{
    public const string FileName = "network.json";
    private const int MaximumBytes = 16 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public NetworkControls Read()
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return NetworkControls.Default;
        }

        if (info.Length > MaximumBytes)
        {
            throw new InvalidDataException($"{FileName} is larger than {MaximumBytes} bytes.");
        }

        try
        {
            return JsonSerializer.Deserialize<NetworkControls>(File.ReadAllBytes(path), Json)
                ?? throw new InvalidDataException($"{FileName} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{FileName} is not valid JSON.", exception);
        }
    }

    public void Save(NetworkControls controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Temp then move, for the reason AtomicJsonFile gives.
        var temporary = full + ".writing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(controls, Json));
        File.Move(temporary, full, overwrite: true);
    }
}
