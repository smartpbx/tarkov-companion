namespace TarkovCompanion.Application.Services.Planning;

/// <summary>
/// The rules version each planner stamps on what it outputs (#307), shown beside the output the
/// way a saved loot scan shows its ruleset (#784) and a stash sort plan shows its rules (#801).
/// </summary>
/// <remarks>
/// Bump the number when a rule changes what a planner would say for the same inputs, not when the
/// code around it moves. A player comparing two screenshots of a plan can then tell "the game
/// changed" from "the companion changed its mind". docs/adr/0021-next-raid-planner.md lists what
/// each planner reads and what it leaves out.
/// </remarks>
public static class PlannerVersions
{
    /// <summary>Objective visit order on one map: nearest neighbour then 2-opt, straight lines.</summary>
    public const string ObjectiveRoute = "route-307.1";

    /// <summary>Hideout critical path: prerequisites first, build time, trader and skill gates.</summary>
    public const string HideoutPath = "hideout-path-307.1";

    /// <summary>Loadout suggestions from the chosen map's active objectives.</summary>
    public const string LoadoutSuggestions = "loadout-suggest-307.1";

    /// <summary>"Rules route-307.1", the words every planner surface uses.</summary>
    public static string Label(string version) => "Rules " + version;
}
