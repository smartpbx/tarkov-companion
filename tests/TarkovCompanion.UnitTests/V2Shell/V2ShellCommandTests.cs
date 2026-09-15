using System.Reflection;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// Every command is keyboard reachable and documented, and the shell's words stay inside the boundary.
/// </summary>
public sealed class V2ShellCommandTests
{
    /// <summary>The V2 contract's forbidden capability vocabulary, held to the shell's public surface too.</summary>
    private static readonly string[] ForbiddenVocabulary =
    [
        "GameMemory", "ProcessMemory", "GameplayInput", "MouseInput", "KeyboardInput", "ControllerInput",
        "InputInjection", "SendInput", "Packet", "Overlay", "Hook", "Inject", "ProcessHandle", "LiveEnemy",
        "EnemyTracking", "Esp", "EnemyPosition", "PlayerPosition", "Radar",
    ];

    [Theory]
    [InlineData(V2ShellMode.VariantA)]
    [InlineData(V2ShellMode.VariantB)]
    public void Every_destination_has_a_documented_shortcut_and_no_shortcut_is_a_bare_character(V2ShellMode mode)
    {
        var variant = V2ShellVariants.For(mode);
        var commands = V2ShellCommands.For(variant);
        var gestures = commands.Where(command => command.Gesture is not null).Select(command => command.Gesture!).ToArray();

        foreach (var destination in variant.Destinations.Append(variant.Setup))
        {
            Assert.Contains(commands, command => command.Route == destination.Route && command.Gesture is not null);
        }

        Assert.All(commands, command => Assert.False(string.IsNullOrWhiteSpace(V2ShellText.Get(command.LabelKey))));
        Assert.All(gestures, gesture => Assert.False(V2ShellCommands.IsBareCharacter(gesture), $"{gesture} is a bare character."));
        Assert.Equal(gestures.Length, gestures.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(V2ShellMode.VariantA, "raid")]
    [InlineData(V2ShellMode.VariantB, "home")]
    public void Control_and_a_digit_follow_the_variants_own_order(V2ShellMode mode, string first)
    {
        var commands = V2ShellCommands.For(V2ShellVariants.For(mode));

        var matched = V2ShellCommands.Match(commands, new("1", Control: true, Alt: false, Shift: false), captureShortcutEnabled: true);

        Assert.NotNull(matched);
        Assert.Equal(first, matched.Route?.Value);
    }

    [Fact]
    public void The_capture_shortcut_can_be_switched_off_and_nothing_else_goes_with_it()
    {
        var commands = V2ShellCommands.For(V2ShellVariants.B);
        var chord = new V2KeyChord("C", Control: false, Alt: true, Shift: true);

        Assert.Equal(V2ShellCommandKind.ToggleCapture, V2ShellCommands.Match(commands, chord, captureShortcutEnabled: true)?.Kind);
        Assert.Null(V2ShellCommands.Match(commands, chord, captureShortcutEnabled: false));
        Assert.Equal(
            V2ShellCommandKind.TogglePalette,
            V2ShellCommands.Match(commands, new("K", Control: true, Alt: false, Shift: false), captureShortcutEnabled: false)?.Kind);
    }

    [Fact]
    public void Typing_a_letter_matches_no_command()
    {
        var commands = V2ShellCommands.For(V2ShellVariants.A);

        foreach (var key in new[] { "C", "K", "F", "L", "D", "1" })
        {
            Assert.Null(V2ShellCommands.Match(commands, new(key, Control: false, Alt: false, Shift: false), captureShortcutEnabled: true));
            Assert.Null(V2ShellCommands.Match(commands, new(key, Control: false, Alt: false, Shift: true), captureShortcutEnabled: true));
        }
    }

    [Fact]
    public void The_shell_names_no_game_facing_capability()
    {
        var shellTypes = typeof(V2ShellRouter).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("TarkovCompanion.App.Services.V2.Shell", StringComparison.Ordinal) == true);
        var names = shellTypes
            .SelectMany(type => new[] { type.Name }
                .Concat(type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Select(member => member.Name))
                .Concat(type.IsEnum ? Enum.GetNames(type) : []))
            .ToArray();

        Assert.NotEmpty(names);
        foreach (var forbidden in ForbiddenVocabulary)
        {
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Shell_copy_is_labels_not_paragraphs_and_claims_no_validation()
    {
        Assert.All(V2ShellText.English, pair =>
        {
            Assert.InRange(pair.Value.Length, 1, 120);
            Assert.DoesNotContain("validated", pair.Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("accessible", pair.Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("usability", pair.Value, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal("provisional - #265 not yet run", V2ShellText.Get("V2.Shell.Provisional"));
    }
}
