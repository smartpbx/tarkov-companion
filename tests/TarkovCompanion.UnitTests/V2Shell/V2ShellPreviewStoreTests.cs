using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Shell;

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

    /// <summary>
    /// V2 rough package 30 (acceptance sweep): every address the Windows sweep seeds is one the
    /// store will restore, written the way the sweep writes it.
    /// </summary>
    /// <remarks>
    /// The sweep reaches each V2 route by writing this file and launching. It first wrote an
    /// empty focus target, which the store rejects — and a rejected state is set aside whole, so
    /// the address went with it and every route landed on the variant's landing page instead.
    /// Nothing on Linux could see that, and on Windows it was hidden behind a launch failure.
    ///
    /// This reads the addresses out of the script rather than restating them, so a route added to
    /// the sweep is covered the day it is added.
    /// </remarks>
    [Fact]
    public void Every_address_the_Windows_sweep_seeds_survives_a_reload()
    {
        var gallery = File.ReadAllText(V2ShellTestData.RepositoryPath("scripts", "windows-page-gallery.ps1"));
        var addresses = System.Text.RegularExpressions.Regex.Matches(gallery, "address = \"(#/[^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(addresses);

        foreach (var address in addresses)
        {
            var directory = Path.Combine(_config, "seed", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, "v2-shell-preview"));

            // Byte for byte what Set-V2PreviewState writes: schema, variant, address, no focus.
            File.WriteAllText(
                Path.Combine(directory, "v2-shell-preview", "v2-a.json"),
                $$"""
                {
                  "schema": 1,
                  "variant": "v2-a",
                  "address": "{{address}}",
                  "selectedEntity": null,
                  "focusTarget": null,
                  "recents": [],
                  "pins": [],
                  "window": null,
                  "captureShortcutEnabled": true
                }
                """);

            var load = new V2ShellPreviewStore(directory, V2ShellMode.VariantA).Load();

            Assert.Equal(V2PreviewLoadOutcome.Restored, load.Outcome);
            Assert.Equal(address, load.State.Address);
        }

        // And the focus target the script writes beside them. An empty one is rejected, and a
        // rejected state is set aside whole, so this is the difference between a seeded address
        // taking effect and every route quietly landing on the variant's landing page.
        Assert.DoesNotContain("focusTarget = \"\"", gallery, StringComparison.Ordinal);
        foreach (System.Text.RegularExpressions.Match focus in System.Text.RegularExpressions.Regex.Matches(
            gallery, "focusTarget = \"([^\"]*)\""))
        {
            Assert.True(
                focus.Groups[1].Value.Length > 0,
                "a seeded focus target is either absent or a real identifier; \"\" makes the store " +
                "discard the whole state, address included.");
        }
    }

    [Fact]
    public void An_empty_focus_target_is_not_a_missing_one_and_takes_the_address_with_it()
    {
        var directory = Path.Combine(_config, "empty-focus");
        Directory.CreateDirectory(Path.Combine(directory, "v2-shell-preview"));
        File.WriteAllText(
            Path.Combine(directory, "v2-shell-preview", "v2-a.json"),
            """
            {
              "schema": 1,
              "variant": "v2-a",
              "address": "#/raid",
              "selectedEntity": null,
              "focusTarget": "",
              "recents": [],
              "pins": [],
              "window": null,
              "captureShortcutEnabled": true
            }
            """);

        var load = new V2ShellPreviewStore(directory, V2ShellMode.VariantA).Load();

        // Not a quirk to work around — the reason the sweep writes null. Recorded so the next
        // person to seed one of these files knows the address goes with the focus target.
        Assert.Equal(V2PreviewLoadOutcome.ResetAfterCorruption, load.Outcome);
        Assert.NotEqual("#/raid", load.State.Address);
    }

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
    [InlineData("{\"schema\": 1, \"variant\": \"v2-b\", \"address\": \"#/HOME\"}")]
    [InlineData("{\"schema\": 1, \"variant\": \"v2-b\", \"selectedEntity\": \"..\"}")]
    [InlineData("{\"schema\": 1, \"variant\": \"v2-b\", \"focusTarget\": \"../../other-window\"}")]
    [InlineData("{\"schema\": 1, \"variant\": \"v2-b\", \"window\": {\"width\": 800, \"height\": 600, \"left\": 2, \"top\": null}}")]
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

        await b.ResetAsync(CancellationToken.None);

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

    [Fact]
    public async Task Saving_refuses_unbounded_or_noncanonical_state_before_writing()
    {
        var store = new V2ShellPreviewStore(_config, V2ShellMode.VariantB);
        var invalid = new[]
        {
            V2ShellPreviewState.For(V2ShellMode.VariantB) with { Address = "#/HOME" },
            V2ShellPreviewState.For(V2ShellMode.VariantB) with { SelectedEntity = new string('x', V2ShellPreviewState.MaxEntityLength + 1) },
            V2ShellPreviewState.For(V2ShellMode.VariantB) with { FocusTarget = "../../outside" },
            V2ShellPreviewState.For(V2ShellMode.VariantB) with { Window = new(800, 600, 10, null, false) },
        };

        foreach (var state in invalid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(state, CancellationToken.None));
        }

        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task A_save_that_started_before_reset_cannot_recreate_the_file_after_reset()
    {
        var store = new V2ShellPreviewStore(_config, V2ShellMode.VariantA);
        var save = store.SaveAsync(
            V2ShellPreviewState.For(V2ShellMode.VariantA) with { Address = "#/raid" },
            CancellationToken.None);
        var reset = store.ResetAsync(CancellationToken.None);

        await Task.WhenAll(save, reset);

        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task Save_and_reset_surface_storage_failures_instead_of_reporting_success()
    {
        var store = new V2ShellPreviewStore(_config, V2ShellMode.VariantA);
        Directory.CreateDirectory(store.DirectoryPath);
        Directory.CreateDirectory(store.FilePath);

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(
            V2ShellPreviewState.For(V2ShellMode.VariantA) with { Address = "#/raid" },
            CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => store.ResetAsync(CancellationToken.None));

        Assert.True(Directory.Exists(store.FilePath));
    }

    [Fact]
    public void Window_placement_preserves_reachable_overlap_and_clamps_a_stranded_monitor()
    {
        ScreenBounds[] screens = [new(0, 0, 1920, 1080)];
        var reachable = new V2ShellWindowPlacement(900, 700, -40, 30, false);
        var stranded = new V2ShellWindowPlacement(2500, 1400, 5000, 4000, true);

        Assert.Equal(reachable, reachable.ClampTo(screens));
        var clamped = stranded.ClampTo(screens);
        Assert.Equal(1920d, clamped.Width);
        Assert.Equal(1080d, clamped.Height);
        Assert.Equal(0d, clamped.Left!.Value);
        Assert.Equal(0d, clamped.Top!.Value);
        Assert.True(clamped.IsMaximized);
    }

    [Fact]
    public void A_window_saved_taller_than_the_work_area_is_brought_back_above_the_taskbar()
    {
        // A 1080p screen with a 48-pixel taskbar: the work area ends at 1032.
        ScreenBounds[] screens = [new(0, 0, 1920, 1032)];

        var fullScreenSize = new V2ShellWindowPlacement(1920, 1080, 0, 0, false).ClampTo(screens);
        Assert.Equal(1032d, fullScreenSize.Height);
        Assert.Equal(0d, fullScreenSize.Top!.Value);
        Assert.Equal(1920d, fullScreenSize.Width);

        // Short enough to fit, but left hanging under the taskbar: moved up, not resized.
        var lowDown = new V2ShellWindowPlacement(1500, 900, 200, 300, false).ClampTo(screens);
        Assert.Equal(900d, lowDown.Height);
        Assert.Equal(132d, lowDown.Top!.Value);
        Assert.Equal(200d, lowDown.Left!.Value);

        // Hanging off the side is deliberate and is left alone.
        var offTheSide = new V2ShellWindowPlacement(900, 700, -40, 30, false);
        Assert.Equal(offTheSide, offTheSide.ClampTo(screens));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
