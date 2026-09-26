using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.UnitTests.Team;

/// <summary>
/// [#712 T7] "My ready check" on Team › Group: on by default (a file from before it reads as on),
/// saved when flipped like the other squad switches, and kept by the switches that do not own it.
/// </summary>
public sealed class ReadyCheckSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tc-ready-check-" + Guid.NewGuid().ToString("N"));

    private string GroupFile => Path.Combine(_root, "group.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task A_settings_file_from_before_the_switch_reads_as_on()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(GroupFile, """{"enabled":true,"serverUri":"https://relay.example.test/","displayName":"Clay","key":"a-key-long-enough","shareLoadout":false,"shareQuests":true}""");

        var stored = await new JsonFileGroupSettingsStore(GroupFile).GetAsync(CancellationToken.None);

        Assert.True(stored.SharesReadyCheck);
        Assert.True(GroupSharingSettings.Off.SharesReadyCheck);
    }

    [Fact]
    public async Task Turning_it_off_on_Team_is_saved_survives_a_restart_and_other_switches_keep_it()
    {
        Directory.CreateDirectory(_root);
        var store = new JsonFileGroupSettingsStore(GroupFile);
        await store.SaveAsync(new(true, "https://relay.example.test/", "Clay", "a-key-long-enough", false, true), CancellationToken.None);
        var team = Team(store);
        await team.LoadAsync();
        Assert.True(team.SharesReadyCheck);

        team.SharesReadyCheck = false;
        await team.PendingSave;
        Assert.False((await store.GetAsync(CancellationToken.None)).SharesReadyCheck);

        // Another switch, and Save, write the ready check as it stands rather than back to on.
        team.SharesQuests = false;
        await team.PendingSave;
        var saves = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref saves);
        team.SaveCommand.Execute(null);
        for (var attempt = 0; attempt < 100 && Volatile.Read(ref saves) == 0; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, Volatile.Read(ref saves));

        var restarted = Team(new JsonFileGroupSettingsStore(GroupFile));
        await restarted.LoadAsync();
        Assert.False(restarted.SharesReadyCheck);
        Assert.False(restarted.SharesQuests);

        restarted.SharesReadyCheck = true;
        await restarted.PendingSave;
        Assert.True((await store.GetAsync(CancellationToken.None)).SharesReadyCheck);
    }

    private static TeamWorkspaceViewModel Team(IGroupSettingsStore store) =>
        new(Session(), store, dispatch: static action => action());

    private static GroupSessionService Session() => new(
        new JsonFileGroupSettingsStore(Path.Combine(Path.GetTempPath(), "tc-ready-check-none-" + Guid.NewGuid().ToString("N") + ".json")),
        new RuntimeStateStore(new(false, true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5))),
        new HttpClient(new OkHandler()) { Timeout = Timeout.InfiniteTimeSpan },
        NullLogger<GroupSessionService>.Instance);

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
