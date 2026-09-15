namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>
/// What the player is working with, which moving between pages must never discard.
/// </summary>
/// <remarks>
/// The #267 traceability list, as fields rather than as "context" in the abstract. Everything
/// here arrives from the runtime or from the player's own choices; navigation only ever reads it.
/// A reset of the preview's navigation state therefore cannot touch any of it, which is the
/// property the rollback rule depends on.
/// </remarks>
public sealed record V2NavigationContext(
    string? ProfileName,
    string? MapId,
    string? PlanId,
    string? PriorScan,
    string InitiatingDevice)
{
    public const string ThisDesktop = "this-desktop";

    public static V2NavigationContext Empty { get; } = new(null, null, null, null, ThisDesktop);
}

/// <summary>One place in the history, with what was selected and focused there.</summary>
public sealed record V2HistoryEntry(V2ShellLocation Location, string? SelectedEntity, string? FocusTarget, string? Invoker);

public enum V2FocusReason
{
    /// <summary>The player moved to a page: its heading.</summary>
    PageHeading = 1,

    /// <summary>Intel opened beside the page: the panel's heading.</summary>
    IntelHeading,

    /// <summary>Something the player opened closed: the control that opened it.</summary>
    Invoker,

    /// <summary>Back, Forward or a restart returned to a place: whatever had focus there.</summary>
    Restored,
}

/// <summary>Where focus should go after a player's own navigation. Background changes never make one.</summary>
public sealed record V2FocusRequest(string Target, V2FocusReason Reason);

public enum V2NavigationKind
{
    Push = 1,
    Back,
    Forward,
    Restore,
}

public sealed record V2NavigationChange(V2HistoryEntry Previous, V2HistoryEntry Current, V2NavigationKind Kind, V2FocusRequest Focus);

public sealed record V2NavigationResult(bool Succeeded, V2FocusRequest? Focus, string? Failure)
{
    public static V2NavigationResult Refused(string failure) => new(false, null, failure);
}

/// <summary>
/// The one router both variants share: history, deep links, selection, context and focus.
/// </summary>
/// <remarks>
/// Placement is the only thing that differs between the variants, so it is the only thing this
/// asks the variant about. Opening Intel is one call; under Variant A it moves to the Intel
/// workspace and Back returns, under Variant B it opens beside the page at an address of its
/// own. Everything else — history, what focus does, what survives a move — is identical, which is
/// what lets a session compare where things are rather than two different routers.
///
/// Focus follows the #265 state-matrix rule: it moves only for the player's own action. Nothing
/// here raises a focus request for a context update, and <see cref="UpdateContext"/> cannot push
/// history.
/// </remarks>
public sealed class V2ShellRouter
{
    public const int DefaultHistoryLimit = 50;
    public const string PageHeadingTarget = "v2-shell-page-heading";
    public const string IntelHeadingTarget = "v2-shell-intel-heading";

    private readonly V2RouteRegistry _registry;
    private readonly int _historyLimit;
    private readonly List<V2HistoryEntry> _back = [];
    private readonly List<V2HistoryEntry> _forward = [];

    public V2ShellRouter(
        V2ShellVariantDefinition variant,
        V2RouteRegistry registry,
        int historyLimit = DefaultHistoryLimit)
    {
        Variant = variant ?? throw new ArgumentNullException(nameof(variant));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentOutOfRangeException.ThrowIfLessThan(historyLimit, 1);
        _historyLimit = historyLimit;
        Addresses = new V2AddressCodec(variant, registry);
        if (!Addresses.IsAddressable(variant.Landing))
        {
            throw new ArgumentException($"{variant.Token} lands on a route it has no address for.", nameof(variant));
        }

        Current = new(new(variant.Landing), null, null, null);
    }

    public event EventHandler<V2NavigationChange>? Navigated;

    public V2ShellVariantDefinition Variant { get; }

    public V2AddressCodec Addresses { get; }

