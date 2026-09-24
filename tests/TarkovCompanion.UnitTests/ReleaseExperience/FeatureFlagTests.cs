using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services.FeatureFlags;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.ReleaseExperience;
using TarkovCompanion.Core.Features;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.UnitTests.ReleaseExperience;

public sealed class FeatureFlagTests
{
    private static readonly FeatureFlagDefinition Restarting = new("restarting", "Restarting", "d", 1, OnInDev: true, OnInRough: true, OnInStable: false, NeedsRestart: true);
    private static readonly FeatureFlagDefinition Live = new("live", "Live", "d", 2, OnInDev: true, OnInRough: false, OnInStable: false, NeedsRestart: false);
    private static readonly FeatureFlagDefinition[] Registry = [Restarting, Live];

    [Theory]
    [InlineData(ReleaseRing.Dev, true, true)]
    [InlineData(ReleaseRing.Rough, true, false)]
    [InlineData(ReleaseRing.Stable, false, false)]
    public void With_no_file_every_flag_follows_its_ring(ReleaseRing ring, bool restarting, bool live)
    {
        var flags = new FeatureFlagService(ring, new MemoryStore(), registry: Registry);

        Assert.Equal(restarting, flags.IsOn(Restarting));
        Assert.Equal(live, flags.IsOn(Live));
        Assert.All(flags.States, state => Assert.Equal(FeatureFlagSource.RingDefault, state.Source));
    }

    [Fact]
    public void An_override_in_the_file_wins_over_the_ring_default()
    {
        var store = new MemoryStore { ["restarting"] = false, ["live"] = true };

        var flags = new FeatureFlagService(ReleaseRing.Rough, store, registry: Registry);

        Assert.False(flags.IsOn(Restarting));
        Assert.True(flags.IsOn(Live));
        Assert.All(flags.States, state => Assert.Equal(FeatureFlagSource.Override, state.Source));
    }

    [Fact]
    public void Unknown_keys_are_ignored_logged_once_and_kept_when_the_file_is_written()
    {
        var store = new MemoryStore { ["from-a-newer-build"] = true, ["not-a-bool"] = null, ["live"] = true };
        var logger = new RecordingLogger();

        var flags = new FeatureFlagService(ReleaseRing.Rough, store, logger, Registry);
        flags.Set(Restarting, false);
        flags.Reset(Restarting);

        Assert.True(flags.IsOn(Live));
        Assert.Single(logger.Warnings, line => line.Contains("from-a-newer-build", StringComparison.Ordinal));
        Assert.Single(logger.Warnings, line => line.Contains("not-a-bool", StringComparison.Ordinal));
        Assert.Equal(2, logger.Warnings.Count);
        Assert.True(store.LastSaved!["from-a-newer-build"]);
        Assert.False(store.LastSaved.ContainsKey("not-a-bool"));
    }

    [Fact]
    public void A_restart_flag_keeps_its_startup_value_until_the_next_start()
    {
        var store = new MemoryStore();
        var flags = new FeatureFlagService(ReleaseRing.Rough, store, registry: Registry);

        flags.Set(Restarting, false);

        Assert.True(flags.IsOn(Restarting));
        var state = flags.States.Single(entry => entry.Flag == Restarting);
        Assert.False(state.IsOn);
        Assert.True(state.WaitsForRestart);
        Assert.False(new FeatureFlagService(ReleaseRing.Rough, store, registry: Registry).IsOn(Restarting));
    }

