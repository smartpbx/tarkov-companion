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
    private readonly string _leasePath;

    public JsonFileDesktopCompanionAuthorityStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _leasePath = _path + ".lock";
    }

    public ValueTask<IDisposable> AcquireExclusiveLeaseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_leasePath)
                ?? throw new InvalidOperationException("The paired-device authority path has no parent directory."));
            return ValueTask.FromResult<IDisposable>(new FileStream(
                _leasePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.Asynchronous));
        }
        catch (IOException exception)
        {
            throw new IOException("The paired-device authority is already owned by another desktop instance.", exception);
        }
    }

    public async ValueTask<DesktopCompanionAuthorityState?> LoadAsync(CancellationToken cancellationToken)
    {
        byte[]? bytes = null;
        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var initialLength = stream.Length;
            if (initialLength is <= 0 or > MaximumDocumentBytes)
            {
                throw new InvalidDataException("The paired-device authority document is empty or exceeds 4 MiB.");
            }

            bytes = new byte[MaximumDocumentBytes + 1];
            var bytesRead = 0;
            while (bytesRead < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(bytesRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bytesRead += read;
            }

            if (bytesRead is <= 0 or > MaximumDocumentBytes || stream.Length != initialLength || bytesRead != initialLength)
            {
                throw new InvalidDataException("The paired-device authority document changed or exceeds 4 MiB while it was read.");
            }

            var document = JsonSerializer.Deserialize<PersistedAuthorityDocument>(bytes.AsSpan(0, bytesRead), Options)
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
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or
                                              InvalidOperationException or NotSupportedException or
                                              OverflowException or KeyNotFoundException)
        {
            throw new InvalidDataException("The paired-device authority document is invalid.", exception);
        }
        finally
        {
            if (bytes is not null)
            {
                Array.Clear(bytes);
            }
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
