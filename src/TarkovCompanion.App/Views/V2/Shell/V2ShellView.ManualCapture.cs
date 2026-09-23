using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.App.Views.V2.Shell;

/// <summary>
/// [f920 capture] Paste, drop and pick: the three ways a picture the player already has reaches
/// the capture intake without the game writing a file.
/// </summary>
/// <remarks>
/// All three read something the player chose and handed to this window: the clipboard on their
/// own Ctrl+V, a file they dragged in, a file they picked. Nothing here looks at another window
/// or sends anything to one.
/// </remarks>
public sealed partial class V2ShellView
{
    private void AttachManualCapture()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, ManualCaptureDragOver);
        AddHandler(DragDrop.DropEvent, ManualCaptureDrop);
        AddHandler(KeyDownEvent, ManualCaptureKeyDown, RoutingStrategies.Bubble);
    }

    private async Task<IReadOnlyList<string>> PickCaptureImageAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
        {
            return [];
        }

        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Choose screenshots",
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        }).ConfigureAwait(true);
        return [.. picked.Select(file => file.TryGetLocalPath()).Where(path => path is not null).Cast<string>()];
    }

    private void ManualCaptureDragOver(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.DragEffects = eventArgs.DataTransfer.Contains(DataFormat.File) || eventArgs.DataTransfer.Contains(DataFormat.Bitmap)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void ManualCaptureDrop(object? sender, DragEventArgs eventArgs)
    {
        if (_wiredShell is not { } shell)
        {
            return;
        }

        var paths = eventArgs.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .Where(path => path is not null)
            .Cast<string>()
            .ToArray() ?? [];
        if (paths.Length > 0)
        {
            SubmitFiles(shell, V2ManualImageOrigin.Drop, paths);
            eventArgs.Handled = true;
        }
        else if (eventArgs.DataTransfer.TryGetBitmap() is { } bitmap)
        {
            using (bitmap)
            {
                Submit(shell, V2ManualImageOrigin.Drop, null, ToCapturedImage(bitmap, "dropped-image"));
            }

            eventArgs.Handled = true;
        }
    }

    /// <remarks>
    /// Ctrl+V in a text box is the text box's own paste and is left alone. Anywhere else in the
    /// shell it means "read the picture I copied".
    /// </remarks>
    private async void ManualCaptureKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Handled ||
            eventArgs.Key != Key.V ||
            eventArgs.KeyModifiers != KeyModifiers.Control ||
            eventArgs.Source is TextBox ||
            _wiredShell is not { } shell ||
            TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            var files = await clipboard.TryGetFilesAsync().ConfigureAwait(true);
            var paths = files?
                .Select(item => item.TryGetLocalPath())
                .Where(path => path is not null)
                .Cast<string>()
                .ToArray() ?? [];
            if (paths.Length > 0)
            {
                SubmitFiles(shell, V2ManualImageOrigin.Paste, paths);
                return;
            }

            using var bitmap = await clipboard.TryGetBitmapAsync().ConfigureAwait(true);
            if (bitmap is not null)
            {
                Submit(shell, V2ManualImageOrigin.Paste, null, ToCapturedImage(bitmap, "clipboard-image"));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or COMException)
        {
            shell.ReportManualImage("The clipboard could not be read");
        }
    }

    private static void Submit(V2ShellViewModel shell, V2ManualImageOrigin origin, string? path, CapturedImage? pixels)
    {
        if (!shell.IsCaptureOpen)
        {
            // The panel is where the progress and the answer's review appear.
            shell.CaptureCommand.Execute(null);
        }

        shell.SubmitManualImage(origin, path, pixels);
    }

    private static void SubmitFiles(
        V2ShellViewModel shell,
        V2ManualImageOrigin origin,
        IReadOnlyList<string> paths)
    {
        if (!shell.IsCaptureOpen)
        {
            shell.CaptureCommand.Execute(null);
        }

        if (paths.Count == 1)
        {
            shell.SubmitManualImage(origin, paths[0], null);
            return;
        }

        shell.SubmitManualImages(
            origin,
            [.. paths.Select(path => new V2ManualImageItem(
                Guid.NewGuid().ToString("N"),
                Path.GetFileName(path),
                path,
                null))]);
    }

    /// <summary>Copies a bitmap into the BGRA buffer the recogniser reads. Nothing is written to disk.</summary>
    private static CapturedImage ToCapturedImage(Bitmap bitmap, string source)
    {
        var size = bitmap.PixelSize;
        using var target = new WriteableBitmap(size, new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var frame = target.Lock();
        bitmap.CopyPixels(frame);
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        for (var row = 0; row < size.Height; row++)
        {
            Marshal.Copy(frame.Address + (row * frame.RowBytes), pixels, row * stride, stride);
        }

        return new(pixels, size.Width, size.Height, stride, TarkovCompanion.Core.Domain.Recognition.PixelFormat.Bgra8888, DateTimeOffset.UtcNow, source);
    }
}
