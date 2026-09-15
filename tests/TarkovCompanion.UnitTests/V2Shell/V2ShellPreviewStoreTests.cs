using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// Provisional navigation state is namespaced by variant, and resetting it resets nothing else.
/// </summary>
/// <remarks>
/// The rollback rule in #267: a reset must not change profile, raid, capture, marks, plans, or the
/// V1 layout. Those live in files beside the preview directory, so the tests put real files there
/// and compare their bytes afterwards.
/// </remarks>
public sealed class V2ShellPreviewStoreTests : IDisposable
{
    private const string V1Layout = "{\n  \"width\": 1720,\n  \"height\": 980,\n  \"scale\": 1.15\n}";
    private const string Profile = "{\"name\":\"Moth\",\"level\":42}";
    private readonly string _config = V2ShellTestData.TemporaryDirectory();

    public void Dispose() => Directory.Delete(_config, recursive: true);

    [Fact]
    public void Each_variant_keeps_its_own_file_under_the_preview_directory()
    {
        var a = new V2ShellPreviewStore(_config, V2ShellMode.VariantA);
        var b = new V2ShellPreviewStore(_config, V2ShellMode.VariantB);

        Assert.NotEqual(a.FilePath, b.FilePath);
        Assert.Equal(Path.Combine(_config, V2ShellPreviewStore.DirectoryName, "v2-a.json"), a.FilePath);
        Assert.Equal(Path.Combine(_config, V2ShellPreviewStore.DirectoryName, "v2-b.json"), b.FilePath);
    }

    [Fact]
    public void The_legacy_shell_has_no_preview_state_to_keep()
    {
        Assert.Throws<ArgumentException>(() => new V2ShellPreviewStore(_config, V2ShellMode.Legacy));
    }

    [Fact]
    public void A_variant_never_opened_here_is_a_first_launch_and_reading_creates_nothing()
    {
        var load = new V2ShellPreviewStore(_config, V2ShellMode.VariantB).Load();

        Assert.Equal(V2PreviewLoadOutcome.FirstLaunch, load.Outcome);
        Assert.Equal("v2-b", load.State.Variant);
        Assert.Null(load.State.Address);
        Assert.False(Directory.Exists(Path.Combine(_config, V2ShellPreviewStore.DirectoryName)));
    }

    [Fact]
    public async Task What_was_saved_is_what_the_next_launch_restores()
    {
        var store = new V2ShellPreviewStore(_config, V2ShellMode.VariantA);
        var saved = V2ShellPreviewState.For(V2ShellMode.VariantA) with
        {
            Address = "#/plan/hideout",
            SelectedEntity = "station-lavatory",
            FocusTarget = "v2-shell-page-heading",
            Recents = ["#/plan/hideout", "#/raid"],
            Pins = ["#/raid/loot"],
            Window = new(1280, 800, 40, 60, false),
            CaptureShortcutEnabled = false,
        };

        await store.SaveAsync(saved, CancellationToken.None);
        var load = new V2ShellPreviewStore(_config, V2ShellMode.VariantA).Load();

        Assert.Equal(V2PreviewLoadOutcome.Restored, load.Outcome);
        Assert.Equal(saved.Address, load.State.Address);
        Assert.Equal(saved.SelectedEntity, load.State.SelectedEntity);
        Assert.Equal(saved.FocusTarget, load.State.FocusTarget);
        Assert.Equal(saved.Recents, load.State.Recents);
        Assert.Equal(saved.Pins, load.State.Pins);
        Assert.Equal(saved.Window, load.State.Window);
        Assert.False(load.State.CaptureShortcutEnabled);
        Assert.False(File.Exists(new V2ShellPreviewStore(_config, V2ShellMode.VariantB).FilePath));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("{\"schema\": 99, \"variant\": \"v2-b\"}")]
    [InlineData("{\"schema\": 1, \"variant\": \"v2-a\"}")]
    [InlineData("{\"schema\": 1, \"variant\": \"v2-b\", \"recents\": null}")]
    [InlineData("{\"schema\": 1, \"variant\": \"v2-b\", \"window\": {\"width\": 0, \"height\": 0}}")]
    public async Task Corrupt_preview_state_resets_the_preview_and_nothing_else(string corrupt)
    {
        var shellJson = Path.Combine(_config, "shell.json");
        var profileJson = Path.Combine(_config, "profile.json");
        await File.WriteAllTextAsync(shellJson, V1Layout);
        await File.WriteAllTextAsync(profileJson, Profile);
        var a = new V2ShellPreviewStore(_config, V2ShellMode.VariantA);
        await a.SaveAsync(V2ShellPreviewState.For(V2ShellMode.VariantA) with { Address = "#/team" }, CancellationToken.None);
        var aBytes = await File.ReadAllBytesAsync(a.FilePath);
        var b = new V2ShellPreviewStore(_config, V2ShellMode.VariantB, new FixedClock(V2ShellTestData.Now));
        await File.WriteAllTextAsync(b.FilePath, corrupt);

        var load = b.Load();

        Assert.Equal(V2PreviewLoadOutcome.ResetAfterCorruption, load.Outcome);
        Assert.Null(load.State.Address);
        Assert.NotNull(load.SetAsidePath);
        Assert.True(File.Exists(load.SetAsidePath));
        Assert.False(File.Exists(b.FilePath));
        Assert.Equal(V1Layout, await File.ReadAllTextAsync(shellJson));
        Assert.Equal(Profile, await File.ReadAllTextAsync(profileJson));
        Assert.Equal(aBytes, await File.ReadAllBytesAsync(a.FilePath));
        Assert.Equal(V2PreviewLoadOutcome.Restored, a.Load().Outcome);
    }

