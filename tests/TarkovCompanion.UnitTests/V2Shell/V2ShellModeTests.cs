using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// Choosing a shell at launch: variant B unless asked otherwise, and a clear refusal for anything
/// this build does not have.
/// </summary>
/// <remarks>
/// A launch that asks for a shell this build does not have must not quietly draw a different one,
/// because every screenshot taken from it would then be labelled with the wrong variant.
/// </remarks>
public sealed class V2ShellModeTests
{
    [Fact]
    public void VariantB_is_the_shell_when_nothing_asks_for_another()
    {
        Assert.Equal(V2ShellMode.VariantB, AppCommandLine.Parse([]).UiShell);
        Assert.Equal(V2ShellMode.VariantB, AppCommandLine.Parse(["--demo", "--page", "home"]).UiShell);
    }

    [Fact]
    public void Legacy_remains_available_as_an_explicit_fallback()
    {
        Assert.Equal(V2ShellMode.Legacy, AppCommandLine.Parse(["--ui-shell", "legacy"]).UiShell);
        Assert.Equal(V2ShellMode.Legacy, AppCommandLine.Parse(["--ui-shell", "legacy", "--page", "Raid"]).UiShell);
    }

    [Fact]
    public void A_command_line_built_by_hand_is_legacy()
    {
        // Several test projects construct this record positionally; the new option must not
        // change what they get.
        Assert.Equal(V2ShellMode.Legacy, new AppCommandLine(false, true, false, false, null, null, null).UiShell);
    }

    [Theory]
    [InlineData("legacy", V2ShellMode.Legacy)]
    [InlineData("v2-a", V2ShellMode.VariantA)]
    [InlineData("v2-b", V2ShellMode.VariantB)]
    [InlineData("V2-B", V2ShellMode.VariantB)]
    public void Each_launch_mode_is_selected_by_its_token(string token, V2ShellMode expected)
    {
        var first = AppCommandLine.Parse(["--ui-shell", token]);
        var second = AppCommandLine.Parse(["--ui-shell", token]);

        Assert.Equal(expected, first.UiShell);
        Assert.Equal(first.UiShell, second.UiShell);
        Assert.Empty(first.UnknownOptions);
    }

    [Theory]
    [InlineData("v2-c")]
    [InlineData("v2")]
    [InlineData("a")]
    [InlineData("")]
    public void An_unknown_shell_is_refused_by_name(string token)
    {
        var exception = Assert.Throws<ArgumentException>(() => AppCommandLine.Parse(["--ui-shell", token]));

        Assert.Contains("--ui-shell", exception.Message, StringComparison.Ordinal);
        Assert.Contains("legacy, v2-a, v2-b", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shell_option_without_a_value_is_refused()
    {
        var alone = Assert.Throws<ArgumentException>(() => AppCommandLine.Parse(["--ui-shell"]));
        var followed = Assert.Throws<ArgumentException>(() => AppCommandLine.Parse(["--ui-shell", "--demo"]));

        Assert.Contains("requires a value", alone.Message, StringComparison.Ordinal);
        Assert.Contains("requires a value", followed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tokens_round_trip_and_only_v2_modes_are_previews()
    {
        foreach (var mode in Enum.GetValues<V2ShellMode>())
        {
            Assert.Equal(mode, V2ShellModes.Parse(mode.ToToken()));
        }

        Assert.False(V2ShellMode.Legacy.IsPreview());
        Assert.True(V2ShellMode.VariantA.IsPreview());
        Assert.True(V2ShellMode.VariantB.IsPreview());
        Assert.Throws<ArgumentOutOfRangeException>(() => default(V2ShellMode).ToToken());
    }
}
