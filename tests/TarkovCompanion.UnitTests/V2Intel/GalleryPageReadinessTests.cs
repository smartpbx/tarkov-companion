using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Core.Domain.Ammo;
using static TarkovCompanion.UnitTests.V2Intel.IntelWorkspaceFakes;

namespace TarkovCompanion.UnitTests.V2Intel;

/// <summary>
/// [#279] The gallery's "page" scene answers ready only once the page has read its data: run
/// 36032134540 photographed Ammo three times while it still said "Reading the ammunition table…".
/// </summary>
public sealed class GalleryPageReadinessTests
{
    [Fact]
    public async Task Ammo_is_not_loaded_while_its_table_is_being_read()
    {
        var catalog = new GatedCatalog();
        var page = new AmmoPageViewModel(catalog, new FakeItemRepository());
        var workspace = new AmmoWorkspaceViewModel(page);
        Assert.False(workspace.HasLoaded);

        var load = page.LoadAsync();
        Assert.False(workspace.HasLoaded);
        Assert.Equal((false, "the ammunition table"), GalleryPageReadiness.Of(workspace, false, false, false, false));

        catalog.Release();
        await load;

        Assert.True(workspace.HasLoaded);
        Assert.True(GalleryPageReadiness.Of(workspace, false, false, false, false).Loaded);
    }

    [Fact]
    public async Task An_empty_ammo_table_still_counts_as_loaded()
    {
        var page = new AmmoPageViewModel(new FakeFactCatalog(), new FakeItemRepository());

        await page.LoadAsync();

        Assert.True(page.HasLoaded);
    }

    [Fact]
    public void An_item_waits_for_its_intel_and_setup_for_its_data_state()
    {
        Assert.Equal((false, "the item's intel"), GalleryPageReadiness.Of(null, true, false, false, false));
        Assert.True(GalleryPageReadiness.Of(null, true, true, false, false).Loaded);
        Assert.Equal((false, "the data state"), GalleryPageReadiness.Of(null, false, false, true, true));
        Assert.True(GalleryPageReadiness.Of(null, false, false, true, false).Loaded);
        Assert.True(GalleryPageReadiness.Of(new object(), false, false, false, false).Loaded);
    }

    [Fact]
    public void The_page_scene_parses() =>
        Assert.Equal(GallerySceneKind.Page, GallerySceneKinds.Parse("page"));

    private sealed class GatedCatalog : IItemFactCatalog
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.SetResult();

        public async Task<IReadOnlyList<AmmoStats>> GetAmmoAsync(CancellationToken cancellationToken)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            return [];
        }

        public Task<IReadOnlyList<AmmoPackContents>> GetAmmoPacksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AmmoPackContents>>([]);

        public Task<IReadOnlyList<LoadoutItemFacts>> GetLoadoutFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LoadoutItemFacts>>([]);

        public Task<IReadOnlyList<KeyFacts>> GetKeyFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KeyFacts>>([]);

        public void Invalidate()
        {
        }
    }
}