    public V2HistoryEntry Current { get; private set; }

    public string CurrentAddress => Addresses.Format(Current.Location);

    public V2NavigationContext Context { get; private set; } = V2NavigationContext.Empty;

    public IReadOnlyList<V2HistoryEntry> BackEntries => _back;

    public IReadOnlyList<V2HistoryEntry> ForwardEntries => _forward;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>
    /// The destination the current page belongs to, or null when none of the variant's labels
    /// covers it (Variant B's Search results).
    /// </summary>
    public V2RouteId? CurrentDestination => DestinationOf(Current.Location.Route);

    public V2RouteId? DestinationOf(V2RouteId route)
    {
        if (!Variant.Addresses.TryGetValue(route, out var path))
        {
            return null;
        }

        var first = path.Split('/')[0];
        return Variant.Destinations
            .Select(destination => destination.Route)
            .Append(Variant.Setup.Route)
            .Where(candidate => Variant.Addresses.TryGetValue(candidate, out var candidatePath) &&
                                string.Equals(candidatePath, first, StringComparison.Ordinal))
            .Select(candidate => (V2RouteId?)candidate)
            .FirstOrDefault();
    }

    /// <summary>Goes to a route's page. Items open through <see cref="OpenIntel"/>, which knows where.</summary>
    public V2NavigationResult Navigate(V2RouteId route, string? invoker = null)
    {
        if (!_registry.TryGet(route, out var definition) || !Addresses.IsAddressable(route))
        {
            return V2NavigationResult.Refused($"{Variant.Token} has no page for '{route}'.");
        }

        if (definition.TakesItem)
        {
            return V2NavigationResult.Refused("An item opens through Intel, which decides where it opens.");
        }

        return Push(new(route), selectedEntity: null, invoker, new(PageHeadingTarget, V2FocusReason.PageHeading));
    }

    /// <summary>Goes to a deep link, exactly as it was copied from this variant.</summary>
    public V2NavigationResult NavigateToAddress(string? address, string? invoker = null)
    {
        var parsed = Addresses.Parse(address);
        if (parsed.Location is not { } location)
        {
            return V2NavigationResult.Refused(parsed.Failure ?? "That address could not be read.");
        }

        var focus = location.IntelItem is null
            ? new V2FocusRequest(PageHeadingTarget, V2FocusReason.PageHeading)
            : new V2FocusRequest(IntelHeadingTarget, V2FocusReason.IntelHeading);
        return Push(location, location.Item ?? location.IntelItem, invoker, focus);
    }

    /// <summary>Opens Intel on an item, wherever this variant opens it.</summary>
    public V2NavigationResult OpenIntel(string item, string? invoker)
    {
        if (!V2AddressCodec.IsValidItem(item))
        {
            return V2NavigationResult.Refused("That item cannot be addressed.");
        }

        return Variant.IntelPlacement switch
        {
            V2IntelPlacement.Workspace => Push(
                new(V2Routes.Item, Item: item),
                item,
                invoker,
                new(PageHeadingTarget, V2FocusReason.PageHeading)),
            _ => Push(
                Current.Location.WithoutIntel with { IntelItem = item },
                item,
                invoker,
                new(IntelHeadingTarget, V2FocusReason.IntelHeading)),
        };
    }

    /// <summary>
    /// Closes Intel and returns focus to whatever opened it.
    /// </summary>
    /// <remarks>
    /// Beside a page this is a move to the page's own address, so Back reopens Intel the way a
    /// browser's would. As a workspace it is Back, which already restores the page and the focus
    /// that were there.
    /// </remarks>
    public V2NavigationResult CloseIntel()
    {
        if (Variant.IntelPlacement == V2IntelPlacement.Workspace)
        {
            return Current.Location.Route == V2Routes.Item && CanGoBack
                ? Back()
                : V2NavigationResult.Refused("Intel is not open on top of another page.");
        }

        if (Current.Location.IntelItem is null)
        {
            return V2NavigationResult.Refused("Intel is not open.");
        }

        var returnTo = Current.Invoker is { } invoker
            ? new V2FocusRequest(invoker, V2FocusReason.Invoker)
            : new V2FocusRequest(PageHeadingTarget, V2FocusReason.PageHeading);
        return Push(Current.Location.WithoutIntel, selectedEntity: null, invoker: null, returnTo);
    }

