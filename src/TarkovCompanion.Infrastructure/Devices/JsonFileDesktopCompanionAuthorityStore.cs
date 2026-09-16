using System.Text;
using System.Text.Json;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.Infrastructure.Devices;

/// <summary>
/// Persists the desktop authority as one bounded, atomic JSON document.
/// </summary>
/// <remarks>
/// <see cref="CanonicalCompanionState.RecentCommands"/> and its receipt horizon are intentionally
/// omitted from wire snapshots, but they are required here. Losing either on restart would let an
/// evicted offline command apply twice, so the persistence projection carries them explicitly.
/// </remarks>
public sealed class JsonFileDesktopCompanionAuthorityStore : IDesktopCompanionAuthorityStore
{
    public const int FormatVersion = 1;
    public const int MaximumDocumentBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private readonly string _path;

    public JsonFileDesktopCompanionAuthorityStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async ValueTask<DesktopCompanionAuthorityState?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var info = new FileInfo(_path);
        if (info.Length is <= 0 or > MaximumDocumentBytes)
        {
            throw new InvalidDataException("The paired-device authority document is empty or exceeds 4 MiB.");
        }

        var bytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        try
        {
            var document = JsonSerializer.Deserialize<PersistedAuthorityDocument>(bytes, Options)
                ?? throw new InvalidDataException("The paired-device authority document is empty.");
            if (document.FormatVersion != FormatVersion)
            {
                throw new InvalidDataException(
                    $"Paired-device authority format {document.FormatVersion} is unsupported.");
            }

            return new DesktopCompanionAuthorityState(
                document.CanonicalState.ToCanonicalState(),
                document.Devices,
                document.Sessions,
                document.DeliveryLedger);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or
                                              InvalidOperationException or NotSupportedException or
                                              OverflowException or KeyNotFoundException)
        {
            throw new InvalidDataException("The paired-device authority document is invalid.", exception);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    public async ValueTask SaveAsync(
        DesktopCompanionAuthorityState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var document = new PersistedAuthorityDocument(
            FormatVersion,
            PersistedCanonicalState.From(state.CanonicalState),
            state.Devices,
            state.Sessions,
            state.DeliveryLedger);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        try
        {
            if (bytes.Length > MaximumDocumentBytes)
            {
                throw new InvalidOperationException("The paired-device authority document exceeds 4 MiB.");
            }

            await AtomicJsonFile.WriteAsync(
                _path,
                Encoding.UTF8.GetString(bytes),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(CompanionProtocolJson.Options)
        {
            MaxDepth = 24,
            WriteIndented = false,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed record PersistedAuthorityDocument(
        int FormatVersion,
        PersistedCanonicalState CanonicalState,
        IReadOnlyList<PairedDevice> Devices,
        IReadOnlyList<DeviceSession> Sessions,
        DeliveryLedger DeliveryLedger);

    private sealed record PersistedCanonicalState(
        AuthorityEpoch AuthorityEpoch,
        WorkspaceId WorkspaceId,
        string DesktopInstanceId,
        GlobalRevision GlobalRevision,
        CompanionDeviceId DesktopDeviceId,
        DeviceModeAggregate DeviceModes,
        WorkspaceAggregate Workspace,
        MarkAggregate Marks,
        CaptureIntentAggregate CaptureIntent,
        ProfilePreferencesAggregate ProfilePreferences,
        IReadOnlyList<RecentCommandReceipt> RecentCommands,
        DateTimeOffset? ReceiptHorizonUtc)
    {
        public static PersistedCanonicalState From(CanonicalCompanionState state) =>
            new(
                state.AuthorityEpoch,
                state.WorkspaceId,
                state.DesktopInstanceId,
                state.GlobalRevision,
                state.DesktopDeviceId,
                state.DeviceModes,
                state.Workspace,
                state.Marks,
                state.CaptureIntent,
                state.ProfilePreferences,
                state.RecentCommands,
                state.ReceiptHorizonUtc);

        public CanonicalCompanionState ToCanonicalState() =>
            new(
                AuthorityEpoch,
                WorkspaceId,
                DesktopInstanceId,
                GlobalRevision,
                DesktopDeviceId,
                DeviceModes,
                Workspace,
                Marks,
                CaptureIntent,
                ProfilePreferences,
                RecentCommands,
                ReceiptHorizonUtc);
    }
}
