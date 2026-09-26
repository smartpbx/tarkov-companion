using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Sound;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One per-cue switch in the Sound card.</summary>
public sealed class SetupSoundCueRowViewModel : BindableViewModel
{
    private readonly Func<SoundSettings, bool> _read;
    private readonly Func<SoundSettings, bool, SoundSettings> _write;
    private readonly SoundSettingsStore _store;
    private bool _isOn;
    private bool _canChange;

    public SetupSoundCueRowViewModel(
        string id,
        string title,
        string hint,
        SoundSettingsStore store,
        Func<SoundSettings, bool> read,
        Func<SoundSettings, bool, SoundSettings> write)
    {
        Title = title;
        Hint = hint;
        AutomationId = $"v2-setup-sound-{id}";
        _store = store;
        _read = read;
        _write = write;
        Refresh();
    }

    public string Title { get; }

    public string Hint { get; }

    public string AutomationId { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (SetProperty(ref _isOn, value))
            {
                _store.Update(settings => _write(settings, value));
            }
        }
    }

    /// <summary>Greyed while the master switch is off: the row keeps its choice for when sound comes back.</summary>
    public bool CanChange
    {
        get => _canChange;
        private set => SetProperty(ref _canChange, value);
    }

    public void Refresh()
    {
        var settings = _store.Current;
        // The fields, not the properties: the property saves, and a reload must not write back.
        if (_isOn != _read(settings))
        {
            _isOn = _read(settings);
            OnPropertyChanged(nameof(IsOn));
        }

        CanChange = settings.Enabled;
    }
}

/// <summary>One volume step.</summary>
public sealed class SetupSoundVolumeChoiceViewModel(int percent, bool isCurrent, Action choose)
{
    public string Label { get; } = SetupText.SoundVolumePercent(percent);

    public int Percent { get; } = percent;

    public bool IsCurrent { get; } = isCurrent;

    public string AutomationId { get; } = $"v2-setup-sound-volume-{percent}";

    public ICommand ChooseCommand { get; } = new DelegateCommand(choose);
}

/// <summary>One output device in the picker; a null id is the system default.</summary>
public sealed record SetupSoundDeviceChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// [#712 0-10] Setup › Notifications › Sound: the master switch (off by default, decision 1), one
/// switch per cue, spoken loot verdicts, volume, output device and a Test button.
/// </summary>
/// <remarks>
/// The device picker exists so the companion can go to speakers while comms stay on the headset.
/// Volume is steps rather than a slider for the reason Loot Scan's return choice is: the value in
/// force reads at a glance.
/// </remarks>
public sealed class SetupSoundViewModel : BindableViewModel
{
    private static readonly int[] VolumeSteps = [20, 40, 60, 80, 100];
    private readonly SoundSettingsStore _store;
    private readonly ISoundCues _sound;
    private readonly ISoundLines _lines;
    private readonly IAudioDeviceCatalog? _devices;
    private readonly Action<Action> _post;
    private bool _enabled;
    private bool _speak;
    private IReadOnlyList<SetupSoundVolumeChoiceViewModel> _volumeChoices = [];
    private IReadOnlyList<SetupSoundDeviceChoice> _deviceChoices = [new(null, SetupText.SoundDefaultDevice)];
    private SetupSoundDeviceChoice? _selectedDevice;

    public SetupSoundViewModel(
        SoundSettingsStore store,
        ISoundCues sound,
        ISoundLines lines,
        IAudioDeviceCatalog? devices,
        bool isAvailable,
        Action<Action>? post = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sound = sound ?? throw new ArgumentNullException(nameof(sound));
        _lines = lines ?? throw new ArgumentNullException(nameof(lines));
        _devices = devices;
        _post = post ?? (action => action());
        IsAvailable = isAvailable;
        Cues =
        [
            new("deadline", SetupText.SoundCueDeadline, SetupText.SoundCueDeadlineHint, store, s => s.ExtractDeadline, (s, on) => s with { ExtractDeadline = on }),
            new("ping", SetupText.SoundCuePing, SetupText.SoundCuePingHint, store, s => s.SquadPing, (s, on) => s with { SquadPing = on }),
            new("loot", SetupText.SoundCueLoot, SetupText.SoundCueLootHint, store, s => s.LootScan, (s, on) => s with { LootScan = on }),
            new("outcome", SetupText.SoundCueOutcome, SetupText.SoundCueOutcomeHint, store, s => s.OutcomeQuestion, (s, on) => s with { OutcomeQuestion = on }),
        ];
        TestCommand = new DelegateCommand(() => Test());
        ReadStored();
        store.Changed += (_, _) => _post(ReadStored);
        _ = LoadDevicesAsync();
    }

