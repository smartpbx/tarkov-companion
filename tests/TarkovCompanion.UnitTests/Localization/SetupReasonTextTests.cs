using System.Globalization;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.V2.SelfTest;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests.Localization;

/// <summary>
/// [#314] The Setup reasons the Application now returns as codes read, in English, exactly as the
/// sentences they replaced. Each expected string is the old sentence, copied from the producer.
/// </summary>
public sealed class SetupReasonTextTests
{
    [Fact]
    public void Group_status_lines_read_as_they_did()
    {
        var sharing = new Phrase(GroupStatus.SharingAlone, "Geo");
        var two = Phrase.Counted(GroupStatus.SharingWith, 2, "Geo");
        InEnglish(() =>
        {
            Assert.Equal("Not sharing", SetupText.GroupStatus(GroupSnapshot.Off));
            Assert.Equal("Needs the group's server address", Say(new Phrase(GroupStatus.Needs, new Phrase(GroupSettingsGap.ServerAddress))));
            Assert.Equal($"Needs a group key of at least {GroupKeyLimits.Minimum} characters", Say(new Phrase(GroupStatus.Needs, new Phrase(GroupSettingsGap.KeyTooShort, GroupKeyLimits.Minimum))));
            Assert.Equal($"Needs a group key of at most {GroupKeyLimits.Maximum} characters", Say(new Phrase(GroupStatus.Needs, new Phrase(GroupSettingsGap.KeyTooLong, GroupKeyLimits.Maximum))));
            Assert.Equal(
                $"The group key must be between {GroupKeyLimits.Minimum} and {GroupKeyLimits.Maximum} characters",
                Say(new Phrase(GroupStatus.KeyLength, GroupKeyLimits.Minimum, GroupKeyLimits.Maximum)));
            Assert.Equal("Server answered 502 · last heard 42 s ago", Say(new Phrase(GroupStatus.LastHeard, new Phrase(GroupStatus.ServerAnswered, 502), TimeSpan.FromSeconds(42))));
            Assert.Equal("Server unreachable · No route to host", Say(new Phrase(GroupStatus.ServerUnreachable, "No route to host")));
            Assert.Equal("Sharing as Geo · nobody else here", Say(sharing));
            Assert.Equal("Sharing as Geo · 1 other", Say(Phrase.Counted(GroupStatus.SharingWith, 1, "Geo")));
            Assert.Equal("Sharing as Geo · 2 others", Say(two));
            Assert.Equal(
                "Sharing as Geo · 2 others · no position: the game's screenshot folder has not been found, set it in Settings",
                Say(new Phrase(GroupStatus.NoPositionFolder, two)));
            Assert.Equal(
                "Sharing as Geo · nobody else here · Relay speaks 2, this build speaks 1 · it updates itself within half an hour",
                Say(new Phrase(GroupStatus.WithSkew, sharing, new Phrase(GroupStatus.RelaySkew, 2, 1))));
            Assert.Equal(
                "The group settings file was unreadable and has been reset. The old one is at group.json.bad.",
                Say(new Phrase(GroupStatus.SettingsResetAside, "group.json.bad")));
        });
    }

    [Fact]
    public void Group_settings_gap_reads_as_the_missing_piece_did()
    {
        var cases = new[]
        {
            new GroupSharingSettings(true, null, "Geo", "abcdefgh", false, true),
            new GroupSharingSettings(true, "not a uri", "Geo", "abcdefgh", false, true),
            new GroupSharingSettings(true, "http://relay.example.com/", "Geo", "abcdefgh", false, true),
            new GroupSharingSettings(true, "https://relay.example.com/", null, "abcdefgh", false, true),
            new GroupSharingSettings(true, "https://relay.example.com/", "Geo", null, false, true),
            new GroupSharingSettings(true, "https://relay.example.com/", "Geo", "short", false, true),
            new GroupSharingSettings(true, "https://relay.example.com/", "Geo", new string('k', 200), false, true),
            new GroupSharingSettings(true, "https://relay.example.com/", "Geo", "abcdefgh", false, true),
        };
        InEnglish(() =>
        {
            foreach (var settings in cases)
            {
                Assert.Equal(settings.MissingPiece, settings.Gap is { } gap ? PhraseText.Say(gap) : null);
            }
        });
    }

