using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.UnitTests.Localization;

/// <summary>The capture panel names its words by enum value; every value must have a word in the table.</summary>
public sealed class ShellCaptureTextTests
{
    public static TheoryData<string> EnumKeys()
    {
        var keys = new TheoryData<string>();
        foreach (var intent in Enum.GetValues<ScanIntent>())
        {
            keys.Add($"V2.Shell.Intent.{intent}");
        }

        foreach (var context in Enum.GetValues<RecognizedContext>())
        {
            keys.Add($"V2.Shell.Capture.Context.{context}");
        }

        foreach (var kind in Enum.GetValues<V2CaptureAttentionKind>())
        {
            keys.Add($"V2.Shell.Capture.Attention.{kind}");
            keys.Add($"V2.Shell.Capture.AttentionDetail.{kind}");
        }

        foreach (var stage in Enum.GetValues<CaptureSessionStage>())
        {
            keys.Add($"V2.Shell.Capture.Stage.{stage}");
            keys.Add($"V2.Shell.Capture.StageDetail.{stage}");
        }

        return keys;
    }

    [Theory]
    [MemberData(nameof(EnumKeys))]
    public void Every_enum_value_the_capture_panel_names_reads_from_the_table(string key)
    {
        var log = new List<string>();
        using (UiText.Scope(UiText.Create("en", log.Add)))
        {
            Assert.False(string.IsNullOrWhiteSpace(V2ShellText.Get(key)));
        }

        // The pseudo-locale proves the word comes from the string table, not a leftover literal.
        using (UiText.Scope(UiText.Create(PseudoLocale.Name, log.Add)))
        {
            Assert.StartsWith("[", V2ShellText.Get(key), StringComparison.Ordinal);
        }

        Assert.Empty(log);
    }

    [Fact]
    public void Read_as_names_every_intent_and_no_two_alike()
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        var labels = Enum.GetValues<ScanIntent>().Select(ScanReadAs.Label).ToArray();
        Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
        Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("Quest items", ScanReadAs.Label(ScanIntent.QuestItems));
    }

    [Fact]
    public void A_startup_fault_list_reads_as_one_sentence()
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        Assert.Equal("Hideout, Ammo and Map did not load", TarkovCompanion.App.ViewModels.V2.Shell.V2ShellViewModel.DescribeStartupFaults(["hideout", "ammo", "map"]));
    }
}

/// <summary>#863: the TarkovTracker line ran two phrases together with no separator.</summary>
public sealed class TarkovTrackerStatusLineTests
{
    private static TarkovTrackerIntegrationStatus Status(
        bool secureStorage = true,
        bool connected = false,
        int? remaining = null,
        int? limit = null) =>
        new(
            FeatureEnabled: true,
            NetworkAccessEnabled: true,
            SecureStorageAvailable: secureStorage,
            Connected: connected,
            RequiresReconnect: false,
            GameMode: GameMode.Regular,
            LastCheckedUtc: null,
            SnapshotFetchedUtc: null,
            NextEligibleRefreshUtc: null,
            Quota: new(limit, remaining, null));

    [Fact]
    public void Each_part_is_separated_from_the_next()
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        Assert.Equal(
            "Off · this machine has no protected storage for the token · Read quota unknown",
            TarkovTrackerStatusLine.Compose(Status(secureStorage: false)));
    }

    [Fact]
    public void The_operation_leads_and_the_quota_follows()
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        Assert.Equal(
            "Token checked and saved · Connected · PvP · Read quota 12/15 left",
            TarkovTrackerStatusLine.Compose(Status(connected: true, remaining: 12, limit: 15), SetupText.QuestsTrackerTokenSaved));
    }
}