    [Fact]
    public void A_live_flag_changes_at_once()
    {
        var flags = new FeatureFlagService(ReleaseRing.Rough, new MemoryStore(), registry: Registry);
        var changed = 0;
        flags.Changed += (_, _) => changed++;

        flags.Set(Live, true);

        Assert.True(flags.IsOn(Live));
        Assert.False(flags.States.Single(entry => entry.Flag == Live).WaitsForRestart);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Reset_removes_the_override_and_choosing_the_default_is_the_same_as_reset()
    {
        var store = new MemoryStore { ["live"] = true };
        var flags = new FeatureFlagService(ReleaseRing.Rough, store, registry: Registry);

        flags.Reset(Live);

        Assert.False(flags.IsOn(Live));
        Assert.Empty(store.LastSaved!);
        flags.Set(Live, true);
        flags.Set(Live, false);
        Assert.Empty(store.LastSaved!);
        Assert.Equal(FeatureFlagSource.RingDefault, flags.States.Single(entry => entry.Flag == Live).Source);
    }

    [Fact]
    public void An_unreadable_file_leaves_the_ring_defaults()
    {
        var flags = new FeatureFlagService(ReleaseRing.Dev, new ThrowingStore(), registry: Registry);

        Assert.True(flags.IsOn(Live));
    }

    [Theory]
    [InlineData("stable", false, false, ReleaseRing.Stable)]
    [InlineData(" Rough ", false, false, ReleaseRing.Rough)]
    [InlineData("nightly", false, true, ReleaseRing.Rough)]
    [InlineData("7", false, false, ReleaseRing.Dev)]
    [InlineData(null, true, true, ReleaseRing.Dev)]
    [InlineData(null, false, true, ReleaseRing.Rough)]
    [InlineData(null, false, false, ReleaseRing.Dev)]
    public void The_ring_comes_from_the_variable_then_the_feed_then_the_install(
        string? configured, bool customFeed, bool installed, ReleaseRing expected)
    {
        Assert.Equal(expected, ReleaseRingDetector.Detect(configured, customFeed, installed));
    }

    [Fact]
    public void The_json_file_round_trips_and_reads_non_booleans_as_null()
    {
        var folder = Directory.CreateTempSubdirectory("feature-flags-");
        try
        {
            var path = Path.Combine(folder.FullName, "Config", JsonFileFeatureFlagOverrideStore.FileName);
            var store = new JsonFileFeatureFlagOverrideStore(path);
            Assert.Empty(store.Read());

            store.Save(new Dictionary<string, bool> { ["draw-mode"] = false });
            Assert.False(store.Read()["draw-mode"]);

            File.WriteAllText(path, """{ "draw-mode": "no", "extra": 1, "tablet-review-cards": true }""");
            var read = store.Read();
            Assert.Null(read["draw-mode"]);
            Assert.True(read["tablet-review-cards"]);

            File.WriteAllText(path, "[]");
            Assert.Throws<InvalidDataException>(() => store.Read());
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Setup_shows_the_source_offers_reset_and_says_a_restart_applies_it()
    {
        var flags = new FeatureFlagService(ReleaseRing.Rough, new MemoryStore(), registry: Registry);
        var setup = new SetupFeatureFlagsViewModel(flags);
        var row = setup.Rows.Single(entry => entry.Key == "restarting");
        Assert.True(row.IsOn);
        Assert.False(row.IsOverridden);
        Assert.Equal("Rough default", row.SourceLabel);

        row.ToggleCommand.Execute(null);

        Assert.False(row.IsOn);
        Assert.True(row.IsOverridden);
        Assert.True(row.WaitsForRestart);
        Assert.Equal("Your choice · rough default is on", row.SourceLabel);

        row.ResetCommand.Execute(null);

        Assert.True(row.IsOn);
        Assert.False(row.IsOverridden);
        Assert.False(row.WaitsForRestart);
    }

    private sealed class MemoryStore : Dictionary<string, bool?>, IFeatureFlagOverrideStore
    {
        public IReadOnlyDictionary<string, bool>? LastSaved { get; private set; }

        public IReadOnlyDictionary<string, bool?> Read() => new Dictionary<string, bool?>(this);

        public void Save(IReadOnlyDictionary<string, bool> overrides)
        {
            LastSaved = new Dictionary<string, bool>(overrides);
            Clear();
            foreach (var (key, value) in overrides)
            {
                this[key] = value;
            }
        }
    }

    private sealed class ThrowingStore : IFeatureFlagOverrideStore
    {
        public IReadOnlyDictionary<string, bool?> Read() => throw new InvalidDataException("broken");

        public void Save(IReadOnlyDictionary<string, bool> overrides) => throw new IOException("read-only");
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
