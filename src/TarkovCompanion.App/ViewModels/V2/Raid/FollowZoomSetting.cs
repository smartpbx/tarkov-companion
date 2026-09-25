using System.Globalization;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>The remembered magnification used while the raid map follows the player.</summary>
/// <remarks>
/// Kept apart from the camera because the camera is raid state and this is a player's choice.
/// A hand-edited layout file can contain any string, so loading and every update share the same
/// finite clamp before the value is allowed near the renderer.
/// </remarks>
internal sealed class FollowZoomSetting
{
    public const double Default = 3;
    public const double Minimum = 1;
    public const double Maximum = 8;
    public const double Step = 0.5;

    private readonly IWorkspaceLayoutStore? _store;

    public FollowZoomSetting(IWorkspaceLayoutStore? store)
    {
        _store = store;
        Value = Parse(store?.Get(WorkspaceLayoutKeys.RaidFollowZoom));
    }

    public double Value { get; private set; }

    /// <summary>[#902] Reads the stored magnification again, after Backup &amp; reset replaced it.</summary>
    public void Reload() => Value = Parse(_store?.Get(WorkspaceLayoutKeys.RaidFollowZoom));

    public double ChangeBy(int steps) => Set(Value + (steps * Step));

    public double Set(double value)
    {
        Value = Clamp(value);
        _store?.Set(WorkspaceLayoutKeys.RaidFollowZoom, Value.ToString("0.##", CultureInfo.InvariantCulture));
        return Value;
    }

    internal static double Clamp(double value) => Math.Clamp(
        double.IsFinite(value) ? value : Default,
        Minimum,
        Maximum);

    internal static double Parse(string? stored) =>
        double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Clamp(value)
            : Default;
}
