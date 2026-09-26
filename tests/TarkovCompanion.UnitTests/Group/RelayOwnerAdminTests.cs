using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.StateSync;
using TarkovCompanion.GroupServer.Storage;
using TarkovCompanion.GroupServer.Tenancy;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;
using TarkovCompanion.UnitTests.RelayLink;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>
/// [#920] The relay owner's room controls, against a relay in this process serving the real
/// routes: who may use them, and what a squad sees after each one.
/// </summary>
/// <remarks>
/// In the admin-key collection because one test sets the process-wide admin key.
/// </remarks>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class RelayOwnerAdminTests
{
    private const string Key = "the-friday-squad-group-key";
    private static readonly string Room = GroupKey.RoomFor(Key);

    [Fact]
    public async Task Every_admin_route_refuses_a_group_member_and_a_squadmates_desktop()
    {
        await using var relay = await AdminRelay.StartAsync();
        string[] routes =
        [
            "GET admin/owner/rooms",
            $"POST admin/owner/rooms/{Room}/drawings/clear",
            $"POST admin/owner/rooms/{Room}/drawings/clear?member=Alpha",
            $"POST admin/owner/rooms/{Room}/marks/clear",
            $"POST admin/owner/rooms/{Room}/members/remove?member=Alpha",
            $"POST admin/owner/rooms/{Room}/reset",
        ];

        foreach (var route in routes)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await relay.SendAsync(route, Caller.Nobody)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await relay.SendAsync(route, Caller.GroupMember)).StatusCode);
            // A squadmate's desktop owns its own tenant, and that is all it owns.
            Assert.Equal(HttpStatusCode.Unauthorized, (await relay.SendAsync(route, Caller.Squadmate)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await relay.SendAsync(route, Caller.Owner)).StatusCode);
        }

        Assert.False(await relay.IsRelayOwnerAsync(Caller.Squadmate));
        Assert.True(await relay.IsRelayOwnerAsync(Caller.Owner));
        Assert.Equal(HttpStatusCode.Unauthorized, (await relay.SendAsync("GET " + RelayOwnerAdminRoutes.StatusPath.TrimStart('/'), Caller.GroupMember)).StatusCode);
    }

    [Fact]
    public async Task The_operators_admin_key_opens_them_too()
    {
        var previous = Environment.GetEnvironmentVariable(RelayAdmin.Variable);
        Environment.SetEnvironmentVariable(RelayAdmin.Variable, LinkRelay.AdminKey);
        try
        {
            await using var relay = await AdminRelay.StartAsync();
            Assert.Equal(HttpStatusCode.OK, (await relay.SendAsync("GET admin/owner/rooms", Caller.AdminKey)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await relay.SendAsync($"POST admin/owner/rooms/{Room}/marks/clear", Caller.AdminKey)).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RelayAdmin.Variable, previous);
        }
    }

    [Fact]
    public async Task Clearing_drawings_takes_every_members_lines_off_the_squads_maps_and_keeps_them_off()
    {
        await using var relay = await AdminRelay.StartAsync();
        await relay.PublishAsync("Alpha", Line("a-1"), Line("a-2"));
        await relay.PublishAsync("Bravo", Line("b-1"));
        Assert.Equal(3, CountLines(await relay.PublishAsync("Carol")));

        var cleared = await relay.PostOutcomeAsync($"admin/owner/rooms/{Room}/drawings/clear");
        Assert.Equal(3, cleared);
        Assert.Equal(0, CountLines(await relay.PublishAsync("Carol")));

        // Every client, older ones included, keeps sending a line until it expires on its own
        // machine. The relay drops the cleared ids, so the squad does not see them come back.
        await relay.PublishAsync("Alpha", Line("a-1"), Line("a-2"));
        await relay.PublishAsync("Bravo", Line("b-1"));
        Assert.Equal(0, CountLines(await relay.PublishAsync("Carol")));

        // A line drawn afterwards is a new line and shows.
        await relay.PublishAsync("Alpha", Line("a-1"), Line("a-3"));
        var carol = await relay.PublishAsync("Carol");
        Assert.Equal(["a-3"], LineIds(carol));

        // A second clear is the same request again, and there is only the new line to take.
        Assert.Equal(1, await relay.PostOutcomeAsync($"admin/owner/rooms/{Room}/drawings/clear"));
        Assert.Equal(0, await relay.PostOutcomeAsync($"admin/owner/rooms/{Room}/drawings/clear"));
    }

    [Fact]
    public async Task Clearing_one_members_drawings_leaves_everybody_elses()
    {
        await using var relay = await AdminRelay.StartAsync();
        await relay.PublishAsync("Alpha", Line("a-1"));
        await relay.PublishAsync("Bravo", Line("b-1"));

        Assert.Equal(1, await relay.PostOutcomeAsync($"admin/owner/rooms/{Room}/drawings/clear?member=Alpha"));

        Assert.Equal(["b-1"], LineIds(await relay.PublishAsync("Carol")));
    }

    [Fact]
    public async Task Clearing_marks_takes_every_waypoint_and_ping_and_the_room_goes_on_marking()
    {
        await using var relay = await AdminRelay.StartAsync();
        relay.Marks.AddWaypoint(Room, "Alpha", "customs", 1, 0, 2, "Dorms");
        relay.Marks.AddWaypoint(Room, "Bravo", "woods", 1, 0, 2, null);
        relay.Marks.AddPing(Room, "Alpha", "customs", 3, 0, 4, null);

        Assert.Equal(3, await relay.PostOutcomeAsync($"admin/owner/rooms/{Room}/marks/clear"));
        var carol = await relay.PublishAsync("Carol");
        Assert.Empty(carol.GetProperty("waypoints").EnumerateArray());
        Assert.Empty(carol.GetProperty("pings").EnumerateArray());

        Assert.NotNull(relay.Marks.AddWaypoint(Room, "Alpha", "customs", 5, 0, 6, null));
        Assert.Single((await relay.PublishAsync("Carol")).GetProperty("waypoints").EnumerateArray());
    }

    [Fact]
    public async Task A_removed_member_is_refused_until_they_leave_and_rejoin()
    {
        await using var relay = await AdminRelay.StartAsync();
        await relay.PublishAsync("Alpha", Line("a-1"));
        Assert.Single(Members(await relay.PublishAsync("Carol")));

        Assert.Equal(1, await relay.PostOutcomeAsync($"admin/owner/rooms/{Room}/members/remove?member=Alpha"));
        Assert.Empty(Members(await relay.PublishAsync("Carol")));

        // Their next exchange is refused, with a status the wrong-key limiter does not count.
        using var refused = await relay.PublishRawAsync("Alpha");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(RelayRoomStateRoutes.RemovedByOwner, await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(Members(await relay.PublishAsync("Carol")));

        // Removing again is the same removal.
        Assert.Equal(0, await relay.PostOutcomeAsync($"admin/owner/rooms/{Room}/members/remove?member=Alpha"));

        // DELETE /state/{name}, which turning sharing off sends, is what lifts it.
        relay.Moderation.Left(Room, "Alpha");
        await relay.PublishAsync("Alpha");
        Assert.Single(Members(await relay.PublishAsync("Carol")));
    }

    [Fact]
    public async Task Resetting_a_room_clears_marks_and_lines_and_members_come_back_without_their_lines()
    {
        await using var relay = await AdminRelay.StartAsync();
        await relay.PublishAsync("Alpha", Line("a-1"));
        relay.Marks.AddWaypoint(Room, "Alpha", "customs", 1, 0, 2, null);

        Assert.True(await relay.PostOutcomeAsync($"admin/owner/rooms/{Room}/reset") >= 3);
        Assert.Empty(relay.Rooms.AdminSnapshot());

        await relay.PublishAsync("Alpha", Line("a-1"));
        var carol = await relay.PublishAsync("Carol");
        Assert.Single(Members(carol));
        Assert.Empty(LineIds(carol));
        Assert.Empty(carol.GetProperty("waypoints").EnumerateArray());
    }

    [Fact]
    public async Task The_room_list_names_members_counts_and_activity()
    {
        await using var relay = await AdminRelay.StartAsync();
        await relay.PublishAsync("Alpha", Line("a-1"), Line("a-2"));
        relay.Marks.AddPing(Room, "Alpha", "customs", 3, 0, 4, null);

        var rooms = await new RelayAdminClient(relay.OwnerSender).ReadRoomsAsync(CancellationToken.None);

        var room = Assert.Single(rooms!);
        Assert.Equal(Room, room.Room);
        var member = Assert.Single(room.Members!);
        Assert.Equal("Alpha", member.Name);
        Assert.Equal(2, member.Drawings);
        Assert.Equal(1, room.Pings);
        Assert.NotNull(room.LastActivityUtc);
    }

    [Fact]
    public async Task A_desktop_the_owner_removed_says_so_rather_than_that_the_relay_failed()
    {
        await using var relay = await AdminRelay.StartAsync();
        using var http = new HttpClient();
        var state = new RuntimeStateStore(new RuntimeOptions(false, Offline: true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5)));
        await using var alpha = new GroupSessionService(
            new FixedSettings(relay.Address, "Alpha"),
            state,
            http,
            NullLogger<GroupSessionService>.Instance);
        alpha.Start();
        Assert.True(await UntilAsync(() => state.Current.Group.IsSharing), "Alpha should reach the relay.");

        var removed = await new RelayAdminClient(relay.OwnerSender).RemoveMemberAsync(Room, "Alpha", CancellationToken.None);
        Assert.Equal(RelayAdminCallOutcome.Done, removed.Outcome);

        Assert.True(
            await UntilAsync(() => Equals(state.Current.Group.Status?.Code, GroupStatus.RemovedByOwner)),
            $"Alpha should say it was removed, not '{state.Current.Group.Status?.Code}'.");
    }

    [Fact]
    public async Task The_desktop_client_reads_a_relay_from_before_the_controls_as_unsupported()
    {
        var client = new RelayAdminClient((_, _, _) => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.False(await client.IsRelayOwnerAsync(CancellationToken.None));
        Assert.Null(await client.ReadRoomsAsync(CancellationToken.None));
        Assert.Equal(RelayAdminCallOutcome.Unsupported, (await client.ClearDrawingsAsync(Room, null, CancellationToken.None)).Outcome);
    }

    private static async Task<bool> UntilAsync(Func<bool> ready)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20))
        {
            if (ready())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return ready();
    }

    private static object Line(string id) => new { id, mapId = "customs", points = new[] { 1.0, 2.0, 3.0, 4.0 } };

    private static IEnumerable<JsonElement> Members(JsonElement room) => room.GetProperty("members").EnumerateArray();

    private static string[] LineIds(JsonElement room) =>
        [.. Members(room)
            .Where(member => member.TryGetProperty("drawings", out var drawings) && drawings.ValueKind == JsonValueKind.Array)
            .SelectMany(member => member.GetProperty("drawings").EnumerateArray())
            .Select(drawing => drawing.GetProperty("id").GetString()!)
            .Order(StringComparer.Ordinal)];

    private static int CountLines(JsonElement room) => LineIds(room).Length;

    private enum Caller
    {
        Nobody,
        GroupMember,
        Squadmate,
        Owner,
        AdminKey,
    }

    /// <summary>
    /// A relay with the production routes this touches, a legacy owner claimed with the admin
    /// key's ceremony (the relay owner) and a squadmate's desktop registered with the group key.
    /// </summary>
    private sealed class AdminRelay : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly OwnerRecoveryProtector _recovery;
        private readonly HttpClient _client;
        private readonly RelaySessionCredential _owner;
        private readonly RelaySessionCredential _squadmate;

        private AdminRelay(
            WebApplication app,
            OwnerRecoveryProtector recovery,
            HttpClient client,
            RelaySessionCredential owner,
            RelaySessionCredential squadmate,
            GroupRooms rooms,
            GroupMarks marks,
            GroupRoomModeration moderation)
        {
            _app = app;
            _recovery = recovery;
            _client = client;
            _owner = owner;
            _squadmate = squadmate;
            Rooms = rooms;
            Marks = marks;
            Moderation = moderation;
        }

        public GroupRooms Rooms { get; }

        public GroupMarks Marks { get; }

        public GroupRoomModeration Moderation { get; }

        public string Address => _client.BaseAddress!.AbsoluteUri;

        public static async Task<AdminRelay> StartAsync()
        {
            var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
            var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x52, 32).ToArray(), clock);
            var legacy = await RelayDeviceRegistry.OpenAsync(clock, recovery);
            var directory = await RelayTenantDirectory.OpenAsync(
                clock, recovery, legacy, new OpaqueRelayFrameHub(legacy, clock), new RelayMapSurfaceStore(legacy, clock), null);

            using var ownerSigner = new Signer();
            var ownerClaim = DesktopRelayOwnerClaim.Build(ownerSigner, new CompanionDeviceId(Guid.NewGuid()), clock.UtcNow).ToCompletedAttempt();
            var grant = recovery.CreateGrant(ownerClaim.Establishment!.Assignment.DeviceId, ownerClaim.Request!.DeviceKey.KeyId);
            var owner = await legacy.RecoverOwnerAsync(grant, ownerClaim, CompanionSurfaceKind.Desktop);
            Assert.True(owner.Succeeded, owner.Code);

            using var squadSigner = new Signer();
            var squadClaim = DesktopRelayOwnerClaim.Build(squadSigner, new CompanionDeviceId(Guid.NewGuid()), clock.UtcNow).ToCompletedAttempt();
            var squadmate = await directory.RegisterDesktopAsync(Room, squadClaim);
            Assert.True(squadmate.Succeeded, squadmate.Code);

            var rooms = new GroupRooms(clock);
            var marks = new GroupMarks(clock);
            var changes = new GroupRoomChanges(clock);
            var moderation = new GroupRoomModeration(clock);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.MapGroupRoomState(rooms, marks, changes, moderation);
            app.MapRelayCompanionRoutes(directory, recovery, new RelayOwnerClaimGate(clock));
            app.MapRelayOwnerAdmin(directory, rooms, marks, changes, moderation);
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.First().TrimEnd('/') + "/") };
            return new AdminRelay(app, recovery, client, owner.Value!, squadmate.Value!, rooms, marks, moderation);
        }

        /// <summary>The owner's desktop, as <see cref="RelayMarksBridge.SendAsOwnerAsync"/> sends.</summary>
        public Task<HttpResponseMessage?> OwnerSender(HttpMethod method, string path, CancellationToken cancellationToken) =>
            SendAsync($"{method.Method} {path}", Caller.Owner)!;

        public async Task<HttpResponseMessage> SendAsync(string route, Caller caller)
        {
            var (method, path) = (route.Split(' ')[0], route.Split(' ')[1]);
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            switch (caller)
            {
                case Caller.GroupMember:
                    request.Headers.Add("X-Group-Key", Key);
                    break;
                case Caller.Squadmate:
                    request.Headers.Add("X-Relay-Session", _squadmate.SessionId.Value.ToString("D"));
                    request.Headers.Add("X-Relay-Credential", _squadmate.Secret);
                    break;
                case Caller.Owner:
                    request.Headers.Add("X-Relay-Session", _owner.SessionId.Value.ToString("D"));
                    request.Headers.Add("X-Relay-Credential", _owner.Secret);
                    break;
                case Caller.AdminKey:
                    request.Headers.Add(RelayAdmin.Header, LinkRelay.AdminKey);
                    break;
            }

            return await _client.SendAsync(request);
        }

        public async Task<bool> IsRelayOwnerAsync(Caller caller)
        {
            using var response = await SendAsync("GET " + RelayOwnerAdminRoutes.StatusPath.TrimStart('/'), caller);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("relayOwner").GetBoolean();
        }

        public async Task<int> PostOutcomeAsync(string path)
        {
            using var response = await SendAsync("POST " + path, Caller.Owner);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cleared").GetInt32();
        }

        public Task<HttpResponseMessage> PublishRawAsync(string name, params object[] drawings)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "state")
            {
                Content = JsonContent.Create(new
                {
                    name,
                    raidState = "InRaid",
                    mapId = "customs",
                    loadout = Array.Empty<string>(),
                    quests = Array.Empty<string>(),
                    drawings = drawings.Length == 0 ? null : drawings,
                }),
            };
            request.Headers.Add("X-Group-Key", Key);
            return _client.SendAsync(request);
        }

        public async Task<JsonElement> PublishAsync(string name, params object[] drawings)
        {
            using var response = await PublishRawAsync(name, drawings);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _recovery.Dispose();
        }
    }

    private sealed class FixedSettings(string address, string name) : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GroupSharingSettings(true, address, name, Key, false, false));

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class Signer : IDesktopIdentitySigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public Signer()
        {
            var spki = _key.ExportSubjectPublicKeyInfo();
            PublicKey = new DesktopIdentityKey(
                new DeviceKeyId(RelaySecurityTestFactory.Base64Url(SHA256.HashData(spki))),
                DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
                RelaySecurityTestFactory.Base64Url(spki));
        }

        public DesktopIdentityKey PublicKey { get; }

        public byte[] Sign(ReadOnlySpan<byte> signatureInput) =>
            _key.SignData(signatureInput.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        public void Dispose() => _key.Dispose();
    }
}
