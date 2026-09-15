using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Infrastructure.Profile;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.Profiles;

public sealed class ProfileTransferV2Tests
{
    private const string FirstProfilePath = "payload.profiles.0";

    [Fact]
    public async Task Wrong_generation_is_quarantined_and_confirm_only_imports_reviewed_new_contexts()
    {
        var store = new MemoryProfileStore();
        using var contexts = new ProfileContextService(store, new ProfileClock(Now));
        var local = Profile("00000000-0000-0000-0000-000000000005", "generation-a", ProfileGameMode.Pvp, "local");
        var incomingWrongGeneration = Profile("00000000-0000-0000-0000-000000000005", "generation-b", ProfileGameMode.Pvp, "wrong");
        var incomingNew = Profile("00000000-0000-0000-0000-000000000006", "generation-c", ProfileGameMode.Pve, "new");
        await contexts.CreateAsync(Request(local), CancellationToken.None);
        var codec = new JsonProfileContextTransferCodec();
        var transfers = new ProfileTransferService(contexts, codec, new ProfileClock(Now));
        var json = codec.Write(new(1, DateTimeOffset.UnixEpoch, [incomingWrongGeneration, incomingNew]));

        var preview = await transfers.PreviewAsync(json, CancellationToken.None);
        var applied = await transfers.ConfirmAsync(preview.ConfirmationId, CancellationToken.None);

        Assert.Contains(preview.Entries, entry => entry.Disposition == ProfileImportDisposition.QuarantinedWrongGeneration);
        Assert.Single(preview.ReadyEntries);
        Assert.Equal(2, applied.Profiles.Count);
        Assert.DoesNotContain(applied.Profiles, profile => profile.Progress.WishlistItemIds.Contains("wrong"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.ConfirmAsync(preview.ConfirmationId, CancellationToken.None));
    }

    /// <summary>
    /// The same stable id and generation under any other context field is a different context.
    /// Each field is changed alone, so a classifier that compared only id and generation fails here.
    /// </summary>
    [Theory]
    [InlineData("generation", ProfileContextMismatch.Generation, ProfileImportDisposition.QuarantinedWrongGeneration)]
    [InlineData("mode", ProfileContextMismatch.Mode, ProfileImportDisposition.QuarantinedIncompatibleContext)]
    [InlineData("wipe", ProfileContextMismatch.WipeSeason, ProfileImportDisposition.QuarantinedIncompatibleContext)]
    [InlineData("language", ProfileContextMismatch.Locale, ProfileImportDisposition.QuarantinedIncompatibleContext)]
    [InlineData("region", ProfileContextMismatch.Locale, ProfileImportDisposition.QuarantinedIncompatibleContext)]
    [InlineData("timeZone", ProfileContextMismatch.Locale, ProfileImportDisposition.QuarantinedIncompatibleContext)]
    [InlineData("snapshotId", ProfileContextMismatch.DataSnapshot, ProfileImportDisposition.QuarantinedIncompatibleContext)]
    [InlineData("snapshotPublished", ProfileContextMismatch.DataSnapshot, ProfileImportDisposition.QuarantinedIncompatibleContext)]
    public async Task Same_id_with_one_different_context_field_is_visibly_quarantined_and_never_imported(
        string field,
        ProfileContextMismatch expectedMismatch,
        ProfileImportDisposition expectedDisposition)
    {
        var id = Id(100);
        var localContext = Context(id, "generation-a", ProfileGameMode.Pvp, "wipe-a", "en-US", "US", "America/New_York", "catalog-a");
        var incomingContext = field switch
        {
            "generation" => Context(id, "generation-b", ProfileGameMode.Pvp, "wipe-a", "en-US", "US", "America/New_York", "catalog-a"),
            "mode" => Context(id, "generation-a", ProfileGameMode.Pve, "wipe-a", "en-US", "US", "America/New_York", "catalog-a"),
            "wipe" => Context(id, "generation-a", ProfileGameMode.Pvp, "wipe-b", "en-US", "US", "America/New_York", "catalog-a"),
            "language" => Context(id, "generation-a", ProfileGameMode.Pvp, "wipe-a", "fr-CA", "US", "America/New_York", "catalog-a"),
            "region" => Context(id, "generation-a", ProfileGameMode.Pvp, "wipe-a", "en-US", "CA", "America/New_York", "catalog-a"),
            "timeZone" => Context(id, "generation-a", ProfileGameMode.Pvp, "wipe-a", "en-US", "US", "America/Chicago", "catalog-a"),
            "snapshotId" => Context(id, "generation-a", ProfileGameMode.Pvp, "wipe-a", "en-US", "US", "America/New_York", "catalog-b"),
            "snapshotPublished" => Context(id, "generation-a", ProfileGameMode.Pvp, "wipe-a", "en-US", "US", "America/New_York", "catalog-a", DateTimeOffset.UnixEpoch.AddDays(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var store = new MemoryProfileStore();
        using var contexts = new ProfileContextService(store, new ProfileClock(Now));
        await contexts.CreateAsync(Request(Profile(localContext, "local")), CancellationToken.None);
        var before = store.Current;
        var published = 0;
        contexts.ContextChanged += _ => published++;
        var codec = new JsonProfileContextTransferCodec();
        var transfers = new ProfileTransferService(contexts, codec, new ProfileClock(Now));

        var preview = await transfers.PreviewAsync(codec.Write(new(1, Now, [Profile(incomingContext, "incoming")])), CancellationToken.None);
        var entry = Assert.Single(preview.Entries);
        var applied = await transfers.ConfirmAsync(preview.ConfirmationId, CancellationToken.None);

        Assert.Equal(expectedDisposition, entry.Disposition);
        Assert.NotNull(entry.Compatibility);
        Assert.Equal([expectedMismatch], entry.Compatibility.Mismatches);
        Assert.Contains(expectedMismatch.ToString(), entry.Message);
        Assert.Empty(preview.ReadyEntries);
        Assert.Same(before, applied);
        Assert.Equal(0, published);
        Assert.Equal(["local"], Assert.Single(applied.Profiles).Progress.WishlistItemIds);
    }

    [Fact]
    public async Task Confirming_a_preview_with_nothing_ready_is_not_a_revision()
    {
        var store = new MemoryProfileStore();
        using var contexts = new ProfileContextService(store, new ProfileClock(Now));
        var local = Profile(110, "generation", ProfileGameMode.Pvp, "local");
        await contexts.CreateAsync(Request(local), CancellationToken.None);
        var published = 0;
        contexts.ContextChanged += _ => published++;
        var codec = new JsonProfileContextTransferCodec();
        var transfers = new ProfileTransferService(contexts, codec, new ProfileClock(Now));

        var present = await transfers.PreviewAsync(codec.Write(new(1, Now, [Profile(local.Context, "different-progress")])), CancellationToken.None);
        var empty = await transfers.PreviewAsync(codec.Write(new(1, Now, [])), CancellationToken.None);
        var afterPresent = await transfers.ConfirmAsync(present.ConfirmationId, CancellationToken.None);
        var afterEmpty = await transfers.ConfirmAsync(empty.ConfirmationId, CancellationToken.None);

        Assert.Equal(ProfileImportDisposition.AlreadyPresent, Assert.Single(present.Entries).Disposition);
        Assert.Empty(empty.Entries);
        Assert.Equal(1, afterPresent.Revision);
        Assert.Equal(1, afterEmpty.Revision);
        Assert.Equal(1, store.ReplaceAttempts);
        Assert.Equal(0, published);
        Assert.Equal(["local"], Assert.Single(afterEmpty.Profiles).Progress.WishlistItemIds);
    }

    [Fact]
    public void Codec_tolerates_unknown_members_and_keeps_them_out_of_the_checksum()
    {
        var codec = new JsonProfileContextTransferCodec();
        var json = codec.Write(new(1, Now, [Profile(120, "generation-a", ProfileGameMode.Pvp, "ledx"), Profile(121, "generation-b", ProfileGameMode.Seasonal, "gpu")]));

        var extended = Edit(json, root =>
        {
            root["producer"] = "a newer writer";
            Node(root, "payload").AsObject()["notes"] = JsonNode.Parse("{\"nested\":[1,2,{\"deep\":null}]}");
            Node(root, FirstProfilePath).AsObject()["futureField"] = 42;
            Node(root, FirstProfilePath + ".context.identity").AsObject()["alias"] = "main";
            Node(root, FirstProfilePath + ".context.locale").AsObject()["calendar"] = "gregorian";
            Node(root, FirstProfilePath + ".progress").AsObject()["prestige"] = JsonNode.Parse("[\"a\",\"b\"]");
        });
        var read = codec.Read(extended);

        Assert.NotEqual(json, extended);
        Assert.Equal(2, read.Profiles.Count);
        Assert.Equal(["ledx"], read.Profiles[0].Progress.WishlistItemIds);

        // Re-exporting what was read reproduces the original bytes: the unknown members were
        // neither hashed nor carried into the verified copy.
        Assert.Equal(json, codec.Write(read));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("payload")]
    [InlineData("payload.exportedUtc")]
    [InlineData("payload.profiles")]
    [InlineData(FirstProfilePath + ".context")]
    [InlineData(FirstProfilePath + ".context.mode")]
    [InlineData(FirstProfilePath + ".context.identity.generation")]
    [InlineData(FirstProfilePath + ".context.wipeSeason")]
    [InlineData(FirstProfilePath + ".context.locale.timeZone")]
    [InlineData(FirstProfilePath + ".context.dataSnapshot.publishedUtc")]
    [InlineData(FirstProfilePath + ".name")]
    [InlineData(FirstProfilePath + ".progress.level")]
    [InlineData(FirstProfilePath + ".lifecycle")]
    [InlineData(FirstProfilePath + ".updatedUtc")]
    public void Codec_rejects_a_document_missing_a_required_member(string path)
    {
        var codec = new JsonProfileContextTransferCodec();
        var json = codec.Write(new(1, Now, [Profile(130, "generation", ProfileGameMode.Pve, "item")]));

        var edited = Edit(json, root => Assert.True(Parent(root, path).AsObject().Remove(LastSegment(path))));
        var exception = Assert.Throws<InvalidDataException>(() => codec.Read(edited));

        Assert.True(exception.InnerException is JsonException, exception.InnerException?.ToString());
    }

    [Theory]
    [InlineData(FirstProfilePath + ".context.mode", "\"regular\"")]
    [InlineData(FirstProfilePath + ".context.mode", "1")]
    [InlineData(FirstProfilePath + ".context.mode", "null")]
    [InlineData(FirstProfilePath + ".lifecycle", "\"deleted\"")]
    [InlineData(FirstProfilePath + ".name", "null")]
    public void Codec_rejects_unknown_enum_names_and_nulls_for_required_values(string path, string value)
    {
        var codec = new JsonProfileContextTransferCodec();
        var json = codec.Write(new(1, Now, [Profile(140, "generation", ProfileGameMode.Unknown, "item")]));

        var edited = Edit(json, root => Parent(root, path).AsObject()[LastSegment(path)] = JsonNode.Parse(value));
        var exception = Assert.Throws<InvalidDataException>(() => codec.Read(edited));

        Assert.True(exception.InnerException is JsonException, exception.InnerException?.ToString());
    }

    [Fact]
    public void Codec_detects_payload_tamper_and_a_well_formed_wrong_checksum()
    {
        var codec = new JsonProfileContextTransferCodec();
        var json = codec.Write(new(1, Now, [Profile(150, "generation", ProfileGameMode.Pvp, "ledx"), Profile(151, "generation-b", ProfileGameMode.Pve, "gpu")]));
        var checksum = Node(JsonNode.Parse(json)!, "checksum").GetValue<string>();
        var flipped = checksum[..^1] + (checksum[^1] == '0' ? "1" : "0");

        string[] tampered =
        [
            Edit(json, root => Node(root, FirstProfilePath).AsObject()["name"] = "renamed-after-export"),
            Edit(json, root => Node(root, FirstProfilePath + ".progress").AsObject()["wishlistItemIds"] = JsonNode.Parse("[\"ledx\",\"bitcoin\"]")),
            Edit(json, root => Node(root, FirstProfilePath + ".context").AsObject()["mode"] = "pve"),
            Edit(json, root => Node(root, "payload").AsObject()["exportedUtc"] = "2030-01-01T00:00:00+00:00"),
            Edit(json, root => ((JsonArray)Node(root, "payload.profiles")).RemoveAt(1)),
            Edit(json, root => root["checksum"] = flipped),
        ];

        Assert.Equal(64, flipped.Length);
        Assert.All(tampered, document =>
        {
            var exception = Assert.Throws<InvalidDataException>(() => codec.Read(document));
            Assert.Contains("checksum does not match", exception.Message);
        });
        Assert.Equal(2, codec.Read(json).Profiles.Count);
    }

    [Fact]
    public void Transfer_rejects_hostile_envelopes_before_they_can_become_a_preview()
    {
        var codec = new JsonProfileContextTransferCodec();
        var json = codec.Write(new(1, DateTimeOffset.UnixEpoch, [Profile("00000000-0000-0000-0000-000000000007", "generation", ProfileGameMode.Unknown, "item")]));

        Assert.Throws<InvalidDataException>(() => codec.Read(json.Replace("\"checksum\":\"", "\"checksum\":\"0", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => codec.Read(Edit(json, root => root["checksum"] = new string('z', 64))));
        Assert.Throws<InvalidDataException>(() => codec.Read(Edit(json, root => root["formatVersion"] = 2)));
        Assert.Throws<InvalidDataException>(() => codec.Read(Edit(json, root => root["formatId"] = "another-format")));
        Assert.Throws<InvalidDataException>(() => codec.Read("{\"formatId\":\"tarkov-companion.profile-context\"}"));
        Assert.Throws<InvalidDataException>(() => codec.Read(new string('x', 2_097_153)));
        Assert.Throws<ArgumentOutOfRangeException>(() => codec.Write(new(1, DateTimeOffset.UnixEpoch, Enumerable.Range(0, 65)
            .Select(index => Profile($"00000000-0000-0000-0000-{index + 100:D12}", $"generation-{index}", ProfileGameMode.Unknown, $"item-{index}")).ToArray())));
    }

    [Fact]
    public async Task Transfer_service_rechecks_the_external_codec_contract_and_rejects_null_codec_output()
    {
        var store = new MemoryProfileStore();
        using var contexts = new ProfileContextService(store, new ProfileClock(Now));
        var wrongVersion = new ProfileTransferService(contexts, new DelegateProfileCodec(_ => new ProfileTransferDocument(2, Now, [])), new ProfileClock(Now));
        var nullRead = new ProfileTransferService(contexts, new DelegateProfileCodec(_ => null), new ProfileClock(Now));
        var nullWrite = new ProfileTransferService(contexts, new DelegateProfileCodec(_ => null, _ => null), new ProfileClock(Now));

        await Assert.ThrowsAsync<InvalidDataException>(() => wrongVersion.PreviewAsync("external-document", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => nullRead.PreviewAsync("external-document", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => nullWrite.ExportAsync(CancellationToken.None));

        Assert.Empty(store.Current.Profiles);
        Assert.Equal(0, store.ReplaceAttempts);
    }

    [Fact]
    public async Task Transfer_apis_reject_null_dependencies_and_arguments()
    {
        using var contexts = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
        var codec = new JsonProfileContextTransferCodec();
        var transfers = new ProfileTransferService(contexts, codec, new ProfileClock(Now));

        Assert.Throws<ArgumentNullException>(() => new ProfileTransferService(null!, codec));
        Assert.Throws<ArgumentNullException>(() => new ProfileTransferService(contexts, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => transfers.PreviewAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => transfers.PreviewAsync("  ", CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => codec.Write(null!));
        Assert.Throws<ArgumentNullException>(() => codec.Read(null!));
        Assert.Throws<ArgumentNullException>(() => new ProfileTransferDocument(1, Now, null!));
        Assert.Throws<ArgumentNullException>(() => new ProfileImportPreview(Guid.NewGuid(), Now, null!));
        Assert.Throws<ArgumentException>(() => new ProfileImportPreview(Guid.NewGuid(), Now, [null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProfileImportPreview(Guid.Empty, Now, []));
    }

    [Fact]
    public async Task Preview_freezes_one_validated_copy_before_the_workspace_read_and_confirm_applies_only_that_copy()
    {
        var reviewed = Profile(160, "generation-reviewed", ProfileGameMode.Pve, "reviewed");
        var swappedIn = Profile(161, "generation-swapped", ProfileGameMode.Pvp, "swapped-in");
        var added = Profile(162, "generation-added", ProfileGameMode.Pvp, "added");
        var codecOwned = new List<ProfileRecord> { reviewed };
        var store = new MemoryProfileStore();
        using var contexts = new ProfileContextService(store, new ProfileClock(Now));
        var transfers = new ProfileTransferService(
            contexts,
            new DelegateProfileCodec(_ => new ProfileTransferDocument(1, Now, codecOwned)),
            new ProfileClock(Now));

        // The codec keeps mutating its list while the service awaits the workspace read.
        store.OnRead = () =>
        {
            codecOwned[0] = swappedIn;
            codecOwned.Add(added);
        };
        var preview = await transfers.PreviewAsync("external-document", CancellationToken.None);
        store.OnRead = null;
        codecOwned.Clear();
        var applied = await transfers.ConfirmAsync(preview.ConfirmationId, CancellationToken.None);

        Assert.Same(reviewed, Assert.Single(preview.Entries).Profile);
        Assert.Same(reviewed, Assert.Single(applied.Profiles));
        Assert.Throws<NotSupportedException>(() => ((IList<ProfileImportEntry>)preview.Entries).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ProfileImportEntry>)preview.ReadyEntries).Clear());

        // A list that changes its answers between reads is observed exactly once, by the document.
        var shifting = new ShiftingProfileList([reviewed], [swappedIn, added]);
        var document = new ProfileTransferDocument(1, Now, shifting);
        var codec = new JsonProfileContextTransferCodec();
        var roundTripped = codec.Read(codec.Write(document));

        Assert.Same(reviewed, Assert.Single(document.Profiles));
        Assert.Equal(reviewed.Context, Assert.Single(roundTripped.Profiles).Context);

        var entries = new List<ProfileImportEntry> { new(reviewed, ProfileImportDisposition.Ready, "ready") };
        var constructed = new ProfileImportPreview(Guid.NewGuid(), Now, entries);
        entries.Add(new(swappedIn, ProfileImportDisposition.Ready, "added later"));
        Assert.Single(constructed.Entries);
        Assert.Single(constructed.ReadyEntries);
    }

    [Fact]
    public async Task Pending_previews_are_bounded_and_abandon_confirm_or_expiry_releases_slots()
    {
        var clock = new ProfileClock(Now);
        var store = new MemoryProfileStore();
        using var contexts = new ProfileContextService(store, clock);
        var codec = new JsonProfileContextTransferCodec();
        var transfers = new ProfileTransferService(contexts, codec, clock);
        var json = codec.Write(new(1, Now, [Profile(170, "generation", ProfileGameMode.Pvp, "item")]));

        var previews = new List<ProfileImportPreview>();
        for (var index = 0; index < ProfileTransferService.MaximumPendingPreviews; index++)
        {
            previews.Add(await transfers.PreviewAsync(json, CancellationToken.None));
        }

        Assert.Equal(ProfileTransferService.MaximumPendingPreviews, previews.Select(preview => preview.ConfirmationId).Distinct().Count());
        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.PreviewAsync(json, CancellationToken.None));

        // An abandoned preview gives its slot back and cannot be confirmed afterwards.
        Assert.True(transfers.Abandon(previews[0].ConfirmationId));
        Assert.False(transfers.Abandon(previews[0].ConfirmationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.ConfirmAsync(previews[0].ConfirmationId, CancellationToken.None));
        previews.Add(await transfers.PreviewAsync(json, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.PreviewAsync(json, CancellationToken.None));
        Assert.Empty(store.Current.Profiles);

        // A confirmation consumes its slot.
        await transfers.ConfirmAsync(previews[1].ConfirmationId, CancellationToken.None);
        previews.Add(await transfers.PreviewAsync(json, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.PreviewAsync(json, CancellationToken.None));

        // Expiry releases every slot without anyone confirming or abandoning.
        clock.Set(previews.Max(preview => preview.ExpiresUtc));
        for (var index = 0; index < ProfileTransferService.MaximumPendingPreviews; index++)
        {
            await transfers.PreviewAsync(json, CancellationToken.None);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.PreviewAsync(json, CancellationToken.None));
        Assert.False(transfers.Abandon(previews[^1].ConfirmationId));
        Assert.Single(store.Current.Profiles);
    }

    [Fact]
    public async Task Preview_is_invalid_from_the_instant_it_expires()
    {
        var clock = new ProfileClock(Now);
        var store = new MemoryProfileStore();
        using var contexts = new ProfileContextService(store, clock);
        var codec = new JsonProfileContextTransferCodec();
        var transfers = new ProfileTransferService(contexts, codec, clock);
        var beforeExpiry = await transfers.PreviewAsync(codec.Write(new(1, Now, [Profile(180, "generation-a", ProfileGameMode.Pvp, "a")])), CancellationToken.None);
        var atExpiry = await transfers.PreviewAsync(codec.Write(new(1, Now, [Profile(181, "generation-b", ProfileGameMode.Pvp, "b")])), CancellationToken.None);
        var evicted = await transfers.PreviewAsync(codec.Write(new(1, Now, [Profile(182, "generation-c", ProfileGameMode.Pvp, "c")])), CancellationToken.None);

        Assert.Equal(Now + ProfileTransferService.PreviewLifetime, atExpiry.ExpiresUtc);
        clock.Set(beforeExpiry.ExpiresUtc - TimeSpan.FromTicks(1));
        var applied = await transfers.ConfirmAsync(beforeExpiry.ConfirmationId, CancellationToken.None);

        clock.Set(atExpiry.ExpiresUtc);
        var expired = await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.ConfirmAsync(atExpiry.ConfirmationId, CancellationToken.None));
        Assert.Contains("expired", expired.Message);
        Assert.False(transfers.Abandon(evicted.ConfirmationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.ConfirmAsync(evicted.ConfirmationId, CancellationToken.None));

        Assert.Equal(Id(180), Assert.Single(applied.Profiles).Context.Identity.ProfileId);
        Assert.Same(applied, store.Current);
    }

    [Fact]
    public async Task Parallel_previews_never_exceed_the_bound_and_one_token_applies_once()
    {
        var clock = new ProfileClock(Now);
        var store = new MemoryProfileStore { YieldOnAccess = true };
        using var contexts = new ProfileContextService(store, clock);
        var codec = new JsonProfileContextTransferCodec();
        var transfers = new ProfileTransferService(contexts, codec, clock);
        var json = codec.Write(new(1, Now, [Profile(190, "generation", ProfileGameMode.Pve, "item")]));

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4 * ProfileTransferService.MaximumPendingPreviews).Select(_ => Task.Run<ProfileImportPreview?>(async () =>
        {
            try
            {
                return await transfers.PreviewAsync(json, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        })));
        var created = attempts.OfType<ProfileImportPreview>().ToArray();

        Assert.Equal(ProfileTransferService.MaximumPendingPreviews, created.Length);
        Assert.Equal(created.Length, created.Select(preview => preview.ConfirmationId).Distinct().Count());

        var target = created[0].ConfirmationId;
        var confirmations = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            try
            {
                await transfers.ConfirmAsync(target, CancellationToken.None);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        })));

        Assert.Single(confirmations, confirmed => confirmed);
        Assert.Single(store.Current.Profiles);
        Assert.Equal(1, store.Current.Revision);

        var abandoned = await Task.WhenAll(created.Skip(1).Select(preview => Task.Run(() => transfers.Abandon(preview.ConfirmationId))));
        Assert.All(abandoned, released => Assert.True(released));
        Assert.Equal(
            ProfileTransferService.MaximumPendingPreviews,
            (await Task.WhenAll(Enumerable.Range(0, ProfileTransferService.MaximumPendingPreviews).Select(_ => transfers.PreviewAsync(json, CancellationToken.None)))).Length);
    }

    [Fact]
    public async Task Export_timestamp_and_preview_expiry_are_generated_in_utc()
    {
        var instant = DateTimeOffset.Parse("2026-09-14T12:00:00Z");
        var clock = new ProfileClock(instant.ToOffset(TimeSpan.FromHours(-7)));
        using var contexts = new ProfileContextService(new MemoryProfileStore(), clock);
        await contexts.CreateAsync(Request(Profile(200, "generation", ProfileGameMode.Pvp, "item")), CancellationToken.None);
        var codec = new JsonProfileContextTransferCodec();
        var transfers = new ProfileTransferService(contexts, codec, clock);

        var exported = await transfers.ExportAsync(CancellationToken.None);
        using var parsed = JsonDocument.Parse(exported);
        var written = parsed.RootElement.GetProperty("payload").GetProperty("exportedUtc").GetDateTimeOffset();
        var read = codec.Read(exported);
        var preview = await transfers.PreviewAsync(exported, CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, written.Offset);
        Assert.Equal(instant.UtcDateTime, written.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, read.ExportedUtc.Offset);
        Assert.Equal(TimeSpan.Zero, Assert.Single(read.Profiles).UpdatedUtc.Offset);
        Assert.Equal(TimeSpan.Zero, preview.ExpiresUtc.Offset);
        Assert.Equal((instant + ProfileTransferService.PreviewLifetime).UtcDateTime, preview.ExpiresUtc.UtcDateTime);
    }

    private static string Edit(string json, Action<JsonNode> edit)
    {
        var root = JsonNode.Parse(json)!;
        edit(root);
        return root.ToJsonString();
    }

    private static JsonNode Node(JsonNode root, string path) =>
        path.Split('.').Aggregate(root, (node, segment) => int.TryParse(segment, out var index) ? node[index]! : node[segment]!);

    private static JsonNode Parent(JsonNode root, string path)
    {
        var separator = path.LastIndexOf('.');
        return separator < 0 ? root : Node(root, path[..separator]);
    }

    private static string LastSegment(string path) => path[(path.LastIndexOf('.') + 1)..];
}
