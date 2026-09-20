using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// #309: what a tidy shows before it acts, what a dry run does, and what it does when the folder, a file or
/// the recycle bin does not behave. It removes files the player did not make, so every one of these is a
/// promise about a file that must still be there.
/// </summary>
public sealed class ScreenshotTidyPreviewTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 20, 0, 0, TimeSpan.Zero);
    private static readonly ScreenshotRetentionSettings On = ScreenshotRetentionSettings.Default with { IsEnabled = true };

    private readonly string _folder = Directory.CreateTempSubdirectory("tarkov-tidy").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ThePreviewNamesTheFolderTheFilesTheCountTheBytesTheAgeAndWhatStays()
    {
        var oldest = Write("2026-09-10[14-05]_1_a.png", Now.AddDays(-5), 2_000);
        var old = Write("2026-09-11[10-00]_2_a.png", Now.AddDays(-4), 3_000);
        Write("2026-09-19[19-30]_3_a.png", Now.AddMinutes(-30), 500);
        Write("2026-09-19[19-59]_4_a.png", Now.AddMinutes(-1), 500);
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "keep");
        // A screenshot's own folder inside the folder is not this folder's business either.
        var inner = Directory.CreateDirectory(Path.Combine(_folder, "2026-01-01[10-00]_dir.png")).FullName;

        // Tidying is off, as it is when somebody is deciding whether to turn it on.
        var plan = Service(new RecordingBin()).Plan(_folder, ScreenshotRetentionSettings.Default);

        Assert.False(plan.IsRefused);
        Assert.Equal(Path.GetFullPath(_folder), plan.Root);
        Assert.Equal(24, plan.RetentionHours);
        Assert.Equal([oldest, old], plan.Eligible.Select(file => file.Name));
        Assert.Equal(5_000, plan.TotalBytes);
        Assert.Equal(1, plan.ExcludedCount(TidySkipReason.NewestKept));
        Assert.Equal(1, plan.ExcludedCount(TidySkipReason.TooRecent));
        Assert.Equal(1, plan.NotGameFiles);
        Assert.True(Directory.Exists(inner));
    }

    [Fact]
    public void ADryRunMovesNothingAndShowsExactlyWhatARealRunThenDoes()
    {
        Write("2026-09-10[14-05]_1_a.png", Now.AddDays(-5), 100);
        Write("2026-09-11[10-00]_2_a.png", Now.AddDays(-4), 100);
        Write("2026-09-19[19-59]_4_a.png", Now.AddMinutes(-1), 100);
        var bin = new RecordingBin();
        var service = Service(bin);

        var preview = service.Run(_folder, On, dryRun: true);

        Assert.True(preview.DryRun);
        Assert.Equal(0, preview.Moved);
        Assert.Empty(bin.Calls);
        Assert.Equal(3, Directory.GetFiles(_folder).Length);

        var real = service.Run(_folder, On, dryRun: false);

        Assert.Equal(preview.Plan.Eligible.Select(file => file.Name), real.Plan.Eligible.Select(file => file.Name));
        Assert.Equal(2, real.Moved);
        Assert.Equal(200, real.MovedBytes);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankFolderIsRefusedWithAReason(string root)
    {
        var plan = Service(new RecordingBin()).Plan(root, On);

        Assert.True(plan.IsRefused);
        Assert.Empty(plan.Eligible);
    }

    [Fact]
    public void AMissingFolderAndADriveRootAreRefusedAndNothingIsRead()
    {
        var bin = new RecordingBin();
        var service = Service(bin);

        var missing = service.Run(Path.Combine(_folder, "gone"), On, dryRun: false);
        var driveRoot = service.Run(Path.GetPathRoot(_folder)!, On, dryRun: false);

        Assert.Equal("That folder does not exist.", missing.Plan.Refusal);
        Assert.Contains("drive root", driveRoot.Plan.Refusal, StringComparison.Ordinal);
        Assert.Empty(bin.Calls);
    }

    [Fact]
    public void ASymbolicLinkNamedLikeAScreenshotIsLeftAloneAndWhatItPointsAtIsUntouched()
    {
        var target = Path.Combine(Directory.CreateTempSubdirectory("tarkov-tidy-target").FullName, "precious.png");
        File.WriteAllText(target, "not a screenshot");
        var link = Path.Combine(_folder, "2026-09-01[10-00]_link_a.png");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows without the privilege to make one: the rule is still asserted by IsCloudOnly's tests.
            return;
        }

        File.SetLastWriteTimeUtc(link, Now.AddDays(-9).UtcDateTime);
        Write("2026-09-19[19-59]_4_a.png", Now.AddMinutes(-1), 10);
        var bin = new RecordingBin();

        var result = Service(bin).Run(_folder, On, dryRun: false);

        Assert.Equal(0, result.Moved);
        Assert.Contains(result.Plan.Excluded, item => item.Reason == TidySkipReason.LinkOrReparsePoint);
        Assert.Empty(bin.Calls);
        Assert.Equal("not a screenshot", File.ReadAllText(target));
    }

    [Fact]
    public void AFileRewrittenAfterItWasPlannedIsLeftAloneAndSaidSo()
    {
        var first = Write("2026-09-10[14-05]_1_a.png", Now.AddDays(-5), 100);
        var second = Write("2026-09-11[10-00]_2_a.png", Now.AddDays(-4), 100);
        Write("2026-09-19[19-59]_4_a.png", Now.AddMinutes(-1), 100);
        // While the first is being moved, the second is replaced by a different picture.
        var bin = new RecordingBin
        {
            OnRecycle = path =>
            {
                if (path.EndsWith(first, StringComparison.Ordinal))
                {
                    File.WriteAllText(Path.Combine(_folder, second), "a different, longer picture");
                }
            },
        };

        var result = Service(bin).Run(_folder, On, dryRun: false);

        Assert.Equal(1, result.Moved);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(second, failure.Name);
        Assert.Contains("changed", failure.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_folder, second)));
    }

    [Fact]
    public void OneFileThatCannotBeMovedDoesNotStopTheRestAndEachFailureSaysWhy()
    {
        var names = new[]
        {
            Write("2026-09-01[10-00]_1_a.png", Now.AddDays(-9), 10),
            Write("2026-09-02[10-00]_2_a.png", Now.AddDays(-8), 10),
            Write("2026-09-03[10-00]_3_a.png", Now.AddDays(-7), 10),
            Write("2026-09-04[10-00]_4_a.png", Now.AddDays(-6), 10),
            Write("2026-09-05[10-00]_5_a.png", Now.AddDays(-5), 10),
        };
        Write("2026-09-19[19-59]_z_a.png", Now.AddMinutes(-1), 10);
        var bin = new RecordingBin
        {
            Throw =
            {
                [names[1]] = new UnauthorizedAccessException(),
                [names[2]] = new IOException("in use"),
            },
            Refuse = { names[3] },
        };
        var ledger = new MemoryLedger();

        var result = Service(bin, ledger).Run(_folder, On, dryRun: false);

        Assert.Equal(2, result.Moved);
        Assert.Equal(3, result.Failures.Count);
        Assert.Contains(result.Failures, failure => failure.Reason.Contains("permission", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Reason.Contains("another program", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Reason.Contains("would not take", StringComparison.Ordinal));
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal((2, 3), (entry.Moved, entry.Failed));
        Assert.Equal(3, entry.FailureReasons.Values.Sum());
    }

    [Fact]
    public void ARunThatDidNothingLeavesNoLedgerEntryAndALedgerThatFailsDoesNotFailTheRun()
    {
        Write("2026-09-19[19-59]_z_a.png", Now.AddMinutes(-1), 10);
        var quiet = new MemoryLedger();
        Assert.Equal(0, Service(new RecordingBin(), quiet).Run(_folder, On, dryRun: false).Moved);
        Assert.Empty(quiet.Entries);

        Write("2026-09-01[10-00]_1_a.png", Now.AddDays(-9), 10);
        var full = new MemoryLedger { Failure = new IOException("There is not enough space on the disk.") };

        var result = Service(new RecordingBin(), full).Run(_folder, On, dryRun: false);

        Assert.Equal(1, result.Moved);
        Assert.True(result.LedgerFailed);
    }

    [Fact]
    public void ALedgerFileKeepsTwentyRunsWithNoFileNamesAndSurvivesCorruption()
    {
        var path = Path.Combine(_folder, "ledger", "screenshot-tidy-ledger.json");
        var ledger = new JsonFileScreenshotTidyLedger(path);
        for (var run = 0; run < 25; run++)
        {
            ledger.Append(new(Now.AddMinutes(run), 24, run, run * 1_000L, 0, new Dictionary<string, int>()));
        }

        var kept = ledger.Read();

        Assert.Equal(20, kept.Count);
        Assert.Equal(5, kept[0].Moved);
        Assert.Equal(24, kept[^1].Moved);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));

        File.WriteAllText(path, "{ this is not json");
        Assert.Empty(ledger.Read());
        ledger.Append(new(Now, 24, 1, 1, 0, new Dictionary<string, int>()));
        Assert.Single(ledger.Read());
    }

    [Fact]
    public void ARealRunWritesItsCountsToTheLedgerFileButNeverTheNamesOfTheScreenshots()
    {
        var name = Write("2026-09-01[10-00]_Secret Position 12.5, 3.0_a.png", Now.AddDays(-9), 10);
        Write("2026-09-19[19-59]_z_a.png", Now.AddMinutes(-1), 10);
        var path = Path.Combine(_folder, "ledger.json");

        Service(new RecordingBin(), new JsonFileScreenshotTidyLedger(path)).Run(_folder, On, dryRun: false);

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("Secret Position", text, StringComparison.Ordinal);
        Assert.DoesNotContain(name, text, StringComparison.Ordinal);
        Assert.Equal(1, new JsonFileScreenshotTidyLedger(path).Read().Single().Moved);
    }

    private ScreenshotRetentionService Service(IRecycleBin bin, IScreenshotTidyLedger? ledger = null) =>
        new(bin, new FixedClock(Now), ledger);

    private string Write(string name, DateTimeOffset writtenUtc, int bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, new string('x', bytes));
        File.SetLastWriteTimeUtc(path, writtenUtc.UtcDateTime);
        return name;
    }

    private sealed class RecordingBin : IRecycleBin
    {
        public List<string> Calls { get; } = [];

        public Dictionary<string, Exception> Throw { get; } = [];

        public HashSet<string> Refuse { get; } = [];

        public Action<string>? OnRecycle { get; init; }

        public bool IsAvailable => true;

        public bool Recycle(string path)
        {
            Calls.Add(path);
            OnRecycle?.Invoke(path);
            var name = Path.GetFileName(path);
            if (Throw.TryGetValue(name, out var exception))
            {
                throw exception;
            }

            if (Refuse.Contains(name))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }

    private sealed class MemoryLedger : IScreenshotTidyLedger
    {
        public List<TidyLedgerEntry> Entries { get; } = [];

        public Exception? Failure { get; init; }

        public IReadOnlyList<TidyLedgerEntry> Read() => Entries;

        public void Append(TidyLedgerEntry entry)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Entries.Add(entry);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
