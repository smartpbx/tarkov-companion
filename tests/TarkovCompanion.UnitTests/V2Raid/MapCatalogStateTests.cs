using System.Net;
using System.Text;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Network;
using TarkovCompanion.Core.Network;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [#292] The Raid page printed "Map catalog is unavailable: Local only is on, so nothing was
/// sent." These pin that a failed catalog load becomes a state, and the state plain words.
/// </summary>
public sealed class MapCatalogStateTests
{
    private static readonly Uri CatalogUri = new("https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json");

    [Fact]
    public async Task LocalOnlyWithNoCacheIsLocalOnlyNotAFailure()
    {
        var result = await LoadAsync(_ => throw new NetworkBlockedException(null, NetworkVerdict.LocalOnly));

        Assert.Null(result.Catalog);
        Assert.Equal(MapCatalogFailure.LocalOnly, result.Failure);
        Assert.Equal(MapCatalogState.LocalOnly, MapCatalogStates.From(result));
    }

    [Fact]
    public async Task ANetworkFailureWithNoCacheIsFailed()
    {
        var result = await LoadAsync(_ => throw new HttpRequestException("Connection refused (127.0.0.1:9)"));

        Assert.Equal(MapCatalogFailure.Failed, result.Failure);
        Assert.Equal(MapCatalogState.Failed, MapCatalogStates.From(result));
    }

    [Fact]
    public async Task ASwitchedOffServiceIsAFailureNotLocalOnly()
    {
        var result = await LoadAsync(_ => throw new NetworkBlockedException(NetworkService.SquadSharing, NetworkVerdict.SwitchedOff));

        Assert.Equal(MapCatalogState.Failed, MapCatalogStates.From(result));
    }

    [Fact]
    public async Task ADownloadedCatalogIsLoaded()
    {
        var json = File.ReadAllText(Fixture("fixtures/maps/tarkov-dev-catalog.synthetic.json"));
        var result = await LoadAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

        Assert.NotNull(result.Catalog);
        Assert.Equal(MapCatalogFailure.None, result.Failure);
        Assert.Equal(MapCatalogState.Loaded, MapCatalogStates.From(result));
    }

    [Fact]
    public async Task LocalOnlyOpensDataAndPrivacyInsteadOfRetrying()
    {
        var retries = 0;
        var opened = 0;
        var notice = new MapCatalogNoticeViewModel(() => { retries++; return Task.CompletedTask; }, () => opened++);

        notice.Apply(MapCatalogState.LocalOnly);
        await notice.ActAsync();

        Assert.True(notice.IsVisible);
        Assert.Equal(RaidText.MapCatalogLocalOnlyTitle, notice.Title);
        Assert.Equal(RaidText.MapCatalogOpenDataPrivacy, notice.ActionLabel);
        Assert.Equal(1, opened);
        Assert.Equal(0, retries);
    }

    [Fact]
    public async Task AFailureOffersRetryAndNeverTheExceptionText()
    {
        var retries = 0;
        var opened = 0;
        var notice = new MapCatalogNoticeViewModel(() => { retries++; return Task.CompletedTask; }, () => opened++);

        notice.Apply(MapCatalogState.Failed);
        await notice.ActAsync();

        Assert.True(notice.IsVisible);
        Assert.Equal(RaidText.MapCatalogFailedTitle, notice.Title);
        Assert.Equal(ShellText.FaultRetry, notice.ActionLabel);
        Assert.DoesNotContain("unavailable", notice.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, retries);
        Assert.Equal(0, opened);
    }

    [Theory]
    [InlineData(MapCatalogState.Loading)]
    [InlineData(MapCatalogState.Loaded)]
    public void NoNoticeWhileLoadingOrLoaded(MapCatalogState state)
    {
        var notice = new MapCatalogNoticeViewModel(() => Task.CompletedTask, () => { });

        notice.Apply(MapCatalogState.Failed);
        notice.Apply(state);

        Assert.False(notice.IsVisible);
    }

    private static async Task<MapCatalogLoadResult> LoadAsync(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var directory = Path.Combine(Path.GetTempPath(), "map-catalog-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var client = new TarkovDevMapCatalogClient(
                new HttpClient(new Handler(respond)),
                new(CatalogUri, directory, TimeSpan.FromHours(1), TimeSpan.FromSeconds(5), 4 * 1024 * 1024));
            return await client.GetAsync(CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static string Fixture(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate test fixture {relativePath}.");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
