#if WINDOWS
using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;
#endif
using TarkovCompanion.Application.Services.Sound;

namespace TarkovCompanion.Platform.Windows.Sound;

/// <summary>The narrow, replaceable call into Windows audio: list devices, play a WAV, speak a line.</summary>
public interface IWindowsAudioBackend
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<AudioOutputDevice>> ListDevicesAsync(CancellationToken cancellationToken);

    Task PlayWavAsync(byte[] wav, double volume, string? deviceId, CancellationToken cancellationToken);

    Task SpeakAsync(string text, double volume, string? deviceId, CancellationToken cancellationToken);
}

/// <summary>[#712 0-10] Earcons and speech through Windows' own media player and speech synthesiser.</summary>
/// <remarks>
/// <para>
/// No package: <c>Windows.Media.Playback</c>, <c>Windows.Media.SpeechSynthesis</c> and
/// <c>Windows.Devices.Enumeration</c> come with the Windows target framework the toast channel
/// already builds for. Output only; nothing here opens a capture device.
/// </para>
/// <para>
/// A chosen device that has gone (a headset unplugged) falls back to the system default rather
/// than going silent, and any failure is swallowed: sound is a courtesy, never a crash.
/// </para>
/// </remarks>
public sealed class WindowsSoundOutput(IWindowsAudioBackend? backend = null) : ICueOutput, ISpeechOutput, IAudioDeviceCatalog
{
    /// <summary>The longest any one cue or line may hold the queue.</summary>
    public static readonly TimeSpan MaximumPlayback = TimeSpan.FromSeconds(15);

    private readonly IWindowsAudioBackend _backend = backend ?? new WinRtAudioBackend();

    public bool IsAvailable => _backend.IsAvailable;

    public async Task<IReadOnlyList<AudioOutputDevice>> ListAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return [];
        }

        try
        {
            return await _backend.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            return [];
        }
    }

    public Task PlayAsync(ReadOnlyMemory<byte> wav, AudioOutputOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var bytes = wav.ToArray();
        return RunAsync((volume, device, token) => _backend.PlayWavAsync(bytes, volume, device, token), options, cancellationToken);
    }

    public Task SpeakAsync(string text, AudioOutputOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.CompletedTask;
        }

        return RunAsync((volume, device, token) => _backend.SpeakAsync(text.Trim(), volume, device, token), options, cancellationToken);
    }

    private async Task RunAsync(Func<double, string?, CancellationToken, Task> play, AudioOutputOptions options, CancellationToken cancellationToken)
    {
        var volume = Math.Clamp(options.Volume, 0, 1);
        if (!IsAvailable || volume <= 0)
        {
            return;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(MaximumPlayback);
        try
        {
            await play(volume, options.DeviceId, limit.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (options.DeviceId is not null && !cancellationToken.IsCancellationRequested
            && exception is not OutOfMemoryException and not OperationCanceledException)
        {
            // The chosen device is gone; the default one is better than nothing.
            try
            {
                await play(volume, null, limit.Token).ConfigureAwait(false);
            }
            catch (Exception fallback) when (fallback is not OutOfMemoryException and not OperationCanceledException)
            {
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
        }
    }

    private sealed class WinRtAudioBackend : IWindowsAudioBackend
    {
#if WINDOWS
        public bool IsAvailable => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

        public async Task<IReadOnlyList<AudioOutputDevice>> ListDevicesAsync(CancellationToken cancellationToken)
        {
            var found = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector()).AsTask(cancellationToken).ConfigureAwait(false);
            return [.. found.Where(device => device.IsEnabled).Select(device => new AudioOutputDevice(device.Id, device.Name))];
        }

        public async Task PlayWavAsync(byte[] wav, double volume, string? deviceId, CancellationToken cancellationToken)
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(wav);
                await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
                writer.DetachStream();
            }

            await PlayStreamAsync(stream, "audio/wav", volume, deviceId, cancellationToken).ConfigureAwait(false);
        }

        public async Task SpeakAsync(string text, double volume, string? deviceId, CancellationToken cancellationToken)
        {
            using var synthesizer = new SpeechSynthesizer();
            using var speech = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(cancellationToken).ConfigureAwait(false);
            await PlayStreamAsync(speech, speech.ContentType, volume, deviceId, cancellationToken).ConfigureAwait(false);
        }

        private static async Task PlayStreamAsync(IRandomAccessStream stream, string contentType, double volume, string? deviceId, CancellationToken cancellationToken)
        {
            using var player = new MediaPlayer
            {
                Volume = volume,
                AutoPlay = false,
                AudioCategory = MediaPlayerAudioCategory.Alerts,
            };
            // Not a media session: no entry in the Windows volume flyout's media controls.
            player.CommandManager.IsEnabled = false;
            if (deviceId is not null)
            {
                player.AudioDevice = await DeviceInformation.CreateFromIdAsync(deviceId).AsTask(cancellationToken).ConfigureAwait(false);
            }

            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.MediaEnded += (_, _) => finished.TrySetResult();
            player.MediaFailed += (_, failed) => finished.TrySetException(new IOException(failed.ErrorMessage));
            stream.Seek(0);
            using var source = MediaSource.CreateFromStream(stream, contentType);
            player.Source = source;
            player.Play();
            try
            {
                await finished.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                player.Pause();
                player.Source = null;
            }
        }
#else
        // Linux compiles this target to test the platform contract without WinRT; the installed
        // Windows build resolves the implementation above.
        public bool IsAvailable => false;

        public Task<IReadOnlyList<AudioOutputDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AudioOutputDevice>>([]);

        public Task PlayWavAsync(byte[] wav, double volume, string? deviceId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SpeakAsync(string text, double volume, string? deviceId, CancellationToken cancellationToken) => Task.CompletedTask;
#endif
    }
}
