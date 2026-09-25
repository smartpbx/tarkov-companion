using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TarkovCompanion.App.Services.Updates;

/// <summary>The package this machine ran before its last update, kept so it can go back without the feed.</summary>
/// <param name="Version">The kept build.</param>
/// <param name="FileName">The file in the kept folder.</param>
/// <param name="Sha256">Its SHA256 when it was kept, upper-case hex.</param>
/// <param name="Size">Its length in bytes.</param>
public sealed record KeptPackage(string Version, string FileName, string Sha256, long Size);

/// <summary>What the updater remembers between runs: the pin, the last apply, the kept package.</summary>
public sealed record UpdateState(UpdatePin? Pin = null, UpdateProvenance? Applied = null, KeptPackage? Kept = null);

/// <summary>Where <see cref="UpdateState"/> lives.</summary>
public interface IUpdateStateStore
{
    /// <summary>The folder the kept package is in. Outside the install folder, which an update replaces.</summary>
    string KeptFolder { get; }

    UpdateState Read();

    void Write(UpdateState state);
}

/// <summary>
/// <see cref="UpdateState"/> as a small JSON file in the data folder.
/// </summary>
/// <remarks>
/// In the data folder, not beside the application: everything under the install folder is
/// replaced by the very update this file describes. A file that cannot be read is an empty
/// state, never a failure: the worst a lost pin does is offer a build once too often.
/// </remarks>
public sealed class UpdateStateFile(string folder, ILogger? logger = null) : IUpdateStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _gate = new();

    public static UpdateStateFile For(AppDataPaths paths, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new(Path.Combine(paths.Root, "Updates"), logger);
    }

    public string KeptFolder => Path.Combine(folder, "previous");

    private string FilePath => Path.Combine(folder, "update-state.json");

    public UpdateState Read()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(FilePath), Json) ?? new()
                    : new();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                logger?.LogWarning(exception, "Could not read the update state; treating it as empty");
                return new();
            }
        }
    }

    public void Write(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            // Durable as well as atomic (#888): a pin lost to a power cut offers the build it held back.
            TarkovCompanion.Infrastructure.Settings.AtomicJsonFile.Write(FilePath, JsonSerializer.Serialize(state, Json));
        }
    }
}
