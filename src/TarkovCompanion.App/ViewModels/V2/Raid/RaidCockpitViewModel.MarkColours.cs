using System.Windows.Input;
using Avalonia.Media;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// #290: a mark's chosen colour, on this map and on the paired tablet's.
/// </summary>
/// <remarks>
/// A colour is presentation, so it rides in the host's styles rather than the scene contract (see
/// <see cref="MapSceneObjectStyle"/>). The same id-to-colour table goes to the tablet with the
/// surface, which is how a colour picked on the tablet comes back drawn on both.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    private IReadOnlyDictionary<string, string> _markColours = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Scene object id to palette colour, for every coloured mark on the current scene.</summary>
    public IReadOnlyDictionary<string, string> MarkColours => Volatile.Read(ref _markColours);

    /// <summary>
    /// The player's own marks and the squad's, by the scene object id each is drawn under, for
    /// the ones given a palette colour. Internal for direct coverage.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> MarkColoursFor(
        IReadOnlyList<RaidMark> marks,
        IReadOnlyList<GroupWaypointView> waypoints,
        IReadOnlyList<GroupPingView> pings)
    {
        var colours = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mark in marks)
        {
            if (MarkPalette.Normalize(mark.Colour) is { } colour)
            {
                colours[$"{MarkIdPrefix}{mark.Id}"] = colour;
            }
        }

        foreach (var waypoint in waypoints)
        {
            if (MarkPalette.Normalize(waypoint.Colour) is { } colour)
            {
                colours[$"{GroupWaypointPrefix}{waypoint.Id}"] = colour;
            }
        }

        foreach (var ping in pings)
        {
            if (MarkPalette.Normalize(ping.Colour) is { } colour)
            {
                colours[$"{GroupPingPrefix}{ping.Id}"] = colour;
            }
        }

        return colours;
    }

    /// <summary>The styles with each coloured mark's colour laid over whatever else it had.</summary>
    internal static IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> WithMarkColours(
        IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> styles,
        IReadOnlyDictionary<string, string> colours)
    {
        if (colours.Count == 0)
        {
            return styles;
        }

        var merged = new Dictionary<MapSceneObjectId, MapSceneObjectStyle>(styles);
        foreach (var (id, colour) in colours)
        {
            var key = new MapSceneObjectId(id);
            merged[key] = merged.TryGetValue(key, out var existing) ? existing with { Color = colour } : new(Color: colour);
        }

        return merged;
    }

    private string? _newMarkColour;
    private IReadOnlyList<MarkColourChoiceViewModel>? _markColourChoices;

    /// <summary>The colour new marks get, from the Marks card's picker; null keeps each kind's own.</summary>
    public string? NewMarkColour => _newMarkColour;

    /// <summary>Auto and the six palette colours, for the Marks card.</summary>
    public IReadOnlyList<MarkColourChoiceViewModel> MarkColourChoices => _markColourChoices ??=
    [
        new(null, "Auto colour", this),
        .. MarkPalette.Colours.Select(colour => new MarkColourChoiceViewModel(colour.Hex, colour.Name, this)),
    ];

    internal void ChooseNewMarkColour(string? colour)
    {
        _newMarkColour = MarkPalette.Normalize(colour);
        OnPropertyChanged(nameof(NewMarkColour));
        foreach (var choice in MarkColourChoices)
        {
            choice.Refresh();
        }
    }

    /// <summary>Called by the scene rebuild once the marks' own styles are in place.</summary>
    private void ApplyMarkColours()
    {
        var group = _stateStore.Current.Group;
        var colours = MarkColoursFor(_marks.Marks, group.Waypoints, group.Pings);
        Volatile.Write(ref _markColours, colours);
        _objectStyles = WithMarkColours(_objectStyles, colours);
    }
}

/// <summary>One swatch of the Marks card's colour picker (#290).</summary>
public sealed class MarkColourChoiceViewModel : BindableViewModel
{
    private readonly RaidCockpitViewModel _owner;

    internal MarkColourChoiceViewModel(string? hex, string name, RaidCockpitViewModel owner)
    {
        Hex = hex;
        Name = name;
        _owner = owner;
        Brush = hex is null ? null : new SolidColorBrush(Color.Parse(hex));
        ChooseCommand = new DelegateCommand(() => _owner.ChooseNewMarkColour(Hex));
    }

    public string? Hex { get; }

    /// <summary>What a screen reader says for the swatch.</summary>
    public string Name { get; }

    public IBrush? Brush { get; }

    public bool IsAuto => Hex is null;

    public bool IsChosen => string.Equals(_owner.NewMarkColour, Hex, StringComparison.Ordinal);

    public ICommand ChooseCommand { get; }

    internal void Refresh() => OnPropertyChanged(nameof(IsChosen));
}
