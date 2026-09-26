using System.Buffers.Binary;

namespace TarkovCompanion.Application.Services.Sound;

/// <summary>Short, quiet, distinct tones for each cue, made in code as 16-bit mono WAV.</summary>
/// <remarks>
/// Generated rather than shipped: no audio files to license, and every cue is a few notes of pure
/// tone with a soft attack and release, so nothing clicks. Peak level is a quarter of full scale
/// before the player's volume, because a cue that startles is a cue that gets switched off.
/// </remarks>
public static class Earcons
{
    public const int SampleRate = 22_050;
    private const double Peak = 0.25;
    private const double FadeSeconds = 0.012;

    /// <summary>The notes of a cue: frequency in Hz (0 is a rest) and length in seconds.</summary>
    public static IReadOnlyList<(double Hz, double Seconds)> Notes(SoundCue cue) => cue switch
    {
        // Two falling notes: time is running down.
        SoundCue.ExtractApproaching => [(784, 0.12), (0, 0.05), (587, 0.16)],
        // Three low repeats: the clock has run out.
        SoundCue.ExtractReached => [(440, 0.12), (0, 0.06), (440, 0.12), (0, 0.06), (440, 0.18)],
        // One bright rising pair: somebody pointed at something.
        SoundCue.SquadPing => [(880, 0.07), (1175, 0.11)],
        // A soft single tick: the scan is done.
        SoundCue.LootVerdict => [(659, 0.09)],
        // A gentle question: up, then up again.
        SoundCue.OutcomeQuestion => [(523, 0.1), (0, 0.04), (659, 0.1), (0, 0.04), (784, 0.14)],
        SoundCue.Test => [(523, 0.1), (659, 0.1), (784, 0.14)],
        _ => [],
    };

    /// <summary>The whole RIFF WAV for <paramref name="cue"/>.</summary>
    public static byte[] Wav(SoundCue cue)
    {
        var samples = new List<short>();
        foreach (var (hz, seconds) in Notes(cue))
        {
            var count = (int)Math.Round(seconds * SampleRate);
            var fade = Math.Max(1, (int)(FadeSeconds * SampleRate));
            for (var index = 0; index < count; index++)
            {
                if (hz <= 0)
                {
                    samples.Add(0);
                    continue;
                }

                var envelope = Math.Min(1.0, Math.Min(index, count - 1 - index) / (double)fade);
                var value = Math.Sin(2 * Math.PI * hz * index / SampleRate) * Peak * envelope;
                samples.Add((short)Math.Round(value * short.MaxValue));
            }
        }

        return Encode(samples);
    }

    private static byte[] Encode(IReadOnlyList<short> samples)
    {
        const int headerLength = 44;
        var dataLength = samples.Count * 2;
        var bytes = new byte[headerLength + dataLength];
        var span = bytes.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataLength);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);
        for (var index = 0; index < samples.Count; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(headerLength + index * 2)..], samples[index]);
        }

        return bytes;
    }
}
