using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>[#712 0-6] The spare minutes the Now panel's late-raid line adds to the walk to the exit.</summary>
/// <remarks>
/// "The margin is set once": stored as whole minutes, 0 to <see cref="Maximum"/>. A hand-edited
/// layout file can hold anything, so anything else reads back as the default two minutes.
/// </remarks>
internal sealed class LeaveMarginSetting
{
    public const int Maximum = 10;

    private readonly IWorkspaceLayoutStore? _store;

    public LeaveMarginSetting(IWorkspaceLayoutStore? store)
    {
        _store = store;
        Minutes = Parse(store?.Get(WorkspaceLayoutKeys.RaidLeaveMargin));
    }

    public static int Default => (int)NowPanelState.LeaveMargin.TotalMinutes;

    public int Minutes { get; private set; }

    public TimeSpan Value => TimeSpan.FromMinutes(Minutes);

    /// <summary>Reads the stored margin again, after Backup &amp; reset replaced it.</summary>
    public void Reload() => Minutes = Parse(_store?.Get(WorkspaceLayoutKeys.RaidLeaveMargin));

    public int Change(int steps)
    {
        Minutes = Math.Clamp(Minutes + steps, 0, Maximum);
        _store?.Set(WorkspaceLayoutKeys.RaidLeaveMargin, Minutes.ToString(CultureInfo.InvariantCulture));
        return Minutes;
    }

    internal static int Parse(string? stored) =>
        int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes is >= 0 and <= Maximum
            ? minutes
            : Default;
}