    public string Heading => SetupText.SoundHeading;

    public string Intro => SetupText.SoundIntro;

    public string MasterLabel => SetupText.SoundMaster;

    public string MasterHint => SetupText.SoundMasterHint;

    public string SpeakLabel => SetupText.SoundSpeak;

    public string SpeakHint => SetupText.SoundSpeakHint;

    public string VolumeLabel => SetupText.SoundVolume;

    public string DeviceLabel => SetupText.SoundDevice;

    public string TestLabel => SetupText.SoundTest;

    public string UnavailableNote => SetupText.SoundUnavailable;

    /// <summary>False where there is no audio output (Linux, a headless build): the card says so.</summary>
    public bool IsAvailable { get; }

    public bool IsUnavailable => !IsAvailable;

    public IReadOnlyList<SetupSoundCueRowViewModel> Cues { get; }

    public ICommand TestCommand { get; }

    /// <summary>The master switch. Off by default; while off, nothing is ever heard.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                _store.Update(settings => settings with { Enabled = value });
            }
        }
    }

    public bool SpeakLootVerdicts
    {
        get => _speak;
        set
        {
            if (SetProperty(ref _speak, value))
            {
                _store.Update(settings => settings with { SpeakLootVerdicts = value });
            }
        }
    }

    public IReadOnlyList<SetupSoundVolumeChoiceViewModel> VolumeChoices
    {
        get => _volumeChoices;
        private set => SetProperty(ref _volumeChoices, value);
    }

    public IReadOnlyList<SetupSoundDeviceChoice> DeviceChoices
    {
        get => _deviceChoices;
        private set => SetProperty(ref _deviceChoices, value);
    }

    public SetupSoundDeviceChoice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value is null || !SetProperty(ref _selectedDevice, value))
            {
                return;
            }

            if (!string.Equals(_store.Current.DeviceId, value.Id, StringComparison.Ordinal))
            {
                _store.Update(settings => settings with { DeviceId = value.Id });
            }
        }
    }

    /// <summary>Plays the test tone and line through the same gate as every cue, so off stays silent.</summary>
    public bool Test() => _sound.Play(SoundCue.Test, _lines.Test);

    public void ChooseVolume(int percent)
    {
        if (_store.Current.Volume == percent)
        {
            return;
        }

        _store.Update(settings => settings with { Volume = percent });
        RebuildVolume();
    }

    public async Task LoadDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (_devices is null)
        {
            return;
        }

        IReadOnlyList<AudioOutputDevice> found;
        try
        {
            found = await _devices.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            // No list is the default device only, which is what the player hears anyway.
            return;
        }

        _post(() =>
        {
            DeviceChoices = [new(null, SetupText.SoundDefaultDevice), .. found.Select(device => new SetupSoundDeviceChoice(device.Id, device.Name))];
            SelectStoredDevice();
        });
    }

    /// <summary>[#902] A save or Backup &amp; reset changed the settings: show what they hold now.</summary>
    private void ReadStored()
    {
        var settings = _store.Current;
        if (_enabled != settings.Enabled)
        {
            _enabled = settings.Enabled;
            OnPropertyChanged(nameof(Enabled));
        }

        if (_speak != settings.SpeakLootVerdicts)
        {
            _speak = settings.SpeakLootVerdicts;
            OnPropertyChanged(nameof(SpeakLootVerdicts));
        }

        foreach (var row in Cues)
        {
            row.Refresh();
        }

        RebuildVolume();
        SelectStoredDevice();
    }

    private void SelectStoredDevice()
    {
        var id = _store.Current.DeviceId;
        var match = DeviceChoices.FirstOrDefault(choice => string.Equals(choice.Id, id, StringComparison.Ordinal))
            ?? DeviceChoices[0];
        if (!Equals(match, _selectedDevice))
        {
            _selectedDevice = match;
            OnPropertyChanged(nameof(SelectedDevice));
        }
    }

    private void RebuildVolume()
    {
        var current = _store.Current.Volume;
        VolumeChoices = [.. VolumeSteps.Select(step => new SetupSoundVolumeChoiceViewModel(step, step == current, () => ChooseVolume(step)))];
    }
}
