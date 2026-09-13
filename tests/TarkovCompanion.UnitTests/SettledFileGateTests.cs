using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.UnitTests;

public sealed class SettledFileGateTests
{
    /// <summary>
    /// A file is never handed on the first time it is seen.
    /// </summary>
    /// <remarks>
    /// The bug this exists to stop: the listing shows a screenshot the moment the game creates
    /// it, and the picture arrives afterwards. Yielding on first sight handed the recogniser a
    /// frame with black rows where the rest of it had not landed yet — and, because the path
    /// went into a seen set at the same moment, it was never looked at again. One press of the
    /// screenshot key, one chance, and the chance was taken before the file existed in full.
    /// </remarks>
    [Fact]
    public void AFileIsNotHandedOnUntilItsLengthStopsChanging()
    {
        var gate = new SettledFileGate();

        Assert.False(gate.IsSettled("shot.png", 4_096));
        Assert.False(gate.IsSettled("shot.png", 512_000));
        Assert.False(gate.IsSettled("shot.png", 2_400_000));
        Assert.True(gate.IsSettled("shot.png", 2_400_000));
    }

    /// <summary>Settled once means settled once, not on every poll afterwards.</summary>
    [Fact]
    public void AFileIsOnlyEverHandedOnOnce()
    {
        var gate = new SettledFileGate();
        gate.IsSettled("shot.png", 100);
        Assert.True(gate.IsSettled("shot.png", 100));

        Assert.False(gate.IsSettled("shot.png", 100));
        Assert.False(gate.IsSettled("shot.png", 100));
        Assert.True(gate.WasReleased("shot.png"));
    }

    /// <summary>
    /// A length that cannot be read is a reason to wait, not to proceed.
    /// </summary>
    /// <remarks>
    /// Usually the game still holding the file. Treating the failure as a length would settle
    /// the file against a measurement that never happened.
    /// </remarks>
    [Fact]
    public void AnUnmeasurableFileStartsItsCountAgain()
    {
        var gate = new SettledFileGate();

        Assert.False(gate.IsSettled("shot.png", 2_400_000));
        Assert.False(gate.IsSettled("shot.png", -1));

        // The earlier measurement was discarded, so this is a first sighting again.
        Assert.False(gate.IsSettled("shot.png", 2_400_000));
        Assert.True(gate.IsSettled("shot.png", 2_400_000));
    }

    /// <summary>A frame that failed to decode goes back in the queue rather than being lost.</summary>
    /// <remarks>
    /// The settled length says the writer stopped, not that the picture is whole. A decode that
    /// comes back incomplete after that has to be retryable, or the screenshot is gone — which
    /// is the failure this whole gate exists to prevent, arriving one step later.
    /// </remarks>
    [Fact]
    public void ARetriedFileCanBeHandedOnAgainOnceItSettlesAnew()
    {
        var gate = new SettledFileGate();
        gate.IsSettled("shot.png", 100);
        Assert.True(gate.IsSettled("shot.png", 100));

        gate.Retry("shot.png");
        Assert.False(gate.WasReleased("shot.png"));

        Assert.False(gate.IsSettled("shot.png", 2_400_000));
        Assert.True(gate.IsSettled("shot.png", 2_400_000));
    }

    /// <summary>Files from an earlier session are retired, not watched.</summary>
    [Fact]
    public void AnAlreadyFinishedFileIsRetiredWithoutWaiting()
    {
        var gate = new SettledFileGate();
        gate.Release("yesterday.png");

        Assert.True(gate.WasReleased("yesterday.png"));
        Assert.False(gate.IsSettled("yesterday.png", 100));
        Assert.False(gate.IsSettled("yesterday.png", 100));
    }

    /// <summary>Two screenshots taken seconds apart are tracked independently.</summary>
    /// <remarks>
    /// Players photograph an item and then the extract list on purpose, so the pair has to
    /// settle separately rather than one holding the other up.
    /// </remarks>
    [Fact]
    public void EachFileSettlesOnItsOwnSchedule()
    {
        var gate = new SettledFileGate();

        Assert.False(gate.IsSettled("item.png", 900_000));
        Assert.False(gate.IsSettled("extracts.png", 40_000));
        Assert.False(gate.IsSettled("item.png", 1_800_000));
        Assert.True(gate.IsSettled("extracts.png", 40_000));
        Assert.False(gate.WasReleased("item.png"));

        Assert.True(gate.IsSettled("item.png", 1_800_000));
    }
}