    [Fact]
    public void A_fixture_group_line_is_said_as_written() =>
        InEnglish(() => Assert.Equal("Sharing", SetupText.GroupStatus(new GroupSnapshot(true, [], "Sharing", DateTimeOffset.UnixEpoch))));

    [Fact]
    public void Ocr_reasons_and_the_scanner_line_read_as_they_did()
    {
        var missing = OcrEngineAvailability.Unavailable("tesseract", OcrUnavailableReason.NativeLibraryMissing, "A packaged native OCR library or its Visual C++ runtime dependency is unavailable.");
        var failed = OcrEngineAvailability.Unavailable("tesseract", OcrUnavailableReason.ProviderFailed, "InvalidOperationException: OCR provider initialization or execution failed.", "InvalidOperationException");
        var windows = OcrEngineAvailability.Unavailable("windows-media-ocr", OcrUnavailableReason.CouldNotStart, "Windows could not start its OCR engine: boom", "boom");
        InEnglish(() =>
        {
            foreach (var availability in new[] { missing, failed, windows })
            {
                Assert.Equal(availability.Reason, SetupText.OcrReason(availability));
            }

            Assert.Equal("written by a test", SetupText.OcrReason(new OcrEngineAvailability(false, "x", "written by a test")));
            Assert.Equal(
                "Scanning cannot run: A packaged native OCR library or its Visual C++ runtime dependency is unavailable.",
                Say(new Phrase(ScannerStatus.CannotRun, missing.Why)));
            Assert.Equal("Scanning cannot run; tesseract did not start.", Say(new Phrase(ScannerStatus.DidNotStart, "tesseract")));
            Assert.Equal("Ready. Take a screenshot with the game's own key and it will be read.", Say(new Phrase(ScannerStatus.Ready)));
        });
    }

    [Fact]
    public void Data_detail_reads_as_it_did()
    {
        var failures = new Phrase(
            DataDetail.AndAlso,
            new Phrase(DataDetail.EndpointFailed, "items", "HTTP 503"),
            new Phrase(DataDetail.EndpointFailed, "maps", DataDetail.UnknownReason));
        InEnglish(() =>
        {
            Assert.Equal("Refreshed from 7 endpoints", Say(new Phrase(DataDetail.Refreshed, 7)));
            Assert.Equal("Refreshed from 7 endpoints · 2 served a cached copy", Say(new Phrase(DataDetail.RefreshedWithStale, 7, 2)));
            Assert.Equal("items: HTTP 503; maps: unknown reason · local data stands", Say(new Phrase(DataDetail.LocalDataStands, failures)));
            Assert.Equal("No game items are available; items: HTTP 503; maps: unknown reason.", Say(new Phrase(DataDetail.NoItems, failures)));
            Assert.Equal(
                "This profile has no game mode. Choose PvP, PvE or Seasonal to load game data.",
                SetupText.DataDetail(new RuntimeDataState(DataAvailability.Unavailable, 0, 0, null, "ignored").Saying(DataDetail.NoGameMode)));
            Assert.Equal("fixture", SetupText.DataDetail(new RuntimeDataState(DataAvailability.Current, 1, 1, null, "fixture")));
        });
    }

    [Fact]
    public void Quest_import_reasons_read_as_the_stored_english()
    {
        InEnglish(() =>
        {
            foreach (var reason in Enum.GetValues<QuestImportReason>())
            {
                var stored = QuestImportReasons.Stored(reason);
                Assert.Equal(stored, PhraseText.Say(reason));
                Assert.Equal(stored, SetupText.QuestImportReasonStored(stored));
            }

            Assert.Equal("an adapter said this", SetupText.QuestImportReasonStored("an adapter said this"));
        });
    }

