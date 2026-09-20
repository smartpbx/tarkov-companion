using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;

namespace TarkovCompanion.App.Services.V2.Setup;

/// <summary>What is true right now beside an About or Data &amp; Privacy item, read when the page opens.</summary>
public static class SetupPageFacts
{
    public static IReadOnlyList<string> ForAbout(string anchor) => anchor == SetupAnchors.WhatItIs
        ? [$"Version {AppBuildIdentity.Current.Version}"]
        : [];

    /// <param name="anchor">The item asking.</param>
    /// <param name="offline">Whether offline mode is on, so nothing is fetched.</param>
    /// <param name="source">The Data section's source line, e.g. "json.tarkov.dev · PvE · en".</param>
    /// <param name="group">The Team page's state, for whether a room is joined.</param>
    public static IReadOnlyList<string> ForDataPrivacy(string anchor, bool offline, string? source, GroupPageViewModel? group) => anchor switch
    {
        SetupAnchors.LeavesComputer => offline
            ? ["Offline mode is on: nothing is being fetched."]
            : source is null ? [] : [$"Game data now: {source}"],
        SetupAnchors.SharingScope => group is null
            ? []
            : [group.HasMembers ? $"In a room with {group.Members.Count} people right now." : "Not in a room, so nothing is being shared."],
        _ => [],
    };
}
