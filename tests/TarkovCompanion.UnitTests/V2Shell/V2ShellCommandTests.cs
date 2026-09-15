using System.Reflection;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Shell;

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

    [Theory]
    [InlineData("F", true, false, false, V2ShellCommandKind.FocusSearch)]
    [InlineData("F6", false, false, false, V2ShellCommandKind.NextRegion)]
    [InlineData("F6", false, false, true, V2ShellCommandKind.PreviousRegion)]
    [InlineData("L", true, false, false, V2ShellCommandKind.CopyAddress)]
    [InlineData("D", true, false, false, V2ShellCommandKind.TogglePin)]
    [InlineData("Escape", false, false, false, V2ShellCommandKind.CloseTransient)]
    public void Workflow_shortcuts_are_declared_commands(
        string key,
        bool control,
        bool alt,
        bool shift,
        V2ShellCommandKind expected)
    {
        var command = V2ShellCommands.Match(
            V2ShellCommands.For(V2ShellVariants.A),
            new(key, control, alt, shift),
            captureShortcutEnabled: true);

        Assert.Equal(expected, command?.Kind);
    }

    [Fact]
    public void Narrow_width_moves_variant_a_to_a_labelled_row_while_variant_b_always_uses_one()
    {
        Assert.Equal(V2WidthClass.Narrow, V2ShellAdaptation.Classify(599));
        Assert.Equal(V2WidthClass.Compact, V2ShellAdaptation.Classify(600));
        Assert.False(V2ShellAdaptation.UsesRail(V2ShellVariants.A, V2WidthClass.Compact));
        Assert.True(V2ShellAdaptation.UsesRail(V2ShellVariants.A, V2WidthClass.Standard));
        Assert.False(V2ShellAdaptation.UsesRail(V2ShellVariants.B, V2WidthClass.Expanded));
        Assert.False(V2ShellAdaptation.IntelFitsBeside(V2WidthClass.Compact));
        Assert.True(V2ShellAdaptation.IntelFitsBeside(V2WidthClass.Standard));
    }

    [Fact]
    public void Current_navigation_has_a_visible_non_colour_marker_and_a_textual_uia_state()
    {
        var destination = new V2ShellDestinationViewModel(
            new(V2Routes.Raid, "V2.Shell.Label.Raid"),
            _ => { });
        var section = new V2ShellSectionViewModel(
            V2RouteRegistry.Default[V2Routes.Loot],
            _ => { });

        destination.IsCurrent = true;
        section.IsCurrent = true;

        Assert.Equal("› Raid", destination.DisplayLabel);
        Assert.Equal("› Loot decision", section.DisplayLabel);
        Assert.Equal("Current page", destination.SelectionDescription);
        Assert.Equal("Current page", section.SelectionDescription);
    }

    [Fact]
    public void A_readiness_action_names_the_check_status_and_evidence_on_the_actual_button()
    {
        var action = new V2ReadinessCheckViewModel(
            new(
                "screenshots",
                "V2.Shell.Check.Screenshots",
                V2CheckStatus.NeedsAction,
                "No screenshot folder",
                V2Routes.Setup,
                Required: true),
            () => { });

        Assert.Equal("v2-shell-readiness-screenshots", action.AutomationId);
        Assert.Equal("Open Screenshot folder", action.ActionLabel);
        Assert.Equal("Open Screenshot folder. Needs action. No screenshot folder", action.AutomationName);
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
