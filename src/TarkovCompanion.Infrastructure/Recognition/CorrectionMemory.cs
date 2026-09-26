using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Learning;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>
/// What the companion keeps when the player corrects it (epic #712, 1-12): the icon crop of an
/// item a scan could not name, an OCR reading picked as a different item, and the corrected
/// cell or row of one screenshot.
/// </summary>
/// <remarks>
/// Decision 6: keeping crops is on by default, stays on this PC, and Setup shows the count with a
/// Delete all. Decision 9: recognition stays never wrong, which <see cref="LearnedIconMatchPolicy"/>
/// holds for the crops and the two-pick rule of <see cref="LearnedTextAlias"/> for names. A failure
/// to remember is logged and swallowed: a correction on screen must never fail because the
/// memory of it could not be written.
/// </remarks>
public sealed class CorrectionMemory(
    ICorrectionMemoryStore store,
    IconReferenceIndex? references = null,
    RecentIconCrops? crops = null,
    CanonicalItemResolverCache? resolvers = null,
    IWorkspaceLayoutStore? layout = null,
    TimeProvider? clock = null,
    ILogger<CorrectionMemory>? logger = null)
{
    private readonly ICorrectionMemoryStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger<CorrectionMemory> _logger = logger ?? NullLogger<CorrectionMemory>.Instance;

    /// <summary>Raised after anything learned is added or cleared.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether a correction keeps its icon crop. On unless the player turned it off.</summary>
    public bool KeepsIconCrops
    {
        get => !string.Equals(layout?.Get(WorkspaceLayoutKeys.LearnIconCrops), "off", StringComparison.Ordinal);
        set
        {
            layout?.Set(WorkspaceLayoutKeys.LearnIconCrops, value ? "on" : "off");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Keeps the icon squares of a cell the player just named, where the read still holds them.
    /// False when crops are off, the crop is no longer in memory, or it could not be kept.
    /// </summary>
    public async Task<bool> LearnIconAsync(
        DateTimeOffset observedUtc,
        EvidenceRegion region,
        string itemId,
        int widthCells,
        int heightCells,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (!KeepsIconCrops || crops?.Find(IconCropKey.For(observedUtc, region)) is not { } crop ||
            EncodePng(crop) is not { } png)
        {
            return false;
        }

        try
        {
            await _store.AddIconAsync(
                    new(Guid.NewGuid(), itemId, widthCells, heightCells, png, _clock.GetUtcNow()),
                    cancellationToken)
                .ConfigureAwait(false);
            if (references is not null)
            {
                await references.RefreshLearnedAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not keep a corrected icon.");
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Counts the player's pick of <paramref name="itemId"/> for an OCR reading. The second pick
    /// makes it an alias the name reader uses from then on.
    /// </summary>
    public async Task<LearnedTextAlias?> RecordNamePickAsync(
        string observedText,
        string itemId,
        string itemName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(observedText) || LearnedTextAlias.Normalize(observedText).Length is 0 or > 256)
        {
            return null;
        }

        try
        {
            var alias = await _store.RecordAliasPickAsync(
                    LearnedTextAlias.ItemNameKind,
                    observedText,
                    itemId,
                    itemName,
                    _clock.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (alias.Picks == LearnedTextAlias.PicksToBecomeAlias)
            {
                resolvers?.Invalidate();
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return alias;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not keep a corrected name.");
            return null;
        }
    }

    /// <summary>Remembers that one cell or row of one screenshot was the given item.</summary>
    public async Task RecordFrameCorrectionAsync(string frameSha256, string targetKey, string itemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(frameSha256))
        {
            return;
        }

        try
        {
            await _store.SetFrameCorrectionAsync(frameSha256, targetKey, itemId, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not keep a correction.");
        }
    }

    /// <summary>The corrections kept for one screenshot; empty when none, or when they cannot be read.</summary>
    public async Task<IReadOnlyDictionary<string, string>> FrameCorrectionsAsync(string? frameSha256, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(frameSha256))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return await _store.ListFrameCorrectionsAsync(frameSha256, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not read kept corrections.");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public async Task<CorrectionMemoryCounts> CountAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _store.CountAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not count what was learned.");
            return CorrectionMemoryCounts.None;
        }
    }

    /// <summary>Forgets one kind of learned data; the matcher and the name reader stop using it at once.</summary>
    public async Task ClearAsync(CorrectionMemoryKind kind, CancellationToken cancellationToken)
    {
        await _store.ClearAsync(kind, cancellationToken).ConfigureAwait(false);
        switch (kind)
        {
            case CorrectionMemoryKind.Icons:
                crops?.Clear();
                if (references is not null)
                {
                    await references.RefreshLearnedAsync(cancellationToken).ConfigureAwait(false);
                }

                break;
            case CorrectionMemoryKind.Names:
                resolvers?.Invalidate();
                break;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Delete all: every crop, name and kept correction.</summary>
    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        foreach (var kind in Enum.GetValues<CorrectionMemoryKind>())
        {
            await ClearAsync(kind, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The crop as a PNG, or null when its pixels cannot be encoded.</summary>
    internal static byte[]? EncodePng(CapturedImage crop)
    {
        var colorType = crop.Format switch
        {
            PixelFormat.Bgra8888 => SKColorType.Bgra8888,
            PixelFormat.Rgba8888 => SKColorType.Rgba8888,
            PixelFormat.Gray8 => SKColorType.Gray8,
            _ => SKColorType.Unknown,
        };
        if (colorType == SKColorType.Unknown || crop.Width <= 0 || crop.Height <= 0)
        {
            return null;
        }

        using var bitmap = new SKBitmap(new SKImageInfo(crop.Width, crop.Height, colorType, SKAlphaType.Unpremul));
        var rowBytes = bitmap.RowBytes;
        var source = crop.Pixels.Span;
        var destination = new byte[rowBytes * crop.Height];
        var copy = Math.Min(rowBytes, crop.Stride);
        for (var row = 0; row < crop.Height; row++)
        {
            source.Slice(row * crop.Stride, copy).CopyTo(destination.AsSpan(row * rowBytes, copy));
        }

        Marshal.Copy(destination, 0, bitmap.GetPixels(), destination.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}
