using Avalonia;
using Avalonia.Controls;

namespace TarkovCompanion.App.Views.V2.Shell;

/// <summary>
/// Shows one workspace view model at a time, keeping the view it built for each one it has shown.
/// </summary>
/// <remarks>
/// [#453] The shell used to show its workspaces through one <see cref="ContentControl"/>, so every
/// route change threw the page it left away and built the page it came to from its template: the
/// Plan page, with its map, its groups and thirty objective cards, cost about 900 ms of interface
/// thread on every return to it. A view that is kept and hidden is free (a control that is not
/// visible is neither measured nor drawn) and comes back as it was left, scroll position included.
///
/// Keyed by view model instance, and bounded, so a workspace whose view model is ever replaced
/// does not keep its old view alive for the rest of the session.
/// </remarks>
public sealed class WorkspaceViewCache : Panel
{
    /// <summary>The view model whose view is shown; null shows nothing.</summary>
    public static readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<WorkspaceViewCache, object?>(nameof(Content));

    /// <summary>More workspaces than the shell has routes for, so in practice nothing is evicted.</summary>
    internal const int Capacity = 16;

    // Most recently shown last.
    private readonly List<ContentControl> _hosts = [];

    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    /// <summary>How many views are kept, shown or not.</summary>
    internal int KeptCount => _hosts.Count;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty)
        {
            Show(change.NewValue);
        }
    }

    private void Show(object? content)
    {
        ContentControl? shown = null;
        foreach (var host in _hosts)
        {
            if (content is not null && ReferenceEquals(host.Content, content))
            {
                shown = host;
            }
            else
            {
                host.IsVisible = false;
            }
        }

        if (content is null)
        {
            return;
        }

        if (shown is null)
        {
            shown = new ContentControl { Content = content };
            _hosts.Add(shown);
            Children.Add(shown);
            if (_hosts.Count > Capacity)
            {
                var oldest = _hosts[0];
                _hosts.RemoveAt(0);
                Children.Remove(oldest);
            }
        }
        else
        {
            _hosts.Remove(shown);
            _hosts.Add(shown);
        }

        shown.IsVisible = true;
    }
}