    public V2NavigationResult Back() => Step(_back, _forward, V2NavigationKind.Back, "There is nothing to go back to.");

    public V2NavigationResult Forward() => Step(_forward, _back, V2NavigationKind.Forward, "There is nothing to go forward to.");

    /// <summary>Remembers what has focus on the current page, so Back and a restart can return to it.</summary>
    public void RecordFocus(string? target) => Current = Current with { FocusTarget = target };

    /// <summary>Records a selection on the current page without making a history entry.</summary>
    public void Select(string? entity) => Current = Current with { SelectedEntity = entity };

    /// <summary>
    /// Takes the runtime's context. Never history, never focus: a background change is not a move.
    /// </summary>
    public void UpdateContext(V2NavigationContext context) =>
        Context = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>
    /// Returns to a place remembered from an earlier launch, with an empty history.
    /// </summary>
    /// <remarks>
    /// History is deliberately not restored. A Back that leaves the application's first page for
    /// something from yesterday's session is a surprise; the place and its focus are the context
    /// worth keeping.
    /// </remarks>
    public V2NavigationResult Restore(V2ShellLocation location, string? selectedEntity, string? focusTarget)
    {
        ArgumentNullException.ThrowIfNull(location);
        try
        {
            _ = Addresses.Format(location);
        }
        catch (ArgumentException exception)
        {
            return V2NavigationResult.Refused(exception.Message);
        }

        var previous = Current;
        _back.Clear();
        _forward.Clear();
        Current = new(location, selectedEntity, focusTarget, null);
        var focus = new V2FocusRequest(
            focusTarget ?? (location.IntelItem is null ? PageHeadingTarget : IntelHeadingTarget),
            V2FocusReason.Restored);
        Navigated?.Invoke(this, new(previous, Current, V2NavigationKind.Restore, focus));
        return new(true, focus, null);
    }

    private V2NavigationResult Push(V2ShellLocation location, string? selectedEntity, string? invoker, V2FocusRequest focus)
    {
        string address;
        try
        {
            address = Addresses.Format(location);
        }
        catch (ArgumentException exception)
        {
            return V2NavigationResult.Refused(exception.Message);
        }

        if (string.Equals(address, CurrentAddress, StringComparison.Ordinal))
        {
            // Choosing the page you are on is not a move, but it is still the player's action, so
            // focus goes where it would have gone and no duplicate history entry is made.
            return new(true, focus, null);
        }

        var previous = Current with { FocusTarget = invoker ?? Current.FocusTarget };
        _back.Add(previous);
        if (_back.Count > _historyLimit)
        {
            _back.RemoveAt(0);
        }

        _forward.Clear();
        Current = new(location, selectedEntity, null, invoker);
        Navigated?.Invoke(this, new(previous, Current, V2NavigationKind.Push, focus));
        return new(true, focus, null);
    }

    private V2NavigationResult Step(List<V2HistoryEntry> from, List<V2HistoryEntry> to, V2NavigationKind kind, string failure)
    {
        if (from.Count == 0)
        {
            return V2NavigationResult.Refused(failure);
        }

        var previous = Current;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(previous);
        if (to.Count > _historyLimit)
        {
            to.RemoveAt(0);
        }

        Current = target;
        var focus = new V2FocusRequest(
            target.FocusTarget ?? (target.Location.IntelItem is null ? PageHeadingTarget : IntelHeadingTarget),
            V2FocusReason.Restored);
        Navigated?.Invoke(this, new(previous, Current, kind, focus));
        return new(true, focus, null);
    }
}
