namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// How prominently a co-op extract is offered. Issue 573: "co-op extracts shouldnt highlight as
/// options on the map, i pretty much never use them" — most players cannot use one without
/// another operator also choosing it, so by default it is drawn small and out of the way rather
/// than competing for attention with an extract anybody can just walk to.
/// </summary>
public enum CoOpExtractVisibility
{
    /// <summary>Not drawn at all.</summary>
    Hidden,

    /// <summary>Drawn small and dim, left out of Extract options and the suggested route. The default.</summary>
    Dim,

    /// <summary>Treated exactly like any other extract.</summary>
    Normal,
}

/// <summary>
/// Tells a co-op extract apart from an ordinary one, and what today's <see cref="CoOpExtractVisibility"/>
/// says to do about it.
/// </summary>
/// <remarks>
/// The primary feed and this repository's own reviewed supplement (<c>ReviewedExtractCatalog</c>)
/// both name a co-op extract with "(Co-op)" in the extract's own display name — "Side Tunnel
/// (Co-Op)", "Emercom Checkpoint (Co-op)" — so that is what this reads, the same name every panel
/// and list already shows the player. There is no separate structured flag in the map catalog to
/// read instead.
/// </remarks>
public static class CoOpExtracts
{
    public static bool IsCoOp(string? extractName) =>
        !string.IsNullOrEmpty(extractName) && extractName.Contains("Co-op", StringComparison.OrdinalIgnoreCase);

    /// <summary>The remembered setting, or <see cref="CoOpExtractVisibility.Dim"/> where none is stored yet.</summary>
    public static CoOpExtractVisibility ParseVisibility(string? stored) =>
        Enum.TryParse<CoOpExtractVisibility>(stored, ignoreCase: true, out var value) ? value : CoOpExtractVisibility.Dim;

    /// <summary>Whether a co-op extract should be offered as a choice: a route target, a row in
    /// Extract options, something the game's own "offered" flag may highlight. False unless the
    /// player asked for Normal.</summary>
    public static bool IsOffered(string extractName, CoOpExtractVisibility visibility) =>
        visibility == CoOpExtractVisibility.Normal || !IsCoOp(extractName);
}