    [Fact]
    public async Task An_oversized_file_is_not_read()
    {
        var store = new V2ShellPreviewStore(_config, V2ShellMode.VariantA);
        Directory.CreateDirectory(store.DirectoryPath);
        await File.WriteAllTextAsync(store.FilePath, new string(' ', V2ShellPreviewStore.MaximumBytes + 1));

        Assert.Equal(V2PreviewLoadOutcome.ResetAfterCorruption, store.Load().Outcome);
    }

    [Fact]
    public async Task Reset_forgets_one_variant_and_leaves_every_other_file_alone()
    {
        var shellJson = Path.Combine(_config, "shell.json");
        await File.WriteAllTextAsync(shellJson, V1Layout);
        var a = new V2ShellPreviewStore(_config, V2ShellMode.VariantA);
        var b = new V2ShellPreviewStore(_config, V2ShellMode.VariantB);
        await a.SaveAsync(V2ShellPreviewState.For(V2ShellMode.VariantA) with { Address = "#/raid" }, CancellationToken.None);
        await b.SaveAsync(V2ShellPreviewState.For(V2ShellMode.VariantB) with { Address = "#/home" }, CancellationToken.None);

        b.Reset();

        Assert.False(File.Exists(b.FilePath));
        Assert.Equal("#/raid", a.Load().State.Address);
        Assert.Equal(V1Layout, await File.ReadAllTextAsync(shellJson));
    }

    [Fact]
    public async Task Saving_writes_only_inside_the_preview_directory_and_bounds_the_lists()
    {
        var store = new V2ShellPreviewStore(_config, V2ShellMode.VariantB);
        var many = Enumerable.Range(0, 40).Select(index => $"#/team/group/intel/item-{index}").ToArray();

        await store.SaveAsync(
            V2ShellPreviewState.For(V2ShellMode.VariantA) with { Recents = many, Pins = many },
            CancellationToken.None);

        var written = Directory.GetFiles(_config, "*", SearchOption.AllDirectories);
        var load = store.Load();
        Assert.Equal([store.FilePath], written);
        Assert.Equal("v2-b", load.State.Variant);
        Assert.Equal(V2ShellPreviewState.MaxRecents, load.State.Recents.Count);
        Assert.Equal(V2ShellPreviewState.MaxPins, load.State.Pins.Count);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