    [Fact]
    public void Profile_preview_lines_read_as_they_did()
    {
        InEnglish(() =>
        {
            Assert.Equal("Keep and sell marks", SetupText.ProfileArea(ProfileBundleArea.KeepAndSellMarks));
            Assert.Equal("Raids from another wipe", SetupText.ProfileArea(ProfileBundleArea.RaidsFromAnotherWipe));
            Assert.Equal("12 set from the file", Say(new Phrase(ProfileBundleValue.SetFromFile, 12)));
            Assert.Equal("5 (2 differ)", Say(new Phrase(ProfileBundleValue.Differ, 5, 2)));
            Assert.Equal("not imported", Say(new Phrase(ProfileBundleValue.NotImported)));
            Assert.Equal("42", Say(new Phrase(ProfileBundleValue.Plain, "42")));
            Assert.Equal("There is no active profile; import it as a new one.", SetupText.ProfileTransferRefusal(new(ProfileGameMode.Pve, null, null)));
            Assert.Equal(
                "This file is PvE and Main is PvP. Import it as a new profile.",
                SetupText.ProfileTransferRefusal(new(ProfileGameMode.Pve, "Main", ProfileGameMode.Pvp)));
            Assert.Equal(
                "This file is Seasonal and Main is an unknown mode. Import it as a new profile.",
                SetupText.ProfileTransferRefusal(new(ProfileGameMode.Seasonal, "Main", ProfileGameMode.Unknown)));
            Assert.Null(SetupText.ProfileTransferRefusal(null));
        });
    }

    [Fact]
    public void Self_test_purposes_and_discovery_read_as_they_did()
    {
        InEnglish(() =>
        {
            Assert.Equal("Install", SetupText.ProbePurpose(SelfTestFolderPurpose.Install));
            Assert.Equal("Logs", SetupText.ProbePurpose(SelfTestFolderPurpose.Logs));
            Assert.Equal("Screenshots", SetupText.ProbePurpose(SelfTestFolderPurpose.Screenshots));
            var failed = new EftInstallDiscoverySnapshot(
                1,
                EftInstallDiscoveryStatus.Unavailable,
                new(null, null, null, Confidence.Unknown),
                DateTimeOffset.UnixEpoch,
                "eft-discovery-unavailable",
                "Escape from Tarkov installation discovery failed (disk gone). Retry discovery or choose valid folders in Settings.")
            {
                Fault = "disk gone",
            };
            Assert.Equal(failed.Detail, SetupText.DiscoveryDetail(failed));
            var ready = new EftInstallDiscoverySnapshot(
                2,
                EftInstallDiscoveryStatus.Ready,
                new(@"D:\EFT", null, null, Confidence.Unknown),
                DateTimeOffset.UnixEpoch,
                "eft-discovery-ready",
                "Escape from Tarkov installation and file roots are available.");
            Assert.Equal(ready.Detail, SetupText.DiscoveryDetail(ready));
            var missing = new EftInstallDiscoverySnapshot(
                3,
                EftInstallDiscoveryStatus.Missing,
                new(null, null, null, Confidence.Unknown),
                DateTimeOffset.UnixEpoch,
                "eft-install-missing",
                "Escape from Tarkov is not installed or could not be found. Install it or choose valid folders in Settings, then retry discovery.");
            Assert.Equal(missing.Detail, SetupText.DiscoveryDetail(missing));
            var gone = new EftInstallDiscoverySnapshot(
                4,
                EftInstallDiscoveryStatus.Invalidated,
                new(null, null, null, Confidence.Unknown),
                DateTimeOffset.UnixEpoch,
                "eft-install-disappeared",
                "The selected Escape from Tarkov installation is no longer available. Reconnect its drive, reinstall, or choose valid folders in Settings.");
            Assert.Equal(gone.Detail, SetupText.DiscoveryDetail(gone));
            Assert.Equal(
                "Escape from Tarkov installation discovery has not run yet.",
                SetupText.DiscoveryDetail(EftInstallDiscoverySnapshot.Uninitialized(DateTimeOffset.UnixEpoch)));
        });
    }

    private static string Say(Phrase phrase) => PhraseText.Say(phrase);

    private static void InEnglish(Action assertions)
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            assertions();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
