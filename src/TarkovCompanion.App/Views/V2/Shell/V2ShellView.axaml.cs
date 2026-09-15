using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.App.Views.V2.Shell;

/// <summary>Attaches window-owned clipboard, sizing and focus behavior to the provisional shell.</summary>
public sealed partial class V2ShellView : UserControl
{
    private V2ShellViewModel? _wiredShell;
    private bool _attached;
    private bool _requestedInitialFocus;

    public V2ShellView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += DataContextChangedHandler;
        AddHandler(GotFocusEvent, ShellGotFocus, RoutingStrategies.Bubble);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        _attached = true;
        SizeChanged += ShellSizeChanged;
        Wire(DataContext as V2ShellViewModel);
        UpdateWidth();
        RequestInitialFocus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        _attached = false;
        SizeChanged -= ShellSizeChanged;
        Unwire();
        base.OnDetachedFromVisualTree(eventArgs);
    }

    private void DataContextChangedHandler(object? sender, EventArgs eventArgs)
    {
        Unwire();
        if (_attached)
        {
            Wire(DataContext as V2ShellViewModel);
            UpdateWidth();
            RequestInitialFocus();
        }
    }

    private void Wire(V2ShellViewModel? shell)
    {
        if (shell is null || ReferenceEquals(shell, _wiredShell))
        {
            return;
        }

        _wiredShell = shell;
        _requestedInitialFocus = false;
        shell.FocusRequested += FocusRequested;
        shell.Clipboard = CopyToClipboardAsync;
    }

    private void Unwire()
    {
        if (_wiredShell is null)
        {
            return;
        }

        _wiredShell.FocusRequested -= FocusRequested;
        _wiredShell.Clipboard = ClipboardUnavailableAsync;
        _wiredShell = null;
        _requestedInitialFocus = false;
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
            ?? throw new InvalidOperationException("The shell is not attached to a top-level clipboard.");
        await clipboard.SetTextAsync(text).ConfigureAwait(true);
    }

    private static Task ClipboardUnavailableAsync(string _) =>
        Task.FromException(new InvalidOperationException("The shell view is detached."));

    private void ShellSizeChanged(object? sender, SizeChangedEventArgs eventArgs) => UpdateWidth();

    private void UpdateWidth()
    {
        if (_wiredShell is { } shell)
        {
            shell.UpdateEffectiveWidth(Bounds.Width);
        }
    }

    private void RequestInitialFocus()
    {
        if (_requestedInitialFocus || _wiredShell is null)
        {
            return;
        }

        _requestedInitialFocus = true;
        Dispatcher.UIThread.Post(_wiredShell.RequestInitialFocus, DispatcherPriority.Loaded);
    }

    private void FocusRequested(object? sender, V2FocusRequest request)
    {
        if (!ReferenceEquals(sender, _wiredShell))
        {
            return;
        }

        Dispatcher.UIThread.Post(() => FocusTarget(request.Target), DispatcherPriority.Loaded);
    }

    private void FocusTarget(string automationId)
    {
        var target = this.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control =>
                control.IsEffectivelyVisible &&
                control.IsEffectivelyEnabled &&
                string.Equals(AutomationProperties.GetAutomationId(control), automationId, StringComparison.Ordinal));
        if (target is null)
        {
            return;
        }

        // Tab is also what Avalonia's parameterless Focus uses. Supplying it explicitly keeps
        // the focus-visible treatment on for a programmatic move requested by keyboard chrome.
        target.Focus(NavigationMethod.Tab);
        if (target.IsFocused)
        {
            _wiredShell?.RecordFocusedTarget(automationId);
        }
    }

    private void ShellGotFocus(object? sender, GotFocusEventArgs eventArgs)
    {
        if (eventArgs.Source is not StyledElement focused)
        {
            return;
        }

        var automationId = AutomationProperties.GetAutomationId(focused);
        if (automationId?.StartsWith("v2-shell-", StringComparison.Ordinal) == true)
        {
            _wiredShell?.RecordFocusedTarget(automationId);
        }
    }
}
