using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.App.Views.V2.Setup;

/// <summary>
/// Setup › Test checklist. Hands the page this window's clipboard and save picker, writes waiting
/// notes when it closes, and scrolls the list to the row Next untested or an area jump asks for.
/// </summary>
public sealed partial class TestChecklistView : UserControl
{
    private TestChecklistViewModel? _wired;

    public TestChecklistView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Wire();
        AttachedToVisualTree += (_, _) => Wire();
        DetachedFromVisualTree += (_, _) => (DataContext as TestChecklistViewModel)?.FlushNotes();
    }

    private void Wire()
    {
        if (DataContext is not TestChecklistViewModel page)
        {
            return;
        }

        page.Clipboard = CopyAsync;
        page.SaveFile = SaveAsync;
        if (!ReferenceEquals(_wired, page))
        {
            if (_wired is not null)
            {
                _wired.ScrollRequested -= ScrollToRow;
            }

            _wired = page;
            page.ScrollRequested += ScrollToRow;
        }
    }

    /// <summary>
    /// Puts the row at the top of the list. ScrollIntoView alone stops as soon as the row is anywhere
    /// in view, often the last line of the screen, which left the item Next untested found half hidden.
    /// </summary>
    internal void ScrollToRow(int index)
    {
        if (this.FindControl<ItemsControl>("RowList") is not { } list ||
            this.FindControl<ScrollViewer>("ListScroller") is not { } scroller)
        {
            return;
        }

        list.ScrollIntoView(index);
        list.UpdateLayout();
        if (list.ContainerFromIndex(index) is { } row && row.TranslatePoint(default, list) is { } top)
        {
            scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, top.Y));
        }
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    private async Task<bool> SaveAsync(string suggestedName, string contents)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return false;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = TestChecklistText.ExportTitle,
            SuggestedFileName = suggestedName,
            DefaultExtension = "md",
            FileTypeChoices = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }],
        }).ConfigureAwait(true);
        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(contents).ConfigureAwait(true);
        return true;
    }
}
