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
            throw Unreadable($"{FileName} is larger than {MaximumBytes} bytes.");
        }

        NetworkControls? controls;
        try
        {
            controls = JsonSerializer.Deserialize<NetworkControls>(File.ReadAllBytes(path), Json);
        }
        catch (JsonException exception)
        {
            throw Unreadable($"{FileName} is not valid JSON.", exception);
        }

        return controls ?? throw Unreadable($"{FileName} is empty.");
    }

    /// <summary>
    /// Sets the bad file aside before saying so, so the record of what the player had survives
    /// the fail-closed answer the policy writes in its place (#888).
    /// </summary>
    private InvalidDataException Unreadable(string message, Exception? inner = null)
    {
        AtomicJsonFile.SetAside(path, DateTimeOffset.UtcNow);
        return new InvalidDataException(message, inner);
    }

    public void Save(NetworkControls controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        AtomicJsonFile.Write(path, JsonSerializer.Serialize(controls, Json));
    }
}
