using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Saying when an option was not understood.
/// </summary>
/// <remarks>
/// An unknown option used to do nothing and say nothing, so an option that had not shipped yet
/// was indistinguishable from an option that had no effect. Somebody ran a probe flag against
/// an older build, saw no region applied, and the available conclusion was that the region had
/// not helped.
/// </remarks>
public sealed class AppCommandLineUnknownOptionTests
{
    [Fact]
    public void Names_an_option_this_build_does_not_have()
    {
        var options = AppCommandLine.Parse(["--ocr-probe", "shot.png", "--ocr-probe-colour", "green"]);

        Assert.Equal(["--ocr-probe-colour"], options.UnknownOptions);
        Assert.Equal("shot.png", options.OcrProbePath);
    }

    [Fact]
    public void Says_nothing_when_everything_was_understood()
    {
        var options = AppCommandLine.Parse([
            "--ocr-probe", "shot.png",
            "--ocr-probe-region", "0.85,0.005,0.145,0.5",
            "--ocr-probe-lines", "40",
            "--developer-mode",
        ]);

        Assert.Empty(options.UnknownOptions);
        Assert.Equal("0.85,0.005,0.145,0.5", options.OcrProbeRegion);
        Assert.Equal(40, options.OcrProbeLines);
    }

    [Fact]
    public void Does_not_mistake_a_value_for_an_option()
    {
        // A path, a page name and a region are all just strings, and one of them starting with
        // a dash would otherwise be reported as an option nobody passed.
        var options = AppCommandLine.Parse(["--page", "Raid", "--output", "report.json"]);

        Assert.Empty(options.UnknownOptions);
    }

    [Fact]
    public void Reports_every_unknown_option_rather_than_the_first()
    {
        var options = AppCommandLine.Parse(["--demo", "--nonsense", "--headless", "--more-nonsense"]);

        Assert.Equal(["--nonsense", "--more-nonsense"], options.UnknownOptions);
        Assert.True(options.Demo);
        Assert.True(options.Headless);
    }

    [Fact]
    public void An_unknown_option_is_not_fatal()
    {
        // A flag from a newer build passed to an older one is a mistake worth naming, not a
        // reason to refuse to start.
        var options = AppCommandLine.Parse(["--from-the-future"]);

        Assert.Single(options.UnknownOptions);
        Assert.False(options.SelfTest);
    }

    [Fact]
    public void A_bare_word_is_not_an_option()
    {
        Assert.Empty(AppCommandLine.Parse(["shot.png", "somethingelse"]).UnknownOptions);
    }
}
