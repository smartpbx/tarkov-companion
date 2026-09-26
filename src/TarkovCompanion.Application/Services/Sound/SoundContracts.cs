namespace TarkovCompanion.Application.Services.Sound;

/// <summary>The few things the companion may say out loud (#712 T5). Output only, never input.</summary>
public enum SoundCue
{
    /// <summary>The counted raid clock is down to <see cref="SoundCueTriggers.ApproachingAt"/>.</summary>
    ExtractApproaching = 1,

    /// <summary>The counted raid clock reached zero.</summary>
    ExtractReached,

    /// <summary>A squadmate's own companion shared a new ping or waypoint.</summary>
    SquadPing,

    /// <summary>A loot scan finished; spoken as a short verdict when speech is on.</summary>
    LootVerdict,

    /// <summary>A raid ended and nobody has said how yet.</summary>
    OutcomeQuestion,

    /// <summary>The Test button in Setup.</summary>
    Test,
}

/// <summary>One audio output device the player can pick, as Windows names it.</summary>
public sealed record AudioOutputDevice(string Id, string Name);

/// <summary>How loud, and where to; a null device is the system default.</summary>
public sealed record AudioOutputOptions(double Volume, string? DeviceId);

/// <summary>Plays one short earcon (a RIFF WAV) to the end, or not at all.</summary>
public interface ICueOutput
{
    bool IsAvailable { get; }

    Task PlayAsync(ReadOnlyMemory<byte> wav, AudioOutputOptions options, CancellationToken cancellationToken);
}

/// <summary>Speaks one line to the end, or not at all.</summary>
public interface ISpeechOutput
{
    bool IsAvailable { get; }

    Task SpeakAsync(string text, AudioOutputOptions options, CancellationToken cancellationToken);
}

/// <summary>The output devices sound can go to, so it can reach speakers rather than the comms headset.</summary>
public interface IAudioDeviceCatalog
{
    Task<IReadOnlyList<AudioOutputDevice>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>What the triggers ask for; the service decides whether anything is heard.</summary>
public interface ISoundCues
{
    /// <summary>Queues <paramref name="cue"/>, spoken as <paramref name="spoken"/> where speech is on.</summary>
    /// <returns>False when the settings, the rate limit or the platform keep it silent.</returns>
    bool Play(SoundCue cue, string? spoken = null);
}

/// <summary>The output everywhere but Windows: silent, no devices.</summary>
public sealed class SilentSoundOutput : ICueOutput, ISpeechOutput, IAudioDeviceCatalog
{
    public bool IsAvailable => false;

    public Task PlayAsync(ReadOnlyMemory<byte> wav, AudioOutputOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SpeakAsync(string text, AudioOutputOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<AudioOutputDevice>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AudioOutputDevice>>([]);
}
