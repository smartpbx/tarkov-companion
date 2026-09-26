using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.Application.Services.Sound;

/// <summary>What the player chose in Setup › Notifications › Sound.</summary>
/// <remarks>
/// [#712 decision 1] <see cref="Enabled"/> starts off: the companion is silent until the player
/// turns sound on. The per-cue switches start on so that one switch is all it takes, except the
/// outcome question, which is a nicety rather than something worth a sound mid-session.
/// </remarks>
public sealed record SoundSettings
{
    public const int DefaultVolume = 60;

    public static SoundSettings Default { get; } = new();

    public bool Enabled { get; init; }

    /// <summary>0 to 100.</summary>
    public int Volume { get; init; } = DefaultVolume;

    /// <summary>A Windows device id, or null for the system default.</summary>
    public string? DeviceId { get; init; }

    public bool SpeakLootVerdicts { get; init; } = true;

    public bool ExtractDeadline { get; init; } = true;

    public bool SquadPing { get; init; } = true;

    public bool LootScan { get; init; } = true;

    public bool OutcomeQuestion { get; init; }

    /// <summary>Whether <paramref name="cue"/> may be heard at all; the master switch first.</summary>
    public bool Allows(SoundCue cue) => Enabled && cue switch
    {
        SoundCue.ExtractApproaching or SoundCue.ExtractReached => ExtractDeadline,
        SoundCue.SquadPing => SquadPing,
        SoundCue.LootVerdict => LootScan,
        SoundCue.OutcomeQuestion => OutcomeQuestion,
        SoundCue.Test => true,
        _ => false,
    };

    public AudioOutputOptions Output => new(Math.Clamp(Volume, 0, 100) / 100.0, string.IsNullOrWhiteSpace(DeviceId) ? null : DeviceId);
}

/// <summary>Reads and saves <see cref="SoundSettings"/> as one workspace-layout entry.</summary>
/// <remarks>
/// One key rather than a store of its own: the layout store is already registered with Backup
/// &amp; reset (#902), so Reset this section, Export and Import cover sound with one registry line.
/// A value that cannot be read is the default, which is silent.
/// </remarks>
public sealed class SoundSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IWorkspaceLayoutStore? _layout;
    private SoundSettings _current;

    public SoundSettingsStore(IWorkspaceLayoutStore? layout)
    {
        _layout = layout;
        _current = Read();
        if (layout is not null)
        {
            layout.Replaced += (_, _) => Reload();
        }
    }

    /// <summary>Raised after a save or a Backup &amp; reset replaced the layout.</summary>
    public event EventHandler? Changed;

    public SoundSettings Current => Volatile.Read(ref _current);

    public void Save(SoundSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _current, settings);
        _layout?.Set(WorkspaceLayoutKeys.SoundSettings, JsonSerializer.Serialize(settings, Json));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Func<SoundSettings, SoundSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Save(change(Current));
    }

    /// <summary>Parses a stored value; anything unreadable is the silent default.</summary>
    public static SoundSettings Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return SoundSettings.Default;
        }

        try
        {
            return JsonSerializer.Deserialize<SoundSettings>(stored, Json) ?? SoundSettings.Default;
        }
        catch (JsonException)
        {
            return SoundSettings.Default;
        }
    }

    private SoundSettings Read() => Parse(_layout?.Get(WorkspaceLayoutKeys.SoundSettings));

    private void Reload()
    {
        Volatile.Write(ref _current, Read());
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
