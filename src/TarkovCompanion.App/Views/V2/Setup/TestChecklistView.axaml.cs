using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.App.Views.V2.Setup;

/// <summary>Setup › Test checklist. Hands the page this window's clipboard and save picker, and writes waiting notes when it closes.</summary>
public sealed partial class TestChecklistView : UserControl
{
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
