using System.Buffers.Binary;
using TarkovCompanion.App.Services.Sound;
using TarkovCompanion.App.Services.Settings;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Sound;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Domain.Situations;
using TarkovCompanion.Platform.Windows.Sound;

namespace TarkovCompanion.UnitTests.Sound;

/// <summary>[#712 0-10] Sound cues: off by default, once per trigger, one per second, never over each other.</summary>
public sealed class SoundCueTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Nothing_is_heard_until_the_player_turns_sound_on()
    {
        var (_, store, output, service) = Build(enabled: false);

        Assert.False(SoundSettings.Default.Enabled);
        foreach (var cue in Enum.GetValues<SoundCue>())
        {
            Assert.False(service.Play(cue, "Keep: LEDX."));
        }

        await service.Idle;
        Assert.Empty(output.Played);
        Assert.Empty(output.Spoken);

        store.Update(settings => settings with { Enabled = true });
        Assert.True(service.Play(SoundCue.SquadPing));
        await service.Idle;
        Assert.Equal([SoundCue.SquadPing], output.Played);
    }

    [Fact]
    public async Task A_cue_switched_off_stays_silent_while_the_rest_play()
    {
        var (clock, store, output, service) = Build();
        store.Update(settings => settings with { SquadPing = false });

        Assert.False(service.Play(SoundCue.SquadPing));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(service.Play(SoundCue.ExtractApproaching));
        await service.Idle;

        Assert.Equal([SoundCue.ExtractApproaching], output.Played);
    }

    [Fact]
    public async Task No_two_cues_are_accepted_less_than_a_second_apart()
    {
        var (clock, _, output, service) = Build();

        Assert.True(service.Play(SoundCue.SquadPing));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.False(service.Play(SoundCue.ExtractApproaching));
        clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.True(service.Play(SoundCue.ExtractReached));
        await service.Idle;

        Assert.Equal([SoundCue.SquadPing, SoundCue.ExtractReached], output.Played);
    }

    [Fact]
    public async Task A_line_is_never_spoken_over_another_and_the_oldest_waiting_is_dropped()
    {
        var (clock, _, output, service) = Build();
        output.Hold = new TaskCompletionSource();

        Assert.True(service.Play(SoundCue.LootVerdict, "Keep: LEDX, 1.2 million."));
        await output.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 1; index <= 3; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(service.Play(SoundCue.LootVerdict, $"Line {index}."));
        }

        // Still speaking the first: nothing else has started.
        Assert.Equal(["Keep: LEDX, 1.2 million."], output.Spoken);
        Assert.Equal(1, output.MaximumConcurrent);

        output.Hold.SetResult();
        await service.Idle.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["Keep: LEDX, 1.2 million.", "Line 2.", "Line 3."], output.Spoken);
        Assert.Equal(1, output.MaximumConcurrent);
        Assert.All(output.StartTimes.Zip(output.StartTimes.Skip(1)), pair => Assert.True(pair.Second - pair.First >= SoundCueService.MinimumGap));
    }

    [Fact]
    public async Task Turning_sound_off_silences_what_was_waiting()
    {
        var (clock, store, output, service) = Build();
        output.Hold = new TaskCompletionSource();
        service.Play(SoundCue.LootVerdict, "First.");
        await output.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Play(SoundCue.LootVerdict, "Second.");

        store.Update(settings => settings with { Enabled = false });
        output.Hold.SetResult();
        await service.Idle.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["First."], output.Spoken);
    }

    [Fact]
    public async Task A_verdict_is_a_tone_when_speech_is_off()
    {
        var (_, store, output, service) = Build();
        store.Update(settings => settings with { SpeakLootVerdicts = false });

        service.Play(SoundCue.LootVerdict, "Keep: LEDX.");
        await service.Idle;

        Assert.Equal([SoundCue.LootVerdict], output.Played);
        Assert.Empty(output.Spoken);
    }

    [Fact]
    public void The_deadline_sounds_once_approaching_and_once_at_zero()
    {
        var (clock, cues, triggers) = Triggers();
        var raid = Guid.NewGuid();
        var situation = InRaid(raid, left: TimeSpan.FromMinutes(12));

        triggers.CheckClock(situation, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(7));
        triggers.CheckClock(situation, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1));
        triggers.CheckClock(situation, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(4));
        triggers.CheckClock(situation, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1));
        triggers.CheckClock(situation, clock.GetUtcNow());

        Assert.Equal([SoundCue.ExtractApproaching, SoundCue.ExtractReached], cues.Cues);
    }

    [Fact]
    public void A_clock_that_only_counts_up_never_sounds_a_deadline()
    {
        var (clock, cues, triggers) = Triggers();
        var situation = InRaid(Guid.NewGuid(), left: null);

        triggers.CheckClock(situation, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromHours(1));
        triggers.CheckClock(situation, clock.GetUtcNow());

        Assert.Empty(cues.Cues);
    }

    [Fact]
    public void The_outcome_question_sounds_once_per_raid()
    {
        var (clock, cues, triggers) = Triggers();
        var raid = Guid.NewGuid();
        var post = InRaid(raid, left: null) with
        {
            Phase = new(SituationPhase.PostRaid, Confidence.Certain, SituationSource.GameLog, Start, "test"),
        };

        triggers.CheckClock(post, clock.GetUtcNow());
        triggers.CheckClock(post, clock.GetUtcNow());

        Assert.Equal([SoundCue.OutcomeQuestion], cues.Cues);
    }

    [Fact]
    public void A_squadmate_mark_sounds_once_and_the_players_own_and_old_marks_do_not()
    {
        var (_, cues, triggers) = Triggers(self: "Clay");

        triggers.ObserveGroup(Group(Ping(1, "Geo")));
        Assert.Empty(cues.Cues);

        triggers.ObserveGroup(Group(Ping(1, "Geo"), Ping(2, "Clay")));
        Assert.Empty(cues.Cues);

        triggers.ObserveGroup(Group(Ping(1, "Geo"), Ping(2, "Clay"), Ping(3, "Geo")));
        triggers.ObserveGroup(Group(Ping(1, "Geo"), Ping(2, "Clay"), Ping(3, "Geo")));

        Assert.Equal([SoundCue.SquadPing], cues.Cues);
    }

    [Fact]
    public async Task A_scan_is_spoken_once_with_its_short_name_and_value()
    {
        var (_, cues, triggers) = Triggers(shortName: id => id == "ledx" ? "LEDX" : null);
        var scan = Scan("ledx", "LEDX Skin Transilluminator", RecommendationAction.Keep, 1_234_000);

        await triggers.ObserveScanAsync(scan);
        await triggers.ObserveScanAsync(scan);

        Assert.Equal([(SoundCue.LootVerdict, "Keep: LEDX, 1.2 million.")], cues.Spoken);
    }

    [Fact]
    public async Task A_screen_that_is_not_loot_or_an_unsure_item_says_nothing()
    {
        var (_, cues, triggers) = Triggers();
        var flea = Scan("ledx", "LEDX", RecommendationAction.Keep, 1) with { Context = ScanContext.FleaListings };
        var unsure = Scan("ledx", "LEDX", RecommendationAction.Keep, 1, confidence: 0.2);

        await triggers.ObserveScanAsync(flea);
        await triggers.ObserveScanAsync(unsure);

        Assert.Empty(cues.Cues);
    }

    [Theory]
    [InlineData(1_234_000L, "Sell on flea: GPU, 1.2 million.")]
    [InlineData(45_400L, "Sell on flea: GPU, 45 thousand.")]
    [InlineData(800L, "Sell on flea: GPU, 800 roubles.")]
    public void Money_is_said_the_way_a_person_says_it(long value, string expected)
    {
        var line = new LootVerdictLine(LootVerdictWord.SellFlea, "GPU", 1, value);

        Assert.Equal(expected, new LocalizedSoundLines().LootVerdict(line));
    }

    [Fact]
    public void Each_cue_is_a_short_quiet_distinct_wav()
    {
        var sounds = Enum.GetValues<SoundCue>().Select(Earcons.Wav).ToArray();

        foreach (var wav in sounds)
        {
            Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
            Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
            var samples = (wav.Length - 44) / 2;
            Assert.InRange(samples / (double)Earcons.SampleRate, 0.05, 1.0);
            var peak = Enumerable.Range(0, samples).Max(index => Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + index * 2))));
            Assert.InRange(peak, 1, short.MaxValue / 3);
        }

        Assert.Equal(sounds.Length, sounds.Select(Convert.ToBase64String).Distinct().Count());
    }

    [Fact]
    public async Task Windows_output_falls_back_to_the_default_device_when_the_chosen_one_is_gone()
    {
        var backend = new FakeBackend { FailingDevice = "usb-headset" };
        var output = new WindowsSoundOutput(backend);

        await output.PlayAsync(Earcons.Wav(SoundCue.Test), new(0.6, "usb-headset"), CancellationToken.None);
        await output.SpeakAsync("Sound test.", new(4, null), CancellationToken.None);

        Assert.Equal([("wav", "usb-headset"), ("wav", null), ("speak", null)], backend.Calls.Select(call => (call.Kind, call.Device)));
        Assert.Equal(1.0, backend.Calls[^1].Volume);
    }

    [Fact]
    public async Task Windows_output_does_nothing_where_Windows_audio_is_missing()
    {
        var backend = new FakeBackend { Available = false };
        var output = new WindowsSoundOutput(backend);

        await output.PlayAsync(Earcons.Wav(SoundCue.Test), new(0.6, null), CancellationToken.None);
        Assert.Empty(await output.ListAsync(CancellationToken.None));
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task Setup_saves_every_choice_and_reads_it_back_after_a_restart()
    {
        var layout = new MemoryLayout();
        var store = new SoundSettingsStore(layout);
        var output = new RecordingOutput();
        var service = new SoundCueService(store, output, output, new ManualClock(Start));
        var page = new SetupSoundViewModel(store, service, new LocalizedSoundLines(), output, isAvailable: true);

        Assert.False(page.Enabled);
        Assert.False(page.Test());
        Assert.Null(layout.Get(WorkspaceLayoutKeys.SoundSettings));

        page.Enabled = true;
        page.SpeakLootVerdicts = false;
        page.Cues.Single(row => row.AutomationId == "v2-setup-sound-outcome").IsOn = true;
        page.ChooseVolume(40);
        await page.LoadDevicesAsync();
        page.SelectedDevice = page.DeviceChoices.Single(choice => choice.Id == "speakers");

        var restarted = new SoundSettingsStore(layout).Current;
        Assert.Equal(
            SoundSettings.Default with { Enabled = true, SpeakLootVerdicts = false, OutcomeQuestion = true, Volume = 40, DeviceId = "speakers" },
            restarted);
        Assert.True(page.Test());
        await service.Idle;
        Assert.Equal([SoundCue.Test], output.Played);
        Assert.Equal(["Sound test."], output.Spoken);
        Assert.Equal(V2SetupSection.Notifications, SettingsRegistry.FindLayoutKey(WorkspaceLayoutKeys.SoundSettings)?.Home);
    }

    [Fact]
    public void An_unreadable_stored_value_is_the_silent_default()
    {
        Assert.Equal(SoundSettings.Default, SoundSettingsStore.Parse("{not json"));
        Assert.Equal(SoundSettings.Default, SoundSettingsStore.Parse(null));
    }

    private static (ManualClock Clock, SoundSettingsStore Store, RecordingOutput Output, SoundCueService Service) Build(bool enabled = true)
    {
        var clock = new ManualClock(Start);
        var store = new SoundSettingsStore(new MemoryLayout());
        if (enabled)
        {
            store.Update(settings => settings with { Enabled = true });
        }

        var output = new RecordingOutput { Clock = clock };
        var service = new SoundCueService(store, output, output, clock, delay: (wait, _) =>
        {
            clock.Advance(wait);
            return Task.CompletedTask;
        });
        return (clock, store, output, service);
    }

    private static (ManualClock Clock, RecordingCues Cues, SoundCueTriggers Triggers) Triggers(string? self = null, Func<string, string?>? shortName = null)
    {
        var clock = new ManualClock(Start);
        var cues = new RecordingCues();
        var triggers = new SoundCueTriggers(
            cues,
            new LocalizedSoundLines(),
            clock,
            selfName: () => self,
            shortName: shortName is null ? null : (id, _) => Task.FromResult(shortName(id)),
            startTimer: false);
        return (clock, cues, triggers);
    }

    private static Situation InRaid(Guid raid, TimeSpan? left) =>
        new Situation(1, Start, new(SituationPhase.InRaid, Confidence.Certain, SituationSource.GameLog, Start, "test"))
        {
            RaidId = raid,
            Clock = left is { } remaining
                ? new SituationClock(SituationClockBasis.Counted, remaining, Start, Start, "counted")
                : new SituationClock(SituationClockBasis.Elapsed, null, null, Start, "elapsed"),
        };

    private static GroupSnapshot Group(params GroupPingView[] pings) =>
        new(true, [], string.Empty, Start) { Pings = pings };

    private static GroupPingView Ping(long id, string by) => new(id, by, "customs", 0, 0, 0, null, Start);

    private static ScanOutcome Scan(string id, string name, RecommendationAction action, long value, double confidence = 0.95)
    {
        var candidate = new RecognitionCandidate(id, name, new Confidence(confidence), "test");
        return new ScanOutcome(
            Guid.NewGuid(),
            ScanCompletionStatus.Complete,
            ScanContext.SingleItem,
            Start,
            new RecognitionResult(ScanContext.SingleItem, [candidate], Start),
            null,
            null,
            null,
            new RecommendationResult(action, "S", Confidence.Certain, [], "test", value, value, Start, SaleChannel.Flea),
            [])
        {
            EconomicValue = value,
        };
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public void Advance(TimeSpan by)
        {
            lock (_gate)
            {
                _now += by;
            }
        }
    }

    private sealed class RecordingOutput : ICueOutput, ISpeechOutput, IAudioDeviceCatalog
    {
        private int _concurrent;

        public ManualClock? Clock { get; init; }

        public TaskCompletionSource? Hold { get; set; }

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<SoundCue> Played { get; } = [];

        public List<string> Spoken { get; } = [];

        public List<DateTimeOffset> StartTimes { get; } = [];

        public int MaximumConcurrent { get; private set; }

        public bool IsAvailable => true;

        public Task PlayAsync(ReadOnlyMemory<byte> wav, AudioOutputOptions options, CancellationToken cancellationToken)
        {
            var cue = Enum.GetValues<SoundCue>().First(candidate => Earcons.Wav(candidate).AsSpan().SequenceEqual(wav.Span));
            return RunAsync(() => Played.Add(cue));
        }

        public Task SpeakAsync(string text, AudioOutputOptions options, CancellationToken cancellationToken) =>
            RunAsync(() => Spoken.Add(text));

        public Task<IReadOnlyList<AudioOutputDevice>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AudioOutputDevice>>([new("speakers", "Speakers"), new("headset", "Headset")]);

        private async Task RunAsync(Action record)
        {
            MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref _concurrent));
            record();
            StartTimes.Add(Clock?.GetUtcNow() ?? DateTimeOffset.MinValue);
            FirstStarted.TrySetResult();
            if (Hold is { } hold)
            {
                await hold.Task;
            }

            Interlocked.Decrement(ref _concurrent);
        }
    }

    private sealed class RecordingCues : ISoundCues
    {
        public List<(SoundCue Cue, string? Spoken)> Spoken { get; } = [];

        public List<SoundCue> Cues => [.. Spoken.Select(entry => entry.Cue)];

        public bool Play(SoundCue cue, string? spoken = null)
        {
            Spoken.Add((cue, spoken));
            return true;
        }
    }

    private sealed class FakeBackend : IWindowsAudioBackend
    {
        public bool Available { get; init; } = true;

        public string? FailingDevice { get; init; }

        public List<(string Kind, string? Device, double Volume)> Calls { get; } = [];

        public bool IsAvailable => Available;

        public Task<IReadOnlyList<AudioOutputDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AudioOutputDevice>>([new("speakers", "Speakers")]);

        public Task PlayWavAsync(byte[] wav, double volume, string? deviceId, CancellationToken cancellationToken) =>
            Record("wav", deviceId, volume);

        public Task SpeakAsync(string text, double volume, string? deviceId, CancellationToken cancellationToken) =>
            Record("speak", deviceId, volume);

        private Task Record(string kind, string? device, double volume)
        {
            Calls.Add((kind, device, volume));
            return device is not null && device == FailingDevice
                ? Task.FromException(new InvalidOperationException("gone"))
                : Task.CompletedTask;
        }
    }

    private sealed class MemoryLayout : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
